using System.IO;
using System.Windows;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Settings;

namespace PcChecker.App.Services;

/// <summary>
/// App side of scheduled jobs: the headless <c>--scheduled &lt;job&gt;</c> run (no window, temporary tray notification,
/// exit code), the <c>PcChecker.Jobs</c> pipe that lets Task Scheduler hand a job to the running app, and the start-up
/// check that finds tasks pointing at an old copy of PcChecker.exe.
/// </summary>
public sealed class ScheduledRunner : IDisposable
{
    /// <summary>How long the headless run keeps its temporary tray icon so the notification can be read.</summary>
    public static readonly TimeSpan HeadlessNotificationTime = TimeSpan.FromSeconds(10);

    private readonly ScheduledJobExecutor _executor;
    private readonly ISettingsService _settings;
    private readonly IScheduledTaskService _tasks;
    private readonly ILogger<ScheduledRunner> _logger;
    private ScheduledJobPipeServer? _pipe;

    public ScheduledRunner(ScheduledJobExecutor executor, ISettingsService settings, IScheduledTaskService tasks, ILogger<ScheduledRunner> logger)
    {
        _executor = executor;
        _settings = settings;
        _tasks = tasks;
        _logger = logger;
        if (AppOptions.Current.IsDemo)
        {
            // Demo reports go to the demo folder, never into the user's Documents.
            _executor.ReportsDirectory = Path.Combine(JsonSettingsService.DefaultDirectory(), "demo", "reports");
        }
    }

    public ScheduledJobExecutor Executor => _executor;

    /// <summary>Runs a job inside the running app. Returns null when that job is already running.</summary>
    public Task<ScheduledJobResult?> RunAsync(ScheduledJobKind job, string trigger, IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default, bool? dryRunOverride = null) =>
        _executor.RunAsync(job, trigger, progress, cancellationToken, dryRunOverride);

    /// <summary>
    /// Starts answering the job pipe: <c>run &lt;job&gt;</c> starts the job in the background (demo mode refuses, so a
    /// real scheduled run then runs on its own), <c>show</c> calls <paramref name="showWindow"/> on the UI thread.
    /// </summary>
    public void StartListening(Action showWindow)
    {
        ArgumentNullException.ThrowIfNull(showWindow);
        if (_pipe is not null)
        {
            return;
        }

        bool demo = AppOptions.Current.IsDemo;
        _pipe = new ScheduledJobPipeServer(request =>
        {
            if (request.Command == PipeCommand.Show)
            {
                DispatcherHelper.PostToUi(showWindow);
                return ScheduledJobPipe.ReplyOk;
            }

            if (demo || request.Job is not { } job)
            {
                return ScheduledJobPipe.ReplyRefused;
            }

            if (_executor.IsRunning(job))
            {
                return ScheduledJobPipe.ReplyBusy;
            }

            _logger.LogInformation("Task Scheduler handed the {Job} job to the running app.", job);
            _ = Task.Run(() => _executor.RunAsync(job, "schedule", null, CancellationToken.None));
            return ScheduledJobPipe.ReplyOk;
        }, _logger);
        _pipe.Start();
    }

    /// <summary>
    /// Looks for enabled jobs (and the logon task) whose task runs a different PcChecker.exe than this one, which
    /// happens after the file was moved. Adds a Dashboard notice pointing at Settings → Schedules → Fix tasks.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindMovedTasksAsync(CancellationToken cancellationToken)
    {
        var moved = new List<string>();
        if (!_tasks.CanSchedule)
        {
            return moved;
        }

        var exe = _tasks.ExecutablePath;
        var s = _settings.Current;
        foreach (var schedule in ScheduleRules.EnsureSettings(s).Jobs.Where(j => j.Enabled))
        {
            var status = await _tasks.GetStatusAsync(schedule.Job, cancellationToken).ConfigureAwait(false);
            if (ScheduleRules.PointsElsewhere(status, exe))
            {
                moved.Add(ScheduledJobIds.DisplayName(schedule.Job));
            }
        }

        if (s.Tray.StartWithWindows)
        {
            var logon = await _tasks.GetLogonTaskStatusAsync(cancellationToken).ConfigureAwait(false);
            if (ScheduleRules.PointsElsewhere(logon, exe))
            {
                moved.Add("Start with Windows");
            }
        }

        return moved;
    }

    /// <summary>Recreates every enabled task (and the logon task) so they run this PcChecker.exe.</summary>
    public async Task<OperationResult> RepairTasksAsync(CancellationToken cancellationToken)
    {
        var s = _settings.Current;
        var failures = new List<string>();
        int fixedCount = 0;
        foreach (var schedule in ScheduleRules.EnsureSettings(s).Jobs.Where(j => j.Enabled))
        {
            var r = await _tasks.CreateOrUpdateAsync(schedule, cancellationToken).ConfigureAwait(false);
            if (r.Success)
            {
                fixedCount++;
            }
            else
            {
                failures.Add(ScheduledJobIds.DisplayName(schedule.Job) + ": " + r.Message);
            }
        }

        if (s.Tray.StartWithWindows)
        {
            var r = await _tasks.SetStartWithWindowsAsync(true, s.Tray.StartInTray, cancellationToken).ConfigureAwait(false);
            if (r.Success)
            {
                fixedCount++;
            }
            else
            {
                failures.Add("Start with Windows: " + r.Message);
            }
        }

        return failures.Count == 0
            ? OperationResult.Ok(fixedCount == 0 ? "There were no tasks to fix." : $"Updated {fixedCount} task(s) to run {_tasks.ExecutablePath}.")
            : OperationResult.Fail(string.Join("\n", failures));
    }

    /// <summary>Background start-up check; never throws.</summary>
    public void CheckMovedTasksInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                var moved = await FindMovedTasksAsync(cts.Token).ConfigureAwait(false);
                if (moved.Count > 0)
                {
                    StartupNotices.Add("Schedules",
                        $"{string.Join(", ", moved)} still point to an older location of PcChecker.exe. Open Settings → Schedules and click \"Fix tasks\".",
                        Severity.Medium);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not check the scheduled tasks.");
            }
        });
    }

    /// <summary>
    /// The <c>--scheduled &lt;job&gt;</c> run: executes the job (10-minute limit), shows the result as a notification
    /// from a temporary tray icon (respecting "notify only when something needs attention"), removes the icon after
    /// 10 seconds and returns the exit code: 0 when the job succeeded, 1 when it failed.
    /// </summary>
    public static async Task<int> RunHeadlessAsync(IServiceProvider services, ScheduledJobKind job, ILogger? logger)
    {
        ScheduledJobResult? result;
        try
        {
            var runner = services.GetRequiredService<ScheduledRunner>();
            result = await runner.RunAsync(job, "schedule").ConfigureAwait(true);
            if (result is null)
            {
                logger?.LogInformation("{Job} was already running in another process; nothing to do.", job);
                return 0;
            }

            var settings = services.GetRequiredService<ISettingsService>().Current;
            if (JobResultSummarizer.ShouldNotify(result, ScheduleRules.EnsureSettings(settings).NotifyOnlyWhenAttention))
            {
                await ShowTemporaryNotificationAsync(result, logger).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "The headless {Job} run failed.", job);
            return 1;
        }

        return result.Success ? 0 : 1;
    }

    private static async Task ShowTemporaryNotificationAsync(ScheduledJobResult result, ILogger? logger)
    {
        TaskbarIcon? icon = null;
        try
        {
            var (title, message) = JobResultSummarizer.NotificationText(result);
            icon = new TaskbarIcon
            {
                ToolTipText = "PC Checker — " + ScheduledJobIds.DisplayName(result.Job),
                IconSource = Tray.TrayIconService.LoadLogo(),
            };
            icon.ForceCreate(false);
            icon.ShowNotification(title, message, !result.Success ? NotificationIcon.Error : result.NeedsAttention ? NotificationIcon.Warning : NotificationIcon.Info);
            await Task.Delay(HeadlessNotificationTime).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not show the scheduled job notification.");
        }
        finally
        {
            try
            {
                icon?.Dispose();
            }
            catch
            {
                // The process is about to exit; a stuck icon is cleaned up by Windows.
            }
        }
    }

    public void Dispose() => _pipe?.Dispose();
}
