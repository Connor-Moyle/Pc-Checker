using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.Leftovers;
using PcChecker.Core.Software;

namespace PcChecker.App.ViewModels;

/// <summary>Actions of the Software page: uninstall, bundle clean-up, size measurement, CSV export, links and winget.</summary>
public sealed partial class SoftwareViewModel
{
    [ObservableProperty]
    private string? _wingetStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWingetUpgrades))]
    private int _wingetUpgradeCount;

    public bool HasWingetUpgrades => WingetUpgradeCount > 0;

    /// <summary>The program uninstalled last, while the page offers to look for what it left behind.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLeftoverOffer))]
    [NotifyPropertyChangedFor(nameof(LeftoverOfferText))]
    private LeftoverFocus? _leftoverOffer;

    public bool HasLeftoverOffer => LeftoverOffer is not null;

    public string LeftoverOfferText => LeftoverOffer is null
        ? string.Empty
        : Loc.F("{0} was removed. Programs often leave folders, settings and tasks behind.", LeftoverOffer.ProgramName);

    /// <summary>Opens the Leftovers page and looks for what the program uninstalled last left behind.</summary>
    [RelayCommand]
    private void FindLeftovers()
    {
        var focus = LeftoverOffer;
        LeftoverOffer = null;
        if (focus is not null)
        {
            _leftovers.Enqueue(focus);
        }

        _navigation.Navigate(typeof(LeftoversPage));
    }

    [RelayCommand]
    private void DismissLeftoverOffer() => LeftoverOffer = null;

    [RelayCommand]
    private async Task UninstallAsync(ProgramRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!row.CanUninstall)
        {
            await _dialogs.ShowMessageAsync(Loc.F("Cannot uninstall {0}", row.Name), row.UninstallReason);
            return;
        }

        bool quiet = QuietUninstall && row.SupportsQuietUninstall;
        bool allUsers = row.IsStoreApp && IsAdvancedMode && RemoveStoreAppsForAllUsers;
        var mode = row.IsStoreApp
            ? allUsers
                ? Loc.T("The Store app will be removed with Windows' package manager for ALL user accounts on this PC (\"Remove Store apps for all users\" is on); no installer window appears.")
                : Loc.T("The Store app will be removed with Windows' package manager from your account only; other accounts on this PC keep it. No installer window appears.")
            : quiet
                ? Loc.T("Quiet uninstall is on: the program's own uninstaller runs with its silent switches, so you will not see its windows. Some uninstallers ignore this and show prompts anyway.")
                : QuietUninstall
                    ? Loc.T("This program's uninstaller has no known silent switch, so its window will open and you follow its prompts.")
                    : Loc.T("Quiet uninstall is off: the program's own uninstaller opens and you follow its prompts.");
        var restore = Settings.Current.RestorePoints.CreateBeforeChanges
            ? Loc.T("A restore point is created first (per your settings).")
            : Loc.T("No restore point will be created (per your settings).");
        var question = row.Publisher == "—"
            ? Loc.F("Remove {0} from this PC?", row.Name)
            : Loc.F("Remove {0} by {1} from this PC?", row.Name, row.Publisher);
        var message = $"{question}\n\n{mode}\n\n{restore}\n\n" + Loc.T("This cannot be undone from PC Checker; you would need to reinstall it from the vendor.");
        if (!row.IsStoreApp && UninstallCommandBuilder.Build(row.Program, quiet) is { } command
            && UninstallElevationRules.UnelevatedReason(row.Program, command.FileName) is { } unelevated)
        {
            message += "\n\n" + Loc.F("Its uninstaller runs with your normal rights, not as administrator. {0} If it needs more, Windows asks you first.", Loc.T(unelevated));
        }

        if (row.IsProtected)
        {
            message += "\n\n" + Loc.F("This program is protected: {0} Advanced mode allows the override, but only continue if you are sure.", row.ProtectionReason);
        }

        if (!await _dialogs.ConfirmAsync(Loc.F("Uninstall {0}", row.Name), message, Loc.T("Uninstall"), isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Detail is null ? p.Stage + "…" : $"{p.Stage}…\n{p.Detail}");
            var result = await _uninstaller.UninstallAsync(row.Program, new UninstallOptions(quiet, allUsers), progress, ct);
            ReportResult(result, Loc.T("Uninstall"));
            if (result.Success)
            {
                // Store apps are removed whole by Windows; for other programs offer a look for what the uninstaller left.
                LeftoverOffer = row.IsStoreApp
                    ? null
                    : new LeftoverFocus(row.Program.Name, row.Program.Publisher, row.Program.InstallLocation, row.Program.Id);
                ApplyScan(await _inventory.ScanAsync(progress, ct));
            }
        }, Loc.F("Uninstalling {0}…", row.Name), cancellable: false);
    }

    [RelayCommand]
    private async Task UninstallExtrasAsync(BundleRow? bundle)
    {
        if (bundle is null || !bundle.HasRemovable)
        {
            return;
        }

        var names = string.Join("\n • ", bundle.Removable.Select(c => c.Name));
        var message = Loc.F("These were installed alongside {0} ({1}):", bundle.ParentName, bundle.Reason) + $"\n\n • {names}\n\n"
            + (QuietUninstall
                ? Loc.F("{0} itself stays. Each extra is removed with its own uninstaller quietly where possible.", bundle.ParentName)
                : Loc.F("{0} itself stays. Each extra is removed with its own uninstaller; follow each uninstaller's prompts.", bundle.ParentName))
            + "\n\n" + Loc.T("This cannot be undone from PC Checker.");
        if (!await _dialogs.ConfirmAsync(Loc.T("Uninstall the extras?"), message, Loc.T("Uninstall extras"), isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            int ok = 0, failed = 0;
            foreach (var child in bundle.Removable)
            {
                ct.ThrowIfCancellationRequested();
                BusyText = Loc.F("Uninstalling {0}…", child.Name);
                var verdict = _uninstaller.CanUninstall(child.Program);
                if (!verdict.CanUninstall)
                {
                    failed++;
                    _notifications.Warning(Loc.F("Skipped {0}", child.Name), verdict.Reason);
                    continue;
                }

                var result = await _uninstaller.UninstallAsync(child.Program, QuietUninstall, null, ct);
                if (result.Success)
                {
                    ok++;
                }
                else
                {
                    failed++;
                    _notifications.Error(Loc.F("Could not uninstall {0}", child.Name), result.Message);
                }
            }

            _notifications.Show(Loc.T("Bundle clean-up finished"), Loc.F("{0} removed, {1} skipped or failed.", ok, failed), failed == 0 ? NotificationKind.Success : NotificationKind.Warning);
            ApplyScan(await _inventory.ScanAsync(null, ct));
        }, Loc.T("Uninstalling bundled extras…"));
    }

    /// <summary>
    /// Measures install folders for entries whose installer recorded no size. Entries that already have one are left
    /// alone: a measured parent folder (say a vendor's whole "NVIDIA Corporation" folder) would double-count nested
    /// installs and inflate the total. The results live in the view model so export and later visits keep them.
    /// </summary>
    [RelayCommand]
    private Task CalculateSizesAsync() => RunBusyAsync(async ct =>
    {
        var current = _current;
        if (current is null)
        {
            return;
        }

        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Detail is null ? p.Stage + "…" : $"{p.Stage}…\n{p.Detail}");
        var updated = await DirectorySizeCalculator.CalculateAsync(current.Programs, onlyMissing: true, progress, ct);
        var before = current.Programs.ToDictionary(p => p.Id, p => p.SizeBytes, StringComparer.OrdinalIgnoreCase);
        int measured = 0;
        foreach (var program in updated)
        {
            if (program.SizeBytes is { } size and > 0 && before.TryGetValue(program.Id, out var old) && old is null or 0)
            {
                _measuredSizes[program.Id] = size;
                measured++;
            }
        }

        int stillUnknown = updated.Count(p => p.SizeBytes is null or 0);
        ApplyInventory(current with { Programs = updated, Bundles = RelinkBundles(current.Bundles, updated) });
        _notifications.Info(Loc.T("Sizes measured"), measured > 0
            ? Loc.F("{0} more programs now have a size.", measured) + (stillUnknown > 0 ? " " + Loc.F("{0} have no install folder to measure.", stillUnknown) : string.Empty)
            : stillUnknown > 0
                ? Loc.T("No new sizes: the remaining entries have no install folder to measure.")
                : Loc.T("Every entry already has a size."));
    }, Loc.T("Measuring install folders…"));

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var current = _current;
        if (current is null || current.Programs.Count == 0)
        {
            _notifications.Warning(Loc.T("Nothing to export"), Loc.T("Scan for installed programs first."));
            return;
        }

        var path = await _dialogs.SaveFileAsync(Loc.T("Export installed programs"), $"installed-programs-{DateTime.Now:yyyyMMdd}.csv", "CSV files|*.csv|All files|*.*");
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, SoftwareCsvExporter.Build(current.Programs), System.Text.Encoding.UTF8);
            _notifications.Success(Loc.T("Exported"), Loc.F("{0} entries written to {1}.", current.Programs.Count, Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CSV export failed.");
            ErrorMessage = Loc.T("The CSV file could not be written:") + " " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenInstallFolder(ProgramRow? row)
    {
        if (row?.Location is null)
        {
            return;
        }

        var result = ShellLauncher.Open(row.Location);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Folder not opened"), result.Message);
        }
    }

    [RelayCommand]
    private void OpenPublisherPage(ProgramRow? row)
    {
        var url = row?.Program.AboutUrl ?? row?.Program.HelpLink;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        var result = ShellLauncher.Open(url);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Page not opened"), result.Message);
        }
    }

    [RelayCommand]
    private Task CheckWingetUpgradesAsync() => RunBusyAsync(async ct =>
    {
        WingetUpgradeResult result;
        if (AppOptions.Mode == AppMode.Demo)
        {
            await DemoData.DelayAsync(ct, 300, 800);
            result = new WingetUpgradeResult(true, WingetListParser.UpgradesOnly(WingetListParser.Parse(DemoWingetOutput)), null);
        }
        else
        {
            result = await WingetUpgradeChecker.CheckAsync(ct);
        }

        DispatcherHelper.RunOnUi(() =>
        {
            WingetUpgrades.Clear();
            foreach (var entry in result.Upgrades)
            {
                WingetUpgrades.Add(entry);
            }

            WingetUpgradeCount = WingetUpgrades.Count;
            WingetStatus = result.Note ?? (result.Upgrades.Count == 1 ? Loc.T("1 program has a newer version available.") : Loc.F("{0} programs have newer versions available.", result.Upgrades.Count));
        });
    }, Loc.T("Asking winget for available upgrades…"));

    [RelayCommand]
    private void OpenWingetUpgradeHelp()
    {
        var result = ShellLauncher.Open("https://learn.microsoft.com/windows/package-manager/winget/upgrade");
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Page not opened"), result.Message);
        }
    }

    private void ReportResult(OperationResult result, string action)
    {
        if (result.Success)
        {
            _notifications.Show(Loc.F("{0} finished", Loc.T(action)), result.Message + (result.RebootRequired ? " " + Loc.T("A restart is needed to finish.") : string.Empty),
                result.RebootRequired ? NotificationKind.Warning : NotificationKind.Success);
        }
        else
        {
            _logger.LogWarning("{Action} failed: {Message}", action, result.Message);
            ErrorMessage = result.Message;
        }
    }

    private static IReadOnlyList<BundleGroup> RelinkBundles(IReadOnlyList<BundleGroup> bundles, IReadOnlyList<InstalledProgram> programs)
    {
        var byId = programs.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        return bundles
            .Where(b => byId.ContainsKey(b.Parent.Id))
            .Select(b => new BundleGroup(byId[b.Parent.Id], b.Children.Where(c => byId.ContainsKey(c.Id)).Select(c => byId[c.Id]).ToList(), b.Reason))
            .ToList();
    }

    private const string DemoWingetOutput =
        "Name                        Id                          Version        Available      Source\n" +
        "-------------------------------------------------------------------------------------------------\n" +
        "Discord                     Discord.Discord             1.0.9170       1.0.9175       winget\n" +
        "OBS Studio                  OBSProject.OBSStudio        30.2.3         30.2.4         winget\n" +
        "VLC media player            VideoLAN.VLC                3.0.21         3.0.21         winget\n" +
        "Notepad++ (64-bit x64)      Notepad++.Notepad++         8.7            8.7.1          winget\n" +
        "Git                         Git.Git                     2.47.0         2.47.1         winget\n" +
        "4 upgrades available.\n";
}
