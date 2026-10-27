using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.TaskManager;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The "Replace Task Manager" setting: when on, Ctrl+Shift+Esc and every other way of opening Task Manager opens PC
/// Checker on the Processes tab instead. Backed by the Image File Execution Options Debugger value (like Process
/// Explorer), it is only available from a Program Files install. The service is resolved lazily so this part needs no
/// change to the view model's constructor.
/// </summary>
public sealed partial class SettingsViewModel
{
    private ITaskManagerReplacementService? _taskManagerReplacement;
    private bool _suppressTaskManagerToggle;

    [ObservableProperty]
    private bool _isTaskManagerReplaced;

    [ObservableProperty]
    private bool _taskManagerReplacementEligible = true;

    [ObservableProperty]
    private string _taskManagerReplacementHint =
        "Open PC Checker on the Processes tab instead of Windows Task Manager (Ctrl+Shift+Esc and every other way).";

    private ITaskManagerReplacementService? TaskManagerReplacement =>
        _taskManagerReplacement ??= App.Services.GetService(typeof(ITaskManagerReplacementService)) as ITaskManagerReplacementService;

    /// <summary>Reads the current state from the registry and updates the toggle and its hint without re-triggering it.</summary>
    public void RefreshTaskManagerReplacement()
    {
        var status = TaskManagerReplacement?.GetStatus() ?? TaskManagerReplacementStatus.Off;
        _suppressTaskManagerToggle = true;
        IsTaskManagerReplaced = status.Enabled;
        _suppressTaskManagerToggle = false;

        TaskManagerReplacementEligible = status.Eligible && !status.PointsElsewhere;
        if (status.PointsElsewhere)
        {
            TaskManagerReplacementHint = Loc.T("Another program is set to open in place of Task Manager, so PC Checker will not change it.");
        }
        else if (status.IneligibleReason is { } reason)
        {
            TaskManagerReplacementHint = reason;
        }
        else
        {
            TaskManagerReplacementHint = Loc.T("Open PC Checker on the Processes tab instead of Windows Task Manager (Ctrl+Shift+Esc and every other way).");
        }
    }

    partial void OnIsTaskManagerReplacedChanged(bool value)
    {
        if (_suppressTaskManagerToggle || TaskManagerReplacement is not { } service)
        {
            return;
        }

        _ = ApplyTaskManagerReplacementAsync(service, value);
    }

    private async Task ApplyTaskManagerReplacementAsync(ITaskManagerReplacementService service, bool enable)
    {
        var result = enable
            ? await service.EnableAsync(CancellationToken.None)
            : await service.DisableAsync(CancellationToken.None);

        if (result.Success)
        {
            _notifications.Success(Loc.T("Task Manager"), result.Message);
        }
        else
        {
            _notifications.Error(Loc.T("Task Manager"), result.Message);
        }

        // Re-read the real state, so a refused change puts the toggle back.
        RefreshTaskManagerReplacement();
    }
}
