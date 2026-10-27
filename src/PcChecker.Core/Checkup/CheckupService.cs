using Microsoft.Extensions.Logging;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.Debloat;
using PcChecker.Core.Drivers;
using PcChecker.Core.Health;
using PcChecker.Core.Optimization;
using PcChecker.Core.Performance;
using PcChecker.Core.Restore;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Settings;
using PcChecker.Core.Startup;
using PcChecker.Core.WindowsUpdate;

namespace PcChecker.Core.Checkup;

/// <summary>
/// Default <see cref="ICheckupService"/>. It owns no system access of its own: it calls the services behind the
/// Health, Cleanup, Drivers, Windows Update, Startup, Speed check, Debloat and Old drivers pages, so demo mode (their
/// Demo implementations) and normal mode share it. This half runs the scans; the other half applies the fixes.
/// </summary>
public sealed partial class CheckupService : ICheckupService
{
    /// <summary>A single part that takes longer than this is skipped and listed as a gap.</summary>
    public static readonly TimeSpan PartTimeout = TimeSpan.FromMinutes(5);

    private const string RunName = "Check-up";

    private readonly IHealthService _health;
    private readonly ITempFileScanner _scanner;
    private readonly ITempFileCleaner _cleaner;
    private readonly IDriverService _drivers;
    private readonly IWindowsUpdateService _updates;
    private readonly IStartupService _startup;
    private readonly IPerformanceAnalyzer _performance;
    private readonly IOptimizationService _optimization;
    private readonly IDebloatService _debloat;
    private readonly IDriverCleanupService _driverCleanup;
    private readonly IChangeBatchRunner _batch;
    private readonly ISettingsService _settings;
    private readonly ILogger<CheckupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CheckupService(IHealthService health, ITempFileScanner scanner, ITempFileCleaner cleaner, IDriverService drivers,
        IWindowsUpdateService updates, IStartupService startup, IPerformanceAnalyzer performance, IOptimizationService optimization,
        IDebloatService debloat, IDriverCleanupService driverCleanup, IChangeBatchRunner batch, ISettingsService settings,
        ILogger<CheckupService> logger)
    {
        _health = health;
        _scanner = scanner;
        _cleaner = cleaner;
        _drivers = drivers;
        _updates = updates;
        _startup = startup;
        _performance = performance;
        _optimization = optimization;
        _debloat = debloat;
        _driverCleanup = driverCleanup;
        _batch = batch;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Returns the size of a program file, or null; the Startup page's probe by default (tests replace it).</summary>
    public Func<string, long?> StartupSizeProbe { get; set; } = StartupEnumerator.ProbeFileSize;

    public CheckupReport? Last { get; private set; }

    public event EventHandler<CheckupReport>? Completed;

    public event EventHandler<CheckupReport>? LastChanged;

    /// <summary>The parts of a check-up in the order they run.</summary>
    public static IReadOnlyList<(CheckupSource Source, string Name)> Parts { get; } =
    [
        (CheckupSource.Health, "Health"),
        (CheckupSource.Cleanup, "Temporary files"),
        (CheckupSource.Drivers, "Drivers"),
        (CheckupSource.WindowsUpdate, "Windows Update"),
        (CheckupSource.Startup, "Startup apps"),
        (CheckupSource.Performance, "Speed check"),
        (CheckupSource.Debloat, "Recommended settings"),
        (CheckupSource.OldDrivers, "Old drivers"),
    ];

    public async Task<CheckupReport> RunAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scan = new ScanState();
            for (int i = 0; i < Parts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (source, name) = Parts[i];
                int index = i;
                var partProgress = new InlineProgress(p => progress?.Report(CheckupProgress.Combine(RunName, index, Parts.Count, name, p)));
                progress?.Report(CheckupProgress.Combine(RunName, index, Parts.Count, name, null));
                await RunPartAsync(source, name, ct => ScanPartAsync(source, scan, partProgress, ct), scan, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new ProgressUpdate("Building the to-do list", 100));
            var results = scan.ToResults(DateTimeOffset.Now);
            var report = new CheckupReport(results, CheckupItemMapper.MapAll(results));
            Last = report;
            _logger.LogInformation("Check-up finished: {Items} item(s), {Fixable} with a fix, {Gaps} part(s) missing.",
                report.Items.Count, report.FixableCount, results.Gaps.Count);
            Completed?.Invoke(this, report);
            LastChanged?.Invoke(this, report);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task ScanPartAsync(CheckupSource source, ScanState scan, IProgress<ProgressUpdate> progress, CancellationToken ct) => source switch
    {
        CheckupSource.Health => ScanHealthAsync(scan, progress, ct),
        CheckupSource.Cleanup => ScanCleanupAsync(scan, progress, ct),
        CheckupSource.Drivers => ScanDriversAsync(scan, progress, ct),
        CheckupSource.WindowsUpdate => ScanWindowsUpdateAsync(scan, progress, ct),
        CheckupSource.Startup => ScanStartupAsync(scan, ct),
        CheckupSource.Performance => ScanPerformanceAsync(scan, progress, ct),
        CheckupSource.Debloat => ScanDebloatAsync(scan, ct),
        CheckupSource.OldDrivers => ScanOldDriversAsync(scan, progress, ct),
        _ => Task.CompletedTask,
    };

    private async Task ScanHealthAsync(ScanState scan, IProgress<ProgressUpdate> progress, CancellationToken ct) =>
        scan.Health = await _health.AnalyzeAsync(progress, ct).ConfigureAwait(false);

    /// <summary>The Safe categories a scheduled cleanup would use (the user's picks on the Cleanup page, or the defaults).</summary>
    private async Task ScanCleanupAsync(ScanState scan, IProgress<ProgressUpdate> progress, CancellationToken ct)
    {
        var ids = ScheduleRules.UnattendedCleanupCategories(_scanner.Categories, _settings.Current.Cleanup.EnabledCategoryIds);
        scan.CleanupCategoryIds = ids;
        scan.Cleanup = ids.Count == 0 ? CleanupScanResult.Empty() : await _scanner.ScanAsync(ids, progress, ct).ConfigureAwait(false);
    }

    private async Task ScanDriversAsync(ScanState scan, IProgress<ProgressUpdate> progress, CancellationToken ct)
    {
        Exception? installedFailure = null;
        try
        {
            scan.Drivers = await _drivers.ScanInstalledAsync(progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            installedFailure = ex;
            _logger.LogWarning(ex, "Check-up: the installed driver scan failed; checking for updates anyway.");
        }

        scan.DriverUpdates = await _drivers.CheckForUpdatesAsync(progress, ct).ConfigureAwait(false);
        if (installedFailure is not null)
        {
            scan.Gaps.Add(new CheckupGap(CheckupSource.Drivers, "Installed drivers could not be read: " + installedFailure.Message));
        }
    }

    private async Task ScanWindowsUpdateAsync(ScanState scan, IProgress<ProgressUpdate> progress, CancellationToken ct)
    {
        try
        {
            scan.UpdateStatus = await _updates.GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Check-up: Windows Update status could not be read; checking for updates anyway.");
        }

        scan.PendingUpdates = await _updates.CheckForUpdatesAsync(progress, ct).ConfigureAwait(false);
    }

    private async Task ScanStartupAsync(ScanState scan, CancellationToken ct)
    {
        var items = await _startup.GetStartupItemsAsync(ct).ConfigureAwait(false);
        var probe = StartupSizeProbe;

        // Impact estimates stat each program file; keep that off the caller's thread.
        scan.Startup = await Task.Run(() => items.Select(i => new CheckupStartupEntry(i, StartupImpactRules.Describe(i, probe))).ToList(), ct).ConfigureAwait(false);
    }

    private async Task ScanPerformanceAsync(ScanState scan, IProgress<ProgressUpdate> progress, CancellationToken ct)
    {
        scan.Optimizations = _optimization.Actions;
        scan.Performance = await _performance.AnalyzeAsync(progress, ct).ConfigureAwait(false);
    }

    private async Task ScanDebloatAsync(ScanState scan, CancellationToken ct)
    {
        var preset = DebloatPresets.Find(DebloatPresets.RecommendedId);
        var ids = new HashSet<string>(preset?.TweakIds ?? [], StringComparer.OrdinalIgnoreCase);
        var tweaks = _debloat.Tweaks.Where(t => ids.Contains(t.Id)).ToList();
        scan.TweakStates = await _debloat.GetStatesAsync(tweaks.Select(t => t.Id), ct).ConfigureAwait(false);
        scan.RecommendedTweaks = tweaks;
    }

    private async Task ScanOldDriversAsync(ScanState scan, IProgress<ProgressUpdate> progress, CancellationToken ct) =>
        scan.OldDrivers = await _driverCleanup.ScanAsync(progress, ct).ConfigureAwait(false);

    /// <summary>Runs one part with its own time limit; a failure becomes a gap instead of stopping the check-up.</summary>
    private async Task RunPartAsync(CheckupSource source, string name, Func<CancellationToken, Task> part, ScanState scan, CancellationToken cancellationToken)
    {
        using var limit = new CancellationTokenSource(PartTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            await part(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Check-up: {Part} took longer than {Minutes} minutes and was skipped.", name, PartTimeout.TotalMinutes);
            scan.Gaps.Add(new CheckupGap(source, $"{name} took too long and was skipped."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Check-up: {Part} failed.", name);
            scan.Gaps.Add(new CheckupGap(source, $"{name} could not be checked: {ex.Message}"));
        }
    }

    /// <summary>Collects the parts' results while the check-up runs.</summary>
    private sealed class ScanState
    {
        public HealthReport? Health { get; set; }
        public CleanupScanResult? Cleanup { get; set; }
        public IReadOnlyList<string> CleanupCategoryIds { get; set; } = [];
        public DriverScanResult? Drivers { get; set; }
        public DriverUpdateCheckResult? DriverUpdates { get; set; }
        public WindowsUpdateStatus? UpdateStatus { get; set; }
        public PendingUpdateScan? PendingUpdates { get; set; }
        public IReadOnlyList<CheckupStartupEntry>? Startup { get; set; }
        public PerformanceReport? Performance { get; set; }
        public IReadOnlyList<OptimizationAction> Optimizations { get; set; } = [];
        public IReadOnlyList<DebloatTweak>? RecommendedTweaks { get; set; }
        public IReadOnlyDictionary<string, TweakState>? TweakStates { get; set; }
        public DriverCleanupScanResult? OldDrivers { get; set; }
        public List<CheckupGap> Gaps { get; } = [];

        public CheckupScanResults ToResults(DateTimeOffset captured) => new(Health, Cleanup, CleanupCategoryIds, Drivers, DriverUpdates,
            UpdateStatus, PendingUpdates, Startup, Performance, Optimizations, RecommendedTweaks, TweakStates, OldDrivers, Gaps.ToList(), captured);
    }

    /// <summary>
    /// Forwards progress straight away on the calling thread (unlike <see cref="Progress{T}"/>, which posts to a captured
    /// context); the caller's own progress object takes care of reaching the UI.
    /// </summary>
    internal sealed class InlineProgress(Action<ProgressUpdate> report) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value) => report(value);
    }
}
