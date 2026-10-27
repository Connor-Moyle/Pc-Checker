using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.WindowsUpdate;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The Roll back tab of the Windows Update page: installed updates with Uninstall, hidden updates with Show again,
/// hiding a pending or just-removed update, and going back to the previous version of Windows (with a longer window).
/// Every change is confirmed first and runs through the change guard in the service.
/// </summary>
public sealed partial class WindowsUpdateViewModel
{
    /// <summary>Index of the Roll back tab in the page's tab strip (Updates, History, Pause, Roll back).</summary>
    public const int RollbackTabIndex = 3;

    public const string RecoveryUri = "ms-settings:recovery";

    private const string RemovedRestartReason = "to finish removing the update";
    private const string GoBackRestartReason = "to go back to the previous version of Windows";
    private const string EarlierChangesRestartReason = "to finish changes Windows is waiting on";

    private RollbackWindow? _rollbackWindow;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRollbackNotLoaded))]
    private bool _rollbackLoaded;

    [ObservableProperty]
    private string _installedSummary = Loc.T("Not read yet");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRollbackWarnings))]
    private string? _rollbackWarnings;

    [ObservableProperty]
    private string _rollbackTitle = Loc.T("Not checked yet");

    [ObservableProperty]
    private string _rollbackMessage = string.Empty;

    [ObservableProperty]
    private string _rollbackDaysText = "—";

    [ObservableProperty]
    private string? _rollbackDeadlineText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    [NotifyPropertyChangedFor(nameof(ShowGoBackControls))]
    private bool _canGoBack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExtendWindowCommand))]
    private RollbackWindowOption? _selectedWindowOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExtendWindow))]
    private IReadOnlyList<RollbackWindowOption> _windowOptions = [];

    [ObservableProperty]
    private bool _rollbackRestartPending;

    [ObservableProperty]
    private string _rollbackRestartMessage = string.Empty;

    [ObservableProperty]
    private string _rollbackRestartReason = RemovedRestartReason;

    public ObservableCollection<InstalledUpdateRow> InstalledUpdates { get; } = [];

    public ObservableCollection<HiddenUpdateRow> HiddenUpdates { get; } = [];

    public bool HasInstalledUpdates => InstalledUpdates.Count > 0;

    public bool HasHiddenUpdates => HiddenUpdates.Count > 0;

    public bool ShowRollbackNotLoaded => !RollbackLoaded;

    public bool HasRollbackWarnings => !string.IsNullOrWhiteSpace(RollbackWarnings);

    public bool ShowGoBackControls => CanGoBack;

    public bool CanExtendWindow => CanGoBack && WindowOptions.Count > 0;

    public string HiddenTabText => HiddenUpdates.Count == 0 ? Loc.T("No hidden updates") : Loc.F("{0} hidden", HiddenUpdates.Count);

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == RollbackTabIndex && !RollbackLoaded && !IsBusy)
        {
            _ = RunBusyAsync(LoadRollbackCoreAsync, "Reading installed updates… This can take a minute.");
        }
    }

    [RelayCommand]
    private Task LoadRollbackAsync() => RunBusyAsync(LoadRollbackCoreAsync, "Reading installed updates… This can take a minute.");

    [RelayCommand]
    private async Task UninstallUpdateAsync(InstalledUpdateRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!row.CanUninstall)
        {
            await _dialogs.ShowMessageAsync("This update cannot be removed", row.Explanation);
            return;
        }

        if (!await _dialogs.ConfirmAsync(Loc.F("Uninstall {0}?", row.Kb), UninstallConfirmation(row), "Uninstall", isDanger: true))
        {
            return;
        }

        OperationResult? result = null;
        await RunBusyAsync(async _ =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
            result = await _rollback.UninstallAsync(row.Update, progress, CancellationToken.None);
            ShowRollbackResult(result);
            if (result.RebootRequired)
            {
                // A failure that needs a restart means earlier changes are waiting; restarting first lets a retry work.
                AskForRestart(result.Success
                    ? Loc.T("Windows needs a restart to finish removing the update. Save your work first.")
                    : Loc.T("Windows has changes waiting for a restart. Restart, then try removing the update again."),
                    result.Success ? RemovedRestartReason : EarlierChangesRestartReason);
            }
        }, Loc.F("Removing {0}… This can take several minutes.", row.Kb), cancellable: false);

        if (result is { Success: true }
            && await _dialogs.ConfirmAsync("Hide this update too?",
                $"Windows Update may install {row.Kb} again the next time it checks. Hide it so it stays off this PC until you show it again (Roll back → Hidden updates)?\n\n"
                + "If Windows Update does not list it until after the restart, hide it then from the Updates tab.",
                "Hide"))
        {
            await RunHideAsync(new UpdateHideTarget(row.Update.UpdateId, row.Kb, row.Title));
        }

        await RunBusyAsync(LoadRollbackCoreAsync, "Reading installed updates…");
    }

    [RelayCommand]
    private async Task HideInstalledAsync(InstalledUpdateRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Hide this update?",
                $"Windows Update will not offer or install {row.Kb} while it is hidden. This only matters once the update is removed; show it again any time under Hidden updates.",
                "Hide"))
        {
            return;
        }

        await RunHideAsync(new UpdateHideTarget(row.Update.UpdateId, row.Kb, row.Title));
    }

    [RelayCommand]
    private async Task HidePendingAsync(PendingUpdateRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Hide this update?",
                $"Windows Update will not offer or install \"{row.Title}\" until you show it again (Roll back → Hidden updates).\n\n"
                + "Any fixes in it stay missing while it is hidden, so only hide an update that causes a problem. This is the same as Microsoft's \"Show or hide updates\" tool.",
                "Hide"))
        {
            return;
        }

        if (await RunHideAsync(new UpdateHideTarget(row.Update.Id, row.Update.KbArticle, row.Update.Title)))
        {
            Updates.Remove(row);
            UpdateCount = Updates.Count;
            PendingCountText = UpdateCount.ToString(CultureInfo.CurrentCulture);
            UpdateSelection();
        }
    }

    [RelayCommand]
    private async Task UnhideAsync(HiddenUpdateRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Show this update again?",
                $"Windows Update will offer \"{row.Title}\" again and may install it the next time it checks.", "Show again"))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _rollback.UnhideAsync(row.Update, ct);
            ShowRollbackResult(result);
            await LoadHiddenAsync(CancellationToken.None);
        }, "Showing the update again…");
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task GoBackAsync()
    {
        var window = _rollbackWindow;
        if (window is not { State: RollbackState.Available })
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Go back to the previous version of Windows?", GoBackWarning(window), "Continue", isDanger: true))
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Have you backed up your files?",
                "Last check. PC Checker now prepares Windows to go back. Windows does it while the PC restarts, which takes a while and cannot be stopped once it starts.\n\n"
                + "Save and close everything, and make sure the PC stays plugged in.",
                "I have backed up, go back", isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async _ =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
            var result = await _rollback.GoBackAsync(progress, CancellationToken.None);
            ShowRollbackResult(result);
            if (result.Success)
            {
                AskForRestart(Loc.T("Windows goes back to the previous version when it restarts. Save your work, then restart."), GoBackRestartReason);
            }

            await LoadWindowAsync(CancellationToken.None);
        }, "Preparing to go back to the previous version of Windows…", cancellable: false);
    }

    [RelayCommand(CanExecute = nameof(CanRunExtend))]
    private async Task ExtendWindowAsync()
    {
        if (SelectedWindowOption is not { } option || _rollbackWindow is not { } window)
        {
            return;
        }

        var until = window.UpgradedOn is { } upgraded
            ? upgraded.AddDays(option.Days).ToLocalTime().ToString("D", CultureInfo.CurrentCulture)
            : null;
        var message = (until is null
                ? $"Going back stays possible for {option.Days} days after the upgrade."
                : $"Going back stays possible until {until}.")
            + "\n\nThe previous version stays in the Windows.old folder until then and keeps using that disk space. "
            + "Storage Sense, Disk Cleanup or removing Windows.old ends the option sooner.";
        if (!await _dialogs.ConfirmAsync("Keep the option to go back longer?", message, "Extend"))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _rollback.SetRollbackWindowAsync(option.Days, ct);
            ShowRollbackResult(result);
            await LoadWindowAsync(CancellationToken.None);
        }, "Changing the window to go back…", cancellable: false);
    }

    private bool CanRunExtend() => CanGoBack && SelectedWindowOption is not null;

    [RelayCommand]
    private void OpenDrivers() => _navigation.Navigate(typeof(DriversPage));

    [RelayCommand]
    private void OpenRecoverySettings() => Open(RecoveryUri);

    [RelayCommand]
    private void DismissRollbackRestart() => RollbackRestartPending = false;

    private async Task LoadRollbackCoreAsync(CancellationToken ct)
    {
        var installed = await _rollback.GetInstalledUpdatesAsync(ct);
        InstalledUpdates.Clear();
        foreach (var update in installed.Updates)
        {
            InstalledUpdates.Add(new InstalledUpdateRow(update));
        }

        int removable = InstalledUpdates.Count(r => r.CanUninstall);
        InstalledSummary = InstalledUpdates.Count == 0
            ? Loc.T("No installed updates found")
            : Loc.F("{0} installed · {1} can be removed", InstalledUpdates.Count, removable);
        OnPropertyChanged(nameof(HasInstalledUpdates));

        var hidden = await LoadHiddenAsync(ct);
        var window = await LoadWindowAsync(ct);
        RollbackWarnings = Join(installed.Warnings.Concat(hidden.Warnings).Concat(window.Warnings));
        RollbackLoaded = true;
    }

    private async Task<HiddenUpdateList> LoadHiddenAsync(CancellationToken ct)
    {
        var hidden = await _rollback.GetHiddenUpdatesAsync(ct);
        HiddenUpdates.Clear();
        foreach (var update in hidden.Updates)
        {
            HiddenUpdates.Add(new HiddenUpdateRow(update));
        }

        OnPropertyChanged(nameof(HasHiddenUpdates));
        OnPropertyChanged(nameof(HiddenTabText));
        return hidden;
    }

    private async Task<RollbackWindow> LoadWindowAsync(CancellationToken ct)
    {
        var window = await _rollback.GetRollbackWindowAsync(ct);
        _rollbackWindow = window;
        RollbackMessage = window.Message;
        RollbackTitle = window.State switch
        {
            RollbackState.Available => Loc.T("You can go back"),
            RollbackState.Expired => Loc.T("The time to go back has run out"),
            RollbackState.NotAvailable => Loc.T("Nothing to go back to"),
            _ => Loc.T("Could not tell"),
        };
        RollbackDaysText = window.DaysLeft is { } left && window.State == RollbackState.Available
            ? (left == 1 ? Loc.T("1 day left") : Loc.F("{0} days left", left))
            : window.State == RollbackState.Available ? Loc.T("Available") : "—";
        RollbackDeadlineText = window.State == RollbackState.Available && window.Deadline is { } deadline
            ? Loc.F("Until {0}", deadline.ToLocalTime().ToString("D", CultureInfo.CurrentCulture))
            : null;
        CanGoBack = window.State == RollbackState.Available;
        WindowOptions = RollbackRules.ExtensionOptions(window.WindowDays, window.DaysSinceUpgrade)
            .Select(d => new RollbackWindowOption(d, Loc.F("{0} days", d)))
            .ToList();
        SelectedWindowOption = WindowOptions.LastOrDefault();
        return window;
    }

    private async Task<bool> RunHideAsync(UpdateHideTarget target)
    {
        bool ok = false;
        await RunBusyAsync(async ct =>
        {
            var result = await _rollback.HideAsync(target, ct);
            ok = result.Success;
            ShowRollbackResult(result);
            await LoadHiddenAsync(CancellationToken.None);
        }, "Hiding the update… Windows Update may take a minute to answer.");
        return ok;
    }

    private void ShowRollbackResult(OperationResult result)
    {
        ResultLines.Clear();
        ResultSuccess = result.Success;
        ResultMessage = result.Message;
        if (result.Success)
        {
            _notifications.Success("Done", result.Message);
        }
        else
        {
            _notifications.Warning("That did not work", result.Message.Split('\n')[0]);
        }
    }

    private void AskForRestart(string message, string reason)
    {
        RollbackRestartMessage = message;
        RollbackRestartReason = reason;
        RollbackRestartPending = true;
    }

    private static string UninstallConfirmation(InstalledUpdateRow row)
    {
        var text = new StringBuilder($"PC Checker will remove \"{row.Title}\" ({row.Plan.Method switch { UninstallMethod.DismPackage => "with DISM", _ => "with wusa" }}). A restore point is created first.\n\n");
        if (row.Update.Type is InstalledUpdateType.Cumulative or InstalledUpdateType.Security)
        {
            text.Append("This takes away the security fixes in it, so only remove it if it causes a problem.\n\n");
        }

        if (row.Plan.Method == UninstallMethod.DismPackage)
        {
            text.Append("This is the monthly cumulative update; the servicing stack update that came with it stays, because Windows cannot remove that part.\n\n");
        }

        text.Append("It can take several minutes and cannot be stopped once it starts. Windows usually needs a restart to finish. "
            + "Windows Update may install it again later; you can hide it afterwards so it does not.");
        return text.ToString();
    }

    private static string GoBackWarning(RollbackWindow window)
    {
        var text = new StringBuilder("Windows replaces this version with the one it had before the last feature update. It takes a while and restarts the PC, possibly more than once.\n\n");
        text.Append("• Back up your files first. They are normally kept, but going back replaces Windows itself.\n");
        text.Append("• Apps you installed and settings you changed since the upgrade may be gone and need reinstalling.\n");
        text.Append("• You need the password you used to sign in to the previous version.\n");
        text.Append("• This cannot be undone here. You can install the newer version again later from Windows Update.\n");
        if (window.DaysLeft is { } left)
        {
            text.Append(left == 1 ? "\nGoing back is possible for 1 more day." : $"\nGoing back is possible for {left} more days.");
        }

        text.Append("\n\nYou will be asked once more before anything starts.");
        return text.ToString();
    }

    private static string? Join(IEnumerable<string> lines)
    {
        var list = lines.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct().ToList();
        return list.Count == 0 ? null : string.Join("\n", list);
    }
}
