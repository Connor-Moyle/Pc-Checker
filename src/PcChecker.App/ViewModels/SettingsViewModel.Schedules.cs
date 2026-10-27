using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Settings → Schedules: per job an on/off switch backed by a Windows scheduled task, frequency/day/time, the last
/// result and Run now; plus "notify only when something needs attention" and "Fix tasks" after PcChecker.exe moved.
/// </summary>
public sealed partial class SettingsViewModel
{
    private bool _schedulesHooked;
    private bool _demoSchedulesSeeded;

    [ObservableProperty] private bool _notifyOnlyWhenAttention;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMovedTasks))]
    private string? _movedTasksText;

    public ObservableCollection<ScheduleJobRow> ScheduleJobs { get; } = [];

    public bool HasMovedTasks => !string.IsNullOrEmpty(MovedTasksText);

    public string ReportsFolder => _scheduledRunner.Executor.ReportsDirectory;

    private void HookSchedules()
    {
        if (_schedulesHooked)
        {
            return;
        }

        _schedulesHooked = true;
        _jobStore.Saved += (_, result) => DispatcherHelper.PostToUi(() => FindRow(result.Job)?.ApplyResult(result));
        _scheduledRunner.Executor.RunningChanged += (_, job) => DispatcherHelper.PostToUi(() =>
        {
            if (FindRow(job) is { } row)
            {
                row.IsRunning = _scheduledRunner.Executor.IsRunning(job);
                if (!row.IsRunning)
                {
                    row.ProgressText = null;
                }
            }
        });
    }

    private void LoadScheduleSettings(AppSettings s)
    {
        var schedules = ScheduleRules.EnsureSettings(s);
        if (IsDemoMode && !_demoSchedulesSeeded)
        {
            // Demo mode shows a realistic setup (only ever in the separate demo settings file, and only in memory until saved).
            _demoSchedulesSeeded = true;
            if (!schedules.Jobs.Any(j => j.Enabled))
            {
                foreach (var job in schedules.Jobs.Where(j => j.Job is ScheduledJobKind.Health or ScheduledJobKind.Cleanup or ScheduledJobKind.Report))
                {
                    job.Enabled = true;
                }
            }
        }

        NotifyOnlyWhenAttention = schedules.NotifyOnlyWhenAttention;
        if (ScheduleJobs.Count == 0)
        {
            foreach (var job in ScheduledJobIds.All)
            {
                ScheduleJobs.Add(new ScheduleJobRow(job, OnScheduleSwitchedAsync, SaveScheduleAsync, RunScheduleNowAsync, OpenScheduleOutput));
            }
        }

        var results = _jobStore.LoadAll();
        foreach (var row in ScheduleJobs)
        {
            row.Load(schedules.For(row.Job));
            row.SkipRecentMinutes = Math.Max(0, s.Cleanup.SkipFilesNewerThanMinutes);
            row.ApplyResult(results.GetValueOrDefault(row.Job));
            row.IsRunning = _scheduledRunner.Executor.IsRunning(row.Job);
            if (!CanScheduleTasks)
            {
                row.TaskStateText = row.IsScheduled ? SchedulingUnavailableText : null;
            }
        }
    }

    partial void OnNotifyOnlyWhenAttentionChanged(bool value) => Persist(s => ScheduleRules.EnsureSettings(s).NotifyOnlyWhenAttention = value);

    private ScheduleJobRow? FindRow(ScheduledJobKind job) => ScheduleJobs.FirstOrDefault(r => r.Job == job);

    /// <summary>The switch: turning it on confirms and creates the task; turning it off removes it.</summary>
    private async Task OnScheduleSwitchedAsync(ScheduleJobRow row, bool enabled)
    {
        var schedule = row.ToSchedule(enabled);
        if (enabled && ScheduleRules.Validate(schedule) is { } problem)
        {
            row.ValidationError = problem;
            row.SetScheduledSilently(false);
            return;
        }

        if (enabled && CanScheduleTasks)
        {
            bool ok = await _dialogs.ConfirmAsync($"Schedule \"{row.Title}\"?",
                $"Windows Task Scheduler will start PC Checker {ScheduleRules.DescribeInline(schedule)} to run this job, even while the app is closed. "
                + "It runs in the background with administrator rights (no UAC prompt) and stops after 10 minutes at most.\n\n"
                + ScheduledJobIds.Description(row.Job, Math.Max(0, Settings.Current.Cleanup.SkipFilesNewerThanMinutes)) + "\n\n"
                + (NotifyOnlyWhenAttention ? "You get a notification only when something needs your attention." : "You get a notification after every run.")
                + " Turn the switch off at any time to remove the task.",
                "Create schedule");
            if (!ok)
            {
                row.SetScheduledSilently(false);
                return;
            }
        }

        row.IsWorking = true;
        try
        {
            var result = enabled
                ? await _scheduledTasks.CreateOrUpdateAsync(schedule, CancellationToken.None)
                : await _scheduledTasks.DeleteAsync(row.Job, CancellationToken.None);
            if (!result.Success)
            {
                ErrorMessage = result.Message;
                row.SetScheduledSilently(!enabled);
                return;
            }

            StoreSchedule(schedule);
            row.MarkSaved();
            _notifications.Show(enabled ? "Schedule created" : "Schedule removed", result.Message,
                enabled ? NotificationKind.Success : NotificationKind.Info);
            await RefreshRowStatusAsync(row, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not change the \"{row.Title}\" schedule: {ex.Message}";
            row.SetScheduledSilently(!enabled);
        }
        finally
        {
            row.IsWorking = false;
        }
    }

    /// <summary>Save: stores the edited frequency/time/options and updates the task when the job is on.</summary>
    private async Task SaveScheduleAsync(ScheduleJobRow row)
    {
        var schedule = row.ToSchedule();
        if (ScheduleRules.Validate(schedule) is { } problem)
        {
            row.ValidationError = problem;
            return;
        }

        row.IsWorking = true;
        try
        {
            if (schedule.Enabled)
            {
                var result = await _scheduledTasks.CreateOrUpdateAsync(schedule, CancellationToken.None);
                if (!result.Success)
                {
                    row.ValidationError = result.Message;
                    return;
                }

                _notifications.Success("Schedule updated", result.Message);
            }

            StoreSchedule(schedule);
            row.MarkSaved();
            await RefreshRowStatusAsync(row, CancellationToken.None);
        }
        catch (Exception ex)
        {
            row.ValidationError = ex.Message;
        }
        finally
        {
            row.IsWorking = false;
        }
    }

    private async Task RunScheduleNowAsync(ScheduleJobRow row)
    {
        var saved = ScheduleRules.EnsureSettings(Settings.Current).For(row.Job);
        if (row.Job == ScheduledJobKind.Cleanup && !saved.DryRun)
        {
            int minutes = Math.Max(0, Settings.Current.Cleanup.SkipFilesNewerThanMinutes);
            bool ok = await _dialogs.ConfirmAsync("Clean up now?",
                "Temporary files from the Safe categories you use on the Cleanup page are deleted permanently (they do not go to the Recycle Bin). "
                + $"Files changed in the last {minutes} minutes, files in use and the Recycle Bin itself are left alone.",
                "Clean now", isDanger: true);
            if (!ok)
            {
                return;
            }
        }

        row.ProgressText = "Starting…";
        var progress = new Progress<ProgressUpdate>(p => row.ProgressText = p.Stage + (p.Percent is { } pc ? $" ({pc.ToString("0", CultureInfo.CurrentCulture)}%)" : string.Empty));
        try
        {
            var result = await _scheduledRunner.RunAsync(row.Job, "manual", progress);
            if (result is null)
            {
                _notifications.Info(row.Title, "This job is already running.");
                return;
            }

            row.ApplyResult(result);
            _notifications.Show(result.Title, result.Summary,
                !result.Success ? NotificationKind.Error : result.NeedsAttention ? NotificationKind.Warning : NotificationKind.Success);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"\"{row.Title}\" could not run: {ex.Message}";
        }
        finally
        {
            row.ProgressText = null;
        }
    }

    private void OpenScheduleOutput(ScheduleJobRow row)
    {
        if (row.OutputPath is { } path)
        {
            Open(path);
        }
    }

    private void StoreSchedule(JobSchedule schedule) => Persist(s =>
    {
        var target = ScheduleRules.EnsureSettings(s).For(schedule.Job);
        target.Enabled = schedule.Enabled;
        target.Frequency = schedule.Frequency;
        target.Day = schedule.Day;
        target.Time = schedule.Time;
        target.DryRun = schedule.DryRun;
        target.KeepReports = schedule.KeepReports;
    });

    /// <summary>Reads every enabled job's task (and the logon task) from Task Scheduler. Called when the page opens.</summary>
    private async Task RefreshScheduleStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var row in ScheduleJobs)
            {
                await RefreshRowStatusAsync(row, cancellationToken);
            }

            await RefreshLogonTaskStatusAsync(cancellationToken);
            if (CanScheduleTasks)
            {
                var moved = await _scheduledRunner.FindMovedTasksAsync(cancellationToken);
                MovedTasksText = moved.Count == 0 ? null
                    : $"{string.Join(", ", moved)} still run PcChecker.exe from its old location. Fix tasks points them at {_scheduledTasks.ExecutablePath}.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = "Could not read the scheduled tasks: " + ex.Message;
        }
    }

    private async Task RefreshRowStatusAsync(ScheduleJobRow row, CancellationToken cancellationToken)
    {
        if (!CanScheduleTasks)
        {
            row.TaskStateText = row.IsScheduled ? SchedulingUnavailableText : null;
            return;
        }

        if (!row.IsScheduled)
        {
            row.TaskStateText = null;
            return;
        }

        var status = await _scheduledTasks.GetStatusAsync(row.Job, cancellationToken);
        row.TaskStateText = !status.Exists
            ? "Not found in Task Scheduler (it may have been deleted). Click Save to add it again."
            : ScheduleRules.PointsElsewhere(status, _scheduledTasks.ExecutablePath)
                ? "Runs an older copy of PcChecker.exe. Use Fix tasks above."
                : "In Task Scheduler · last result: " + ScheduleRules.DescribeLastResult(status.LastResult)
                  + (status.NextRun is { } next ? " · next " + next.ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture) : string.Empty);
        if (!status.Exists)
        {
            row.IsDirty = true;
        }
    }

    [RelayCommand]
    private async Task FixScheduledTasksAsync()
    {
        await RunBusyAsync(async ct =>
        {
            var result = await _scheduledRunner.RepairTasksAsync(ct);
            if (result.Success)
            {
                MovedTasksText = null;
                _notifications.Success("Scheduled tasks fixed", result.Message);
            }
            else
            {
                ErrorMessage = result.Message;
            }

            await RefreshScheduleStatusAsync(ct);
        }, "Updating scheduled tasks…");
    }

    [RelayCommand]
    private void OpenTaskScheduler() => Open("taskschd.msc");

    [RelayCommand]
    private void OpenReportsFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(ReportsFolder);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "Could not create the reports folder: " + ex.Message;
            return;
        }

        Open(ReportsFolder);
    }

    /// <summary>Reset: removes PC Checker's scheduled tasks and the logon task (best effort), then restores the defaults.</summary>
    private async Task ResetTrayAndSchedulesAsync(AppSettings current, AppSettings defaults)
    {
        if (CanScheduleTasks)
        {
            foreach (var job in ScheduledJobIds.All)
            {
                try
                {
                    await _scheduledTasks.DeleteAsync(job, CancellationToken.None);
                }
                catch (Exception)
                {
                    // Best effort; the rows show any task that is left.
                }
            }

            try
            {
                await _scheduledTasks.SetStartWithWindowsAsync(false, false, CancellationToken.None);
            }
            catch (Exception)
            {
            }
        }

        current.Tray = defaults.Tray;
        current.Schedules = defaults.Schedules;
        foreach (var row in ScheduleJobs)
        {
            row.IsDirty = false;
        }
    }
}
