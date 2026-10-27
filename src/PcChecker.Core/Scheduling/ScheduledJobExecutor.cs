using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Logging;
using PcChecker.Core.Performance;
using PcChecker.Core.Reports;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Scheduling;

/// <summary>
/// Runs one maintenance job (health, Safe-only cleanup, driver check, report) with a 10-minute limit, summarizes the
/// outcome, stores it as the job's last result and returns it. Used by the headless <c>--scheduled</c> run, by the
/// running app when Task Scheduler hands it a job, and by "Run now" / the tray. Never throws for a failed job.
/// </summary>
public sealed class ScheduledJobExecutor
{
    /// <summary>Scheduled report files start with this, so retention never touches reports the user saved by hand.</summary>
    public const string ScheduledReportPrefix = "pc-checker-scheduled-report-";

    private readonly IHealthService _health;
    private readonly ITempFileScanner _scanner;
    private readonly ITempFileCleaner _cleaner;
    private readonly IDriverService _drivers;
    private readonly IHardwareInventoryService _hardware;
    private readonly IPerformanceAnalyzer _performance;
    private readonly IReportService _reports;
    private readonly ISensorService? _sensors;
    private readonly IActionJournal? _journal;
    private readonly IScheduledJobStore _store;
    private readonly ISettingsService _settings;
    private readonly ILogger<ScheduledJobExecutor> _logger;
    private readonly ConcurrentDictionary<ScheduledJobKind, byte> _running = new();

    public ScheduledJobExecutor(IHealthService health, ITempFileScanner scanner, ITempFileCleaner cleaner, IDriverService drivers,
        IHardwareInventoryService hardware, IPerformanceAnalyzer performance, IReportService reports, IScheduledJobStore store,
        ISettingsService settings, ILogger<ScheduledJobExecutor> logger, ISensorService? sensors = null, IActionJournal? journal = null)
    {
        _health = health;
        _scanner = scanner;
        _cleaner = cleaner;
        _drivers = drivers;
        _hardware = hardware;
        _performance = performance;
        _reports = reports;
        _store = store;
        _settings = settings;
        _logger = logger;
        _sensors = sensors;
        _journal = journal;
    }

    /// <summary>Folder for scheduled reports (Documents\PC Checker Reports; demo mode points it elsewhere).</summary>
    public string ReportsDirectory { get; set; } = DefaultReportsDirectory();

    /// <summary>Raised when a job starts or stops, so the UI can show a spinner.</summary>
    public event EventHandler<ScheduledJobKind>? RunningChanged;

    /// <summary>Raised after every finished run (scheduled, manual or from the tray).</summary>
    public event EventHandler<ScheduledJobResult>? Completed;

    /// <summary>Where demo mode keeps scheduled reports, so it never writes into the user's Documents.</summary>
    public static string DemoReportsDirectory() => Path.Combine(JsonSettingsService.DefaultDirectory(), "demo", "reports");

    /// <summary>File name (without extension) of a scheduled report for <paramref name="computerName"/> made at <paramref name="when"/>.</summary>
    public static string ScheduledReportStem(string? computerName, DateTimeOffset when) =>
        ReportText.SuggestedFileName(computerName, when).Replace("pc-checker-report-", ScheduledReportPrefix, StringComparison.Ordinal);

    public static string DefaultReportsDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
        {
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(documents, "PC Checker Reports");
    }

    public bool IsRunning(ScheduledJobKind job) => _running.ContainsKey(job);

    /// <summary>
    /// Runs <paramref name="job"/>. Returns null when the same job is already running. <paramref name="trigger"/> is
    /// "schedule", "manual" or "tray" and is kept with the result. <paramref name="dryRunOverride"/> replaces the
    /// cleanup schedule's dry-run choice (the tray's quick clean always cleans for real after confirming).
    /// </summary>
    public async Task<ScheduledJobResult?> RunAsync(ScheduledJobKind job, string trigger, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken, bool? dryRunOverride = null)
    {
        if (!_running.TryAdd(job, 0))
        {
            _logger.LogInformation("{Job} is already running; the {Trigger} request was skipped.", job, trigger);
            return null;
        }

        RunningChanged?.Invoke(this, job);
        var started = DateTimeOffset.Now;
        using var timeout = new CancellationTokenSource(ScheduledJobExecutorLimits.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        ScheduledJobResult result;
        try
        {
            _logger.LogInformation("Scheduled job {Job} started ({Trigger}).", job, trigger);
            var previous = _store.Load(job);
            result = job switch
            {
                ScheduledJobKind.Health => await RunHealthAsync(previous, started, trigger, progress, linked.Token).ConfigureAwait(false),
                ScheduledJobKind.Cleanup => await RunCleanupAsync(started, trigger, dryRunOverride, progress, linked.Token).ConfigureAwait(false),
                ScheduledJobKind.Drivers => await RunDriversAsync(started, trigger, progress, linked.Token).ConfigureAwait(false),
                ScheduledJobKind.Report => await RunReportAsync(started, trigger, progress, linked.Token).ConfigureAwait(false),
                _ => JobResultSummarizer.Failure(job, "Unknown job.", false, started, DateTimeOffset.Now, trigger),
            };
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Scheduled job {Job} hit the {Minutes}-minute limit.", job, ScheduledJobExecutorLimits.Timeout.TotalMinutes);
            result = JobResultSummarizer.Failure(job, string.Empty, true, started, DateTimeOffset.Now, trigger);
        }
        catch (OperationCanceledException)
        {
            result = JobResultSummarizer.Failure(job, "Cancelled before it finished.", false, started, DateTimeOffset.Now, trigger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled job {Job} failed.", job);
            result = JobResultSummarizer.Failure(job, ex.Message, false, started, DateTimeOffset.Now, trigger);
        }
        finally
        {
            _running.TryRemove(job, out _);
        }

        _store.Save(result);
        await JournalAsync(result).ConfigureAwait(false);
        _logger.LogInformation("Scheduled job {Job} finished in {Seconds:0}s: {Title}. {Summary}", job, result.Duration.TotalSeconds, result.Title, result.Summary);
        RunningChanged?.Invoke(this, job);
        Completed?.Invoke(this, result);
        return result;
    }

    /// <summary>
    /// Adds the run to the Action Journal. Cleanup is skipped: the cleaner already journals what it deleted through
    /// the change guard.
    /// </summary>
    private async Task JournalAsync(ScheduledJobResult result)
    {
        if (_journal is null || result.Job == ScheduledJobKind.Cleanup)
        {
            return;
        }

        try
        {
            var outcome = result.Success ? "Success" : "Failed: " + result.Summary;
            await _journal.RecordAsync(ActionRecord.Create("Schedules", $"{ScheduledJobIds.DisplayName(result.Job)} ({result.Trigger}): {result.Title}",
                ScheduledJobIds.TaskName(result.Job), outcome, after: result.Summary)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not add the {Job} run to the action journal.", result.Job);
        }
    }

    private async Task<ScheduledJobResult> RunHealthAsync(ScheduledJobResult? previous, DateTimeOffset started, string trigger,
        IProgress<ProgressUpdate>? progress, CancellationToken token)
    {
        var report = await _health.AnalyzeAsync(progress, token).ConfigureAwait(false);
        return JobResultSummarizer.Health(report, previous, started, DateTimeOffset.Now, trigger);
    }

    private async Task<ScheduledJobResult> RunCleanupAsync(DateTimeOffset started, string trigger, bool? dryRunOverride,
        IProgress<ProgressUpdate>? progress, CancellationToken token)
    {
        var settings = _settings.Current;
        var ids = ScheduleRules.UnattendedCleanupCategories(_scanner.Categories, settings.Cleanup.SavedSelection());
        if (ids.Count == 0)
        {
            return new ScheduledJobResult(ScheduledJobKind.Cleanup, started, DateTimeOffset.Now, true, false, "Cleanup: nothing selected",
                "None of the categories you picked on the Cleanup page is rated Safe, so nothing was cleaned.", [], trigger);
        }

        bool dryRun = dryRunOverride ?? (settings.Schedules?.For(ScheduledJobKind.Cleanup).DryRun ?? false);
        var scan = await _scanner.ScanAsync(ids, progress, token).ConfigureAwait(false);
        var run = await _cleaner.CleanAsync(scan, ids, dryRun, progress, token).ConfigureAwait(false);
        return JobResultSummarizer.Cleanup(run, ids.Count, started, DateTimeOffset.Now, trigger);
    }

    private async Task<ScheduledJobResult> RunDriversAsync(DateTimeOffset started, string trigger, IProgress<ProgressUpdate>? progress, CancellationToken token)
    {
        var check = await _drivers.CheckForUpdatesAsync(progress, token).ConfigureAwait(false);
        return JobResultSummarizer.Drivers(check, started, DateTimeOffset.Now, trigger);
    }

    private async Task<ScheduledJobResult> RunReportAsync(DateTimeOffset started, string trigger, IProgress<ProgressUpdate>? progress, CancellationToken token)
    {
        var missing = new List<string>();
        var hardware = await TryAsync("hardware", () => _hardware.ScanAsync(progress, token), missing, token).ConfigureAwait(false);
        var health = await TryAsync("health", () => _health.AnalyzeAsync(progress, token), missing, token).ConfigureAwait(false);
        var performance = await TryAsync("performance", () => _performance.AnalyzeAsync(progress, token), missing, token).ConfigureAwait(false);
        var drivers = await TryAsync("drivers", () => _drivers.ScanInstalledAsync(progress, token), missing, token).ConfigureAwait(false);
        if (hardware is null && health is null && performance is null && drivers is null)
        {
            return JobResultSummarizer.Failure(ScheduledJobKind.Report, "None of the scans worked, so there was nothing to report.", false, started, DateTimeOffset.Now, trigger);
        }

        var version = typeof(ScheduledJobExecutor).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var generated = DateTimeOffset.Now;
        var bundle = new ReportBundle(hardware, health, performance, null, drivers, _sensors?.Latest, generated, version);
        var computer = hardware?.System?.Name ?? Environment.MachineName;
        var stem = ScheduledReportStem(computer, generated);
        Directory.CreateDirectory(ReportsDirectory);
        progress?.Report(new ProgressUpdate("Writing the report"));
        var written = await _reports.ExportAsync(bundle, ReportFormat.Html, Path.Combine(ReportsDirectory, stem + ".html"), token).ConfigureAwait(false);

        int keep = _settings.Current.Schedules?.For(ScheduledJobKind.Report).KeepReports ?? 10;
        int removed = PruneReports(keep);
        return JobResultSummarizer.Report(written, removed, missing, started, DateTimeOffset.Now, trigger);
    }

    private int PruneReports(int keep)
    {
        int removed = 0;
        try
        {
            var files = new DirectoryInfo(ReportsDirectory).EnumerateFiles(ScheduledReportPrefix + "*.html")
                .Select(f => (f.FullName, f.LastWriteTimeUtc))
                .ToList();
            foreach (var path in ScheduleRules.ReportsToDelete(files, keep))
            {
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not remove old report {Path}.", path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not list old reports in {Folder}.", ReportsDirectory);
        }

        return removed;
    }

    private async Task<T?> TryAsync<T>(string name, Func<Task<T>> scan, List<string> missing, CancellationToken token) where T : class
    {
        try
        {
            return await scan().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The {Section} scan failed while building a scheduled report.", name);
            missing.Add(name);
            return null;
        }
    }
}
