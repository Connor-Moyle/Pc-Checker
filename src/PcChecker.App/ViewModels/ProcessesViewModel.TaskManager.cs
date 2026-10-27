using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.TaskManager;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The "Replace Task Manager" toggle and "Open Windows Task Manager" in the Processes page More menu. The real setting
/// lives in Settings and in the registry; this mirrors it so a Task Manager user can change it from where they expect
/// it. The service is resolved lazily, so the constructor is unchanged.
/// </summary>
public sealed partial class ProcessesViewModel
{
    private ITaskManagerReplacementService? _taskManagerReplacement;

    [ObservableProperty]
    private bool _isTaskManagerReplaced;

    [ObservableProperty]
    private bool _taskManagerReplacementEligible = true;

    private ITaskManagerReplacementService? TaskManagerReplacement =>
        _taskManagerReplacement ??= App.Services.GetService(typeof(ITaskManagerReplacementService)) as ITaskManagerReplacementService;

    /// <summary>Reads the current state so the menu's checkmark is right; called when the page is shown and after every change.</summary>
    public void RefreshTaskManagerReplacement()
    {
        var status = TaskManagerReplacement?.GetStatus() ?? TaskManagerReplacementStatus.Off;
        IsTaskManagerReplaced = status.Enabled;
        TaskManagerReplacementEligible = status.Eligible;

        // The menu item is not checkable (so a refused change cannot leave a wrong checkmark); re-raise so the bound
        // checkmark is redrawn from the real state even when the value did not change.
        OnPropertyChanged(nameof(IsTaskManagerReplaced));
    }

    [RelayCommand]
    private async Task ToggleReplaceTaskManager()
    {
        if (TaskManagerReplacement is not { } service)
        {
            return;
        }

        var status = service.GetStatus();
        try
        {
            if (status.PointsElsewhere && !status.Enabled && status.CurrentTarget is { } other)
            {
                await TaskManagerReplacementPrompts.OfferRepointAsync(service, _dialogs, _notifications, other);
            }
            else
            {
                await TaskManagerReplacementPrompts.SetAsync(service, _dialogs, _notifications, !status.Enabled);
            }
        }
        finally
        {
            RefreshTaskManagerReplacement();
        }
    }

    /// <summary>
    /// Opens Windows Task Manager. While the replacement is on, Windows would start PC Checker again, so this offers to
    /// turn the option off first (there is no public way to skip the redirect).
    /// </summary>
    [RelayCommand]
    private async Task OpenWindowsTaskManager()
    {
        var service = TaskManagerReplacement;
        var status = service?.GetStatus() ?? TaskManagerReplacementStatus.Off;
        if (service is not null && (status.Enabled || status.PointsElsewhere))
        {
            await TaskManagerReplacementPrompts.TurnOffAndOpenWindowsTaskManagerAsync(service, _dialogs, _notifications);
            RefreshTaskManagerReplacement();
            return;
        }

        var result = ShellLauncher.Open(System.IO.Path.Combine(Environment.SystemDirectory, "Taskmgr.exe"));
        if (!result.Success)
        {
            _notifications.Error(Loc.T("Task Manager"), result.Message);
        }
    }

    /// <summary>Task Manager's Startup apps tab lives on Apps &amp; Drivers › Startup; this takes a Task Manager user there.</summary>
    [RelayCommand]
    private void OpenStartupApps() =>
        (App.Services.GetService(typeof(Wpf.Ui.INavigationService)) as Wpf.Ui.INavigationService)?.Navigate(typeof(Views.Pages.StartupPage));
}
