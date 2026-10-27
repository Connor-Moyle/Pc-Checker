using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.Core.TaskManager;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The "Replace Task Manager" toggle shown in the Processes page More menu. The real setting lives in Settings and
/// in the registry; this mirrors it so a Task Manager user can turn it on from where they expect it. The service is
/// resolved lazily, so the constructor is unchanged.
/// </summary>
public sealed partial class ProcessesViewModel
{
    private ITaskManagerReplacementService? _taskManagerReplacement;
    private bool _suppressTaskManagerToggle;

    [ObservableProperty]
    private bool _isTaskManagerReplaced;

    [ObservableProperty]
    private bool _taskManagerReplacementEligible = true;

    private ITaskManagerReplacementService? TaskManagerReplacement =>
        _taskManagerReplacement ??= App.Services.GetService(typeof(ITaskManagerReplacementService)) as ITaskManagerReplacementService;

    /// <summary>Reads the current state so the menu's checkmark is right; called when the page is shown.</summary>
    public void RefreshTaskManagerReplacement()
    {
        var status = TaskManagerReplacement?.GetStatus() ?? TaskManagerReplacementStatus.Off;
        _suppressTaskManagerToggle = true;
        IsTaskManagerReplaced = status.Enabled;
        _suppressTaskManagerToggle = false;
        TaskManagerReplacementEligible = status.Eligible && !status.PointsElsewhere;
    }

    [RelayCommand]
    private async Task ToggleReplaceTaskManager()
    {
        if (_suppressTaskManagerToggle || TaskManagerReplacement is not { } service)
        {
            return;
        }

        var status = service.GetStatus();
        if (!status.Enabled && (!status.Eligible || status.PointsElsewhere))
        {
            await _dialogs.ShowMessageAsync(Loc.T("Replace Task Manager"),
                status.IneligibleReason ?? Loc.T("This option is not available right now."));
            RefreshTaskManagerReplacement();
            return;
        }

        var result = status.Enabled
            ? await service.DisableAsync(CancellationToken.None)
            : await service.EnableAsync(CancellationToken.None);

        if (result.Success)
        {
            _notifications.Success(Loc.T("Task Manager"), result.Message);
        }
        else
        {
            _notifications.Error(Loc.T("Task Manager"), result.Message);
        }

        RefreshTaskManagerReplacement();
    }
}
