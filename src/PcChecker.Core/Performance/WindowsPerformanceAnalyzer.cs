using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Sensors;
using PcChecker.Core.Startup;

namespace PcChecker.Core.Performance;

/// <summary>
/// Windows implementation of <see cref="IPerformanceAnalyzer"/>: gathers <see cref="PerformanceFacts"/> from startup
/// items, services, a two-second process sample, memory, drives, power and registry settings, then lets
/// <see cref="SlowdownRules"/> turn them into findings and a score. Every probe degrades to "unknown" on failure.
/// </summary>
public sealed class WindowsPerformanceAnalyzer : IPerformanceAnalyzer
{
    private static readonly TimeSpan ProcessSampleWindow = TimeSpan.FromSeconds(2);

    private readonly ISensorService _sensors;
    private readonly ILogger<WindowsPerformanceAnalyzer> _logger;

    public WindowsPerformanceAnalyzer(ISensorService sensors, ILogger<WindowsPerformanceAnalyzer> logger)
    {
        _sensors = sensors;
        _logger = logger;
    }

    public PerformanceReport? Last { get; private set; }

    public async Task<PerformanceReport> AnalyzeAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            Last = PerformanceReport.Empty("The slowdown scan only runs on Windows.");
            return Last;
        }

        var warnings = new List<string>();
        _logger.LogInformation("Slowdown scan started.");

        progress?.Report(new ProgressUpdate("Startup programs", 5));
        var startupItems = await SafeAsync(() => StartupEnumerator.EnumerateAsync(cancellationToken), "Startup programs", warnings).ConfigureAwait(false) ?? [];

        progress?.Report(new ProgressUpdate("Services", 20));
        var services = await Task.Run(() => WindowsSystemProbe.Services(warnings), cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressUpdate("Measuring busy processes", 35, "Sampling CPU use for two seconds"));
        var processes = await WindowsSystemProbe.SampleProcessesAsync(ProcessSampleWindow, cancellationToken).ConfigureAwait(false);
        var topProcesses = ProcessRules.SelectTop(processes);

        progress?.Report(new ProgressUpdate("Memory and drives", 55));
        var os = await Task.Run(() => WindowsSystemProbe.OperatingSystem(warnings), cancellationToken).ConfigureAwait(false);
        var drives = await Task.Run(() => WindowsSystemProbe.Drives(warnings), cancellationToken).ConfigureAwait(false);
        var trim = await WindowsSystemProbe.TrimEnabledAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressUpdate("Power and Windows settings", 70));
        var scheme = await WindowsSystemProbe.ActivePowerSchemeAsync(cancellationToken).ConfigureAwait(false);
        var bootSeconds = await WindowsSystemProbe.LastBootSecondsAsync(cancellationToken).ConfigureAwait(false);
        var (hasBattery, onBattery) = WindowsSystemProbe.Battery();

        progress?.Report(new ProgressUpdate("Checking temperatures and updates", 85));
        var (cpuTemp, gpuTemp) = ReadTemperatures();
        var now = DateTimeOffset.Now;

        long totalMemory = os?.TotalBytes ?? SafeTotalMemory();
        long freeMemory = os?.FreeBytes ?? 0;
        double usedPercent = totalMemory > 0 ? Math.Round(100.0 * (totalMemory - freeMemory) / totalMemory, 1) : 0;
        double? commitPercent = os is { CommitLimitBytes: > 0 } o ? Math.Round(100.0 * (o.CommitLimitBytes - o.CommitFreeBytes) / o.CommitLimitBytes, 1) : null;
        double uptimeDays = PerformanceTextParsers.UptimeDays(Environment.TickCount64);
        var systemDrive = drives.FirstOrDefault(d => d.Drive.StartsWith("C", StringComparison.OrdinalIgnoreCase));
        int? visualFx = WindowsSystemProbe.VisualEffectsSetting();

        var facts = new PerformanceFacts
        {
            StartupItems = startupItems,
            Services = services,
            TopProcesses = topProcesses,
            LastBootSeconds = bootSeconds,
            MemoryUsedPercent = usedPercent,
            MemoryTotalBytes = totalMemory,
            CommitUsedPercent = commitPercent,
            ActivePowerPlan = scheme?.Name,
            FastStartupEnabled = WindowsSystemProbe.FastStartupEnabled(),
            HibernationEnabled = WindowsSystemProbe.HibernationEnabled(),
            VisualEffectsBestAppearance = visualFx is null ? null : visualFx == 1,
            GameModeEnabled = WindowsSystemProbe.GameModeEnabled(),
            GameBarEnabled = WindowsSystemProbe.GameBarEnabled(),
            BackgroundAppsEnabled = WindowsSystemProbe.BackgroundAppsEnabled(),
            StorageSenseEnabled = WindowsSystemProbe.StorageSenseEnabled(),
            TrimEnabled = trim,
            SystemDriveIsSsd = systemDrive.Drive is null ? null : systemDrive.MediaType.Equals("SSD", StringComparison.OrdinalIgnoreCase) || systemDrive.MediaType.Equals("SCM", StringComparison.OrdinalIgnoreCase),
            PendingReboot = WindowsSystemProbe.PendingReboot(),
            DaysSinceLastUpdateCheck = WindowsSystemProbe.DaysSinceUpdateCheck(now),
            MaxCpuTempC = cpuTemp,
            MaxGpuTempC = gpuTemp,
            OnBattery = onBattery,
            HasBattery = hasBattery,
            UptimeDays = uptimeDays,
            ThirdPartyAntivirusCount = WindowsSystemProbe.ThirdPartyAntivirusCount(),
            LogicalProcessors = Environment.ProcessorCount,
            Drives = drives,
        };

        progress?.Report(new ProgressUpdate("Looking for slowdowns", 95));
        var findings = SlowdownRules.Evaluate(facts);
        int score = SlowdownRules.Score(findings);
        if (startupItems.Count == 0)
        {
            warnings.Add("No startup programs were listed. The Startup page shows the full list once it has scanned.");
        }

        Last = new PerformanceReport(score, findings, startupItems, services, topProcesses, bootSeconds, usedPercent, totalMemory,
            scheme?.Name, drives, now, warnings);
        _logger.LogInformation("Slowdown scan finished: score {Score}, {Findings} findings, {Warnings} warnings.", score, findings.Count, warnings.Count);
        progress?.Report(new ProgressUpdate("Done", 100));
        return Last;
    }

    private (double? Cpu, double? Gpu) ReadTemperatures()
    {
        try
        {
            var snapshot = _sensors.Latest;
            if (snapshot is null)
            {
                return (null, null);
            }

            double? cpu = snapshot.Readings.Where(r => r.Kind == SensorKind.Temperature && r.Hardware == HardwareKind.Cpu && r.Value.HasValue).Select(r => r.Value).Max();
            double? gpu = snapshot.Readings.Where(r => r.Kind == SensorKind.Temperature && r.Hardware == HardwareKind.Gpu && r.Value.HasValue).Select(r => r.Value).Max();
            return (cpu, gpu);
        }
        catch
        {
            return (null, null);
        }
    }

    private static long SafeTotalMemory()
    {
        try
        {
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<T?> SafeAsync<T>(Func<Task<T>> work, string what, List<string> warnings) where T : class
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{What} could not be read.", what);
            warnings.Add($"{what} could not be read: {ex.Message}");
            return null;
        }
    }
}
