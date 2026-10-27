using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>Settings → Tray: tray icon, minimize/close to tray, start with Windows (logon task), temperature icon and alerts.</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty] private bool _showTrayIcon;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startInTray;
    [ObservableProperty] private bool _showTemperatureInTrayIcon;
    [ObservableProperty] private bool _notifyTemperatureAlerts;
    [ObservableProperty] private string? _startWithWindowsStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeStartWithWindows))]
    private bool _isChangingStartWithWindows;

    /// <summary>False in demo mode (and off Windows): changes are saved but no task is created.</summary>
    public bool CanScheduleTasks => _scheduledTasks.CanSchedule;

    public bool CannotScheduleTasks => !CanScheduleTasks;

    public string SchedulingUnavailableText => _scheduledTasks.UnavailableReason ?? "Scheduled tasks are not available.";

    public bool CanChangeStartWithWindows => !IsChangingStartWithWindows;

    private void LoadTraySettings(AppSettings s)
    {
        ScheduleRules.EnsureSettings(s);
        var tray = s.Tray;
        ShowTrayIcon = tray.ShowIcon;
        CloseToTray = tray.CloseToTray;
        if (!IsChangingStartWithWindows)
        {
            StartWithWindows = tray.StartWithWindows;
        }

        StartInTray = tray.StartInTray;
        ShowTemperatureInTrayIcon = tray.ShowTemperatureInIcon;
        NotifyTemperatureAlerts = tray.NotifyTemperatureAlerts;
    }

    partial void OnShowTrayIconChanged(bool value) => Persist(s => s.Tray.ShowIcon = value);
    partial void OnCloseToTrayChanged(bool value) => Persist(s => s.Tray.CloseToTray = value);
    partial void OnShowTemperatureInTrayIconChanged(bool value) => Persist(s => s.Tray.ShowTemperatureInIcon = value);
    partial void OnNotifyTemperatureAlertsChanged(bool value) => Persist(s => s.Tray.NotifyTemperatureAlerts = value);

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_loading)
        {
            _ = ChangeStartWithWindowsAsync(value);
        }
    }

    partial void OnStartInTrayChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        Persist(s => s.Tray.StartInTray = value);
        if (StartWithWindows)
        {
            // Recreate the logon task so it starts with (or without) --tray.
            _ = ApplyStartWithWindowsAsync(true, confirmFirst: false);
        }
    }

    private Task ChangeStartWithWindowsAsync(bool enable) => ApplyStartWithWindowsAsync(enable, confirmFirst: enable);

    private async Task ApplyStartWithWindowsAsync(bool enable, bool confirmFirst)
    {
        if (confirmFirst && CanScheduleTasks)
        {
            bool ok = await _dialogs.ConfirmAsync("Start PC Checker with Windows?",
                "PC Checker will add a task called \"PC Checker\\Start\" to Windows Task Scheduler. About 30 seconds after you sign in it starts "
                + (StartInTray ? "PC Checker in the tray (no window)" : "PC Checker")
                + " with administrator rights, so there is no UAC prompt.\n\nTurn this switch off at any time to remove the task.",
                "Start with Windows");
            if (!ok)
            {
                SetWithoutSaving(() => StartWithWindows = false);
                return;
            }
        }

        IsChangingStartWithWindows = true;
        try
        {
            var result = await _scheduledTasks.SetStartWithWindowsAsync(enable, StartInTray, CancellationToken.None);
            if (result.Success)
            {
                Persist(s => s.Tray.StartWithWindows = enable);
                StartWithWindowsStatus = enable || CanScheduleTasks ? result.Message : null;
            }
            else
            {
                ErrorMessage = result.Message;
                SetWithoutSaving(() => StartWithWindows = !enable);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "Could not change \"Start with Windows\": " + ex.Message;
            SetWithoutSaving(() => StartWithWindows = !enable);
        }
        finally
        {
            IsChangingStartWithWindows = false;
        }
    }

    /// <summary>Reads the logon task so the toggle's note says whether Windows really has it.</summary>
    private async Task RefreshLogonTaskStatusAsync(CancellationToken cancellationToken)
    {
        if (!CanScheduleTasks)
        {
            StartWithWindowsStatus = StartWithWindows ? SchedulingUnavailableText : null;
            return;
        }

        var status = await _scheduledTasks.GetLogonTaskStatusAsync(cancellationToken);
        StartWithWindowsStatus = (StartWithWindows, status.Exists) switch
        {
            (true, true) when ScheduleRules.PointsElsewhere(status, _scheduledTasks.ExecutablePath) =>
                "The startup task opens an older copy of PcChecker.exe. Use \"Fix tasks\" under Schedules.",
            (true, true) => "Registered in Task Scheduler as \"PC Checker\\Start\".",
            (true, false) => "The startup task is missing (it may have been deleted). Turn the switch off and on to add it again.",
            (false, true) => "A \"PC Checker\\Start\" task still exists in Task Scheduler. Turn the switch on and off to remove it.",
            _ => null,
        };
    }

    /// <summary>Changes a property without saving it or running its side effects (used to undo a toggle).</summary>
    private void SetWithoutSaving(Action set)
    {
        bool was = _loading;
        _loading = true;
        try
        {
            set();
        }
        finally
        {
            _loading = was;
        }
    }
}
