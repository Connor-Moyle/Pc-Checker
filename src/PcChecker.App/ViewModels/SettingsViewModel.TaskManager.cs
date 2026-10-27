using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.TaskManager;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The "Replace Task Manager" setting: when on, Ctrl+Shift+Esc and every other way of opening Task Manager opens PC
/// Checker on the Processes tab instead. Backed by the Image File Execution Options Debugger value (like Process
/// Explorer), it is only available from a Program Files install. Every change is confirmed first. The service is
/// resolved lazily so this part needs no change to the view model's constructor.
/// </summary>
public sealed partial class SettingsViewModel
{
    private ITaskManagerReplacementService? _taskManagerReplacement;
    private bool _suppressTaskManagerToggle;
    private bool _applyingTaskManagerToggle;

    [ObservableProperty]
    private bool _isTaskManagerReplaced;

    [ObservableProperty]
    private bool _taskManagerReplacementEligible = true;

    [ObservableProperty]
    private string _taskManagerReplacementHint = string.Empty;

    /// <summary>The value points at another PC Checker copy that still exists: show "Open this copy".</summary>
    [ObservableProperty]
    private bool _taskManagerPointsElsewhere;

    private ITaskManagerReplacementService? TaskManagerReplacement =>
        _taskManagerReplacement ??= App.Services.GetService(typeof(ITaskManagerReplacementService)) as ITaskManagerReplacementService;

    /// <summary>Reads the current state from the registry and updates the toggle and its hint without re-triggering it.</summary>
    public void RefreshTaskManagerReplacement()
    {
        var status = TaskManagerReplacement?.GetStatus() ?? TaskManagerReplacementStatus.Off;
        _suppressTaskManagerToggle = true;
        IsTaskManagerReplaced = status.Enabled;
        _suppressTaskManagerToggle = false;

        // Turning it off is always allowed; turning it on needs a protected install folder.
        TaskManagerReplacementEligible = status.Eligible || status.Enabled;
        TaskManagerPointsElsewhere = status.PointsElsewhere;
        if (status.PointsElsewhere)
        {
            TaskManagerReplacementHint = Loc.F("Task Manager opens another copy of PC Checker: {0}. Choose \"Open this copy\" to fix it.", status.CurrentTarget ?? string.Empty);
        }
        else if (!status.Eligible && !status.Enabled && status.IneligibleReason is { } reason)
        {
            TaskManagerReplacementHint = reason;
        }
        else if (status.Enabled)
        {
            TaskManagerReplacementHint = Loc.T("On: Task Manager opens PC Checker on the Processes tab. Windows Task Manager opens only while this is off; use \"Open Windows Task Manager\" on the Processes page.");
        }
        else if (status.ThirdPartyPresent)
        {
            TaskManagerReplacementHint = Loc.F("Another program opens in place of Task Manager now ({0}). Turning this on replaces it and puts it back when turned off.", status.CurrentTarget ?? string.Empty);
        }
        else
        {
            TaskManagerReplacementHint = Loc.T("Open PC Checker on the Processes tab instead of Windows Task Manager (Ctrl+Shift+Esc and every other way).");
        }
    }

    partial void OnIsTaskManagerReplacedChanged(bool value)
    {
        if (_suppressTaskManagerToggle || _applyingTaskManagerToggle || TaskManagerReplacement is not { } service)
        {
            return;
        }

        _ = ApplyTaskManagerReplacementAsync(service, value);
    }

    private async Task ApplyTaskManagerReplacementAsync(ITaskManagerReplacementService service, bool enable)
    {
        _applyingTaskManagerToggle = true;
        try
        {
            var status = service.GetStatus();
            if (enable && status.PointsElsewhere && status.CurrentTarget is { } other)
            {
                await TaskManagerReplacementPrompts.OfferRepointAsync(service, _dialogs, _notifications, other);
            }
            else
            {
                await TaskManagerReplacementPrompts.SetAsync(service, _dialogs, _notifications, enable);
            }
        }
        finally
        {
            _applyingTaskManagerToggle = false;

            // Re-read the real state, so a cancelled or refused change puts the toggle back.
            RefreshTaskManagerReplacement();
        }
    }

    /// <summary>Points a value left by another PC Checker copy at this one (or turns it off).</summary>
    [RelayCommand]
    private async Task RepointTaskManagerReplacement()
    {
        if (TaskManagerReplacement is not { } service || service.GetStatus().CurrentTarget is not { } other)
        {
            return;
        }

        await TaskManagerReplacementPrompts.OfferRepointAsync(service, _dialogs, _notifications, other);
        RefreshTaskManagerReplacement();
    }
}
