using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.Core.Checkup;
using PcChecker.Core.Optimization;

namespace PcChecker.App.ViewModels;

/// <summary>"Fix selected" and the per-item "Fix": confirmation, one guarded batch through the owning services, results.</summary>
public sealed partial class CheckupViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private string? _resultMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private bool _resultSuccess;

    [ObservableProperty]
    private bool _rebootRequired;

    /// <summary>One line per item of the last fix run.</summary>
    public ObservableCollection<string> ResultLines { get; } = [];

    public bool HasResultLines => ResultLines.Count > 0;

    public bool ShowSuccessResult => ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    public bool ShowFailureResult => !ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    private bool CanFixSelected() => SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanFixSelected))]
    private Task FixSelectedAsync() => FixAsync(Items.Where(r => r.IsSelected).ToList());

    /// <summary>Fixes one item on its own (the row's Fix button), with the same confirmation.</summary>
    [RelayCommand]
    private Task FixItemAsync(CheckupItemRow? row) => row is null ? Task.CompletedTask : FixAsync([row]);

    [RelayCommand]
    private void DismissReboot() => RebootRequired = false;

    private async Task FixAsync(IReadOnlyList<CheckupItemRow> rows)
    {
        if (_report is not { } report || rows.Count == 0)
        {
            return;
        }

        var plan = CheckupBatchPlanner.Plan(rows.Select(r => r.Item), IsAdvancedMode, Settings.Current.RestorePoints.CreateBeforeChanges);
        if (plan.IsEmpty)
        {
            _notifications.Warning(Loc.T("Nothing to fix here"), Loc.T("The selected items need you: open their pages to deal with them."));
            return;
        }

        bool risky = plan.Steps.SelectMany(s => s.Items).Any(i => i.Risk != RiskLevel.Safe);
        var title = plan.ItemCount == 1 ? Loc.T("Fix this item?") : Loc.F("Fix {0} items?", plan.ItemCount);
        if (!await _dialogs.ConfirmAsync(title, ConfirmationText(plan), Loc.T("Fix"), isDanger: risky))
        {
            return;
        }

        // Driver installs and removals cannot stop part-way; then Cancel is hidden for the whole run.
        bool cancellable = plan.Steps.All(s => s.CanStopPartWay);
        await RunBusyAsync(async ct =>
        {
            try
            {
                var result = await _checkup.FixAsync(report, plan, ProgressReporter(), ct);
                ShowResult(result);
            }
            finally
            {
                OverallProgress = null;
            }
        }, Loc.T("Fixing…"), cancellable);
    }

    /// <summary>What will happen, step by step, plus the restore point and restart notes.</summary>
    private string ConfirmationText(CheckupFixPlan plan)
    {
        var text = new StringBuilder(Loc.T("PC Checker will, one after another:")).Append('\n');
        foreach (var item in plan.Steps.SelectMany(s => s.Items).Take(10))
        {
            // Name the change itself where it is not what the title says (a speed check finding fixed by one setting).
            text.Append("• ").Append(item.FixAction is { } action ? Loc.F("{0} (for: {1})", action, item.Title) : item.Title);
            if (item.Risk != RiskLevel.Safe)
            {
                text.Append(" (").Append(item.Risk == RiskLevel.Moderate ? Loc.T("Moderate") : Loc.T("Advanced")).Append(')');
            }

            text.Append('\n');
        }

        if (plan.ItemCount > 10)
        {
            text.Append(Loc.F("• and {0} more", plan.ItemCount - 10)).Append('\n');
        }

        if (plan.Skipped.Count > 0)
        {
            text.Append('\n').Append(Loc.F("{0} selected item(s) need you on their page and are left out.", plan.Skipped.Count)).Append('\n');
        }

        text.Append('\n').Append(plan.SharedRestorePoint
            ? Loc.T("One restore point is created first and covers every change, so you can go back.")
            : plan.OnlyLightChanges
                ? Loc.T("These changes do not need a restore point: deleted temporary files are recreated as needed and startup apps can be turned back on.")
                : Loc.T("Restore points are turned off in Settings, so none is created before these changes."));
        text.Append(' ').Append(Loc.T("Each change is recorded in the Action Journal (Logs page)."));
        if (plan.Steps.Any(s => s.Kind is CheckupFixKind.InstallDriverUpdates or CheckupFixKind.InstallWindowsUpdates))
        {
            text.Append("\n\n").Append(Loc.T("Installing updates accepts Microsoft's licence terms for them, as Windows Update does. The screen may flicker while a display driver installs."));
        }

        if (plan.MayNeedRestart)
        {
            text.Append("\n\n").Append(Loc.T("Some of these need a restart to finish. PC Checker does not restart on its own."));
        }

        if (!plan.Steps.All(s => s.CanStopPartWay))
        {
            text.Append("\n\n").Append(Loc.T("Driver changes cannot be stopped part-way, so this run cannot be cancelled once it starts."));
        }

        return text.ToString();
    }

    private void ShowResult(CheckupFixReport report)
    {
        ResultLines.Clear();
        foreach (var result in report.Results)
        {
            Items.FirstOrDefault(r => r.Id == result.Item.Id)?.SetResult(result);
            ResultLines.Add((result.Success ? Loc.T("Done") : Loc.T("Not done")) + ": " + result.Item.Title + ". " + result.Message);
        }

        if (report.RestorePoint.RestorePointSequence is { } sequence)
        {
            ResultLines.Add(Loc.F("All changes share restore point {0}.", sequence));
        }
        else if (report.RestorePoint.RestorePointWarning is { } warning)
        {
            ResultLines.Add(Loc.F("No shared restore point could be created ({0}). Each change followed its own rules; ones that need a restore point were not made.", warning));
        }

        RebootRequired |= report.RebootRequired;
        ResultSuccess = report.Failed == 0 && !report.Cancelled;
        ResultMessage = report.Cancelled
            ? Loc.F("Stopped: {0} of {1} done.", report.Succeeded, report.Results.Count)
            : report.Failed == 0
                ? (report.Succeeded == 1 ? Loc.T("The fix was applied.") : Loc.F("All {0} fixes were applied.", report.Succeeded))
                : Loc.F("{0} of {1} done. See the results below for the rest.", report.Succeeded, report.Results.Count);
        if (ResultSuccess)
        {
            _notifications.Success(Loc.T("Check-up fixes applied"), ResultMessage);
        }
        else
        {
            _notifications.Warning(Loc.T("Not everything was fixed"), ResultMessage);
        }

        UpdateSelection();
    }

    private void ClearResults()
    {
        ResultLines.Clear();
        ResultMessage = null;
    }
}
