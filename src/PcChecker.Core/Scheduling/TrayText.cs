using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Scheduling;

/// <summary>The readings the tray tooltip and popup show, resolved from a sensor snapshot.</summary>
public sealed record TrayReadings(
    double? CpuTempC,
    double? GpuTempC,
    double? DriveTempC,
    double? CpuLoadPercent,
    double? GpuLoadPercent,
    double? RamPercent,
    string? CpuName = null)
{
    public static TrayReadings Empty { get; } = new(null, null, null, null, null, null);

    /// <summary>Picks CPU package, GPU core, hottest drive, CPU/GPU load and RAM use from the snapshot.</summary>
    public static TrayReadings From(SensorSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Readings.Count == 0)
        {
            return Empty;
        }

        return new TrayReadings(
            Value(snapshot, OverlayMetricIds.CpuTemp),
            Value(snapshot, OverlayMetricIds.GpuTemp),
            Value(snapshot, OverlayMetricIds.DiskTemp),
            Value(snapshot, OverlayMetricIds.CpuLoad),
            Value(snapshot, OverlayMetricIds.GpuLoad),
            Value(snapshot, OverlayMetricIds.RamPercent),
            TemperatureRoles.CpuNameOf(snapshot.Readings));
    }

    private static double? Value(SensorSnapshot snapshot, string metricId)
    {
        var v = OverlaySensorMatcher.Resolve(snapshot, metricId);
        return v.HasValue ? v.Value : null;
    }
}

/// <summary>Pure text and level helpers for the tray icon, its tooltip and the compact popup.</summary>
public static class TrayText
{
    /// <summary>Windows cuts tray tooltips at 127 characters.</summary>
    public const int MaxTooltipLength = 127;

    public const string AppName = "PC Checker";
    public const string Placeholder = "--";

    /// <summary>"PC Checker — CPU 62 °C · GPU 58 °C". Missing readings are left out; with none it is just the app name.</summary>
    public static string Tooltip(double? cpuTempC, double? gpuTempC, string? suffix = null)
    {
        var parts = new List<string>(2);
        if (IsValid(cpuTempC))
        {
            parts.Add("CPU " + Temperature(cpuTempC));
        }

        if (IsValid(gpuTempC))
        {
            parts.Add("GPU " + Temperature(gpuTempC));
        }

        var text = parts.Count == 0 ? AppName : AppName + " — " + string.Join(" · ", parts);
        if (!string.IsNullOrWhiteSpace(suffix))
        {
            text += "\n" + suffix.Trim();
        }

        return text.Length <= MaxTooltipLength ? text : text[..(MaxTooltipLength - 1)] + "…";
    }

    /// <summary>"62 °C", or "--" when unknown.</summary>
    public static string Temperature(double? celsius) =>
        IsValid(celsius) ? Math.Round(celsius!.Value).ToString("0", CultureInfo.InvariantCulture) + " °C" : Placeholder;

    /// <summary>"37 %", or "--".</summary>
    public static string Percent(double? value) =>
        IsValid(value) ? Math.Round(Math.Clamp(value!.Value, 0, 100)).ToString("0", CultureInfo.InvariantCulture) + " %" : Placeholder;

    /// <summary>"312 W" (or "≈ 312 W" for an estimate), or "--".</summary>
    public static string Watts(double? watts, bool estimated = false) =>
        IsValid(watts) && watts > 0
            ? (estimated ? "≈ " : string.Empty) + Math.Round(watts!.Value).ToString("0", CultureInfo.InvariantCulture) + " W"
            : Placeholder;

    /// <summary>The number drawn in the tray icon: "62", "99+" above 99, "--" when unknown.</summary>
    public static string IconText(double? celsius)
    {
        if (!IsValid(celsius))
        {
            return Placeholder;
        }

        var rounded = Math.Round(celsius!.Value);
        return rounded > 99 ? "99+" : Math.Max(0, rounded).ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>The hottest of the given temperatures, ignoring missing values.</summary>
    public static double? Hottest(params double?[] temperatures)
    {
        double? max = null;
        foreach (var t in temperatures ?? [])
        {
            if (IsValid(t) && (max is null || t > max))
            {
                max = t;
            }
        }

        return max;
    }

    /// <summary>Good below warn, Warning from warn, Critical from crit, Unknown without a value.</summary>
    public static HealthStatus Level(double? celsius, double warnC, double critC)
    {
        if (!IsValid(celsius))
        {
            return HealthStatus.Unknown;
        }

        return celsius >= critC ? HealthStatus.Critical : celsius >= warnC ? HealthStatus.Warning : HealthStatus.Good;
    }

    /// <summary>
    /// Warning and critical limits for a temperature metric of the tray and the overlay, the same ones the alerts and
    /// the Temperatures page use: the CPU limits follow <see cref="TemperatureRoles.CpuThresholds"/> (a Ryzen 7000/9000
    /// is not "critical" at its 95 °C design point), the hot spot has its own limits. Null for other metrics.
    /// </summary>
    public static (double Warn, double Crit)? LimitsFor(string metricId, MonitoringSettings thresholds, string? cpuName)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        return metricId switch
        {
            OverlayMetricIds.CpuTemp => TemperatureRoles.CpuThresholds(thresholds, cpuName),
            OverlayMetricIds.GpuTemp => (thresholds.GpuWarnC, thresholds.GpuCritC),
            OverlayMetricIds.GpuHotspot => (Math.Max(thresholds.GpuHotSpotWarnC, thresholds.GpuWarnC), Math.Max(thresholds.GpuHotSpotCritC, thresholds.GpuCritC)),
            OverlayMetricIds.DiskTemp => (thresholds.DiskWarnC, thresholds.DiskCritC),
            _ => null,
        };
    }

    /// <summary>The level of one temperature metric against <see cref="LimitsFor"/>; Unknown for a metric without limits.</summary>
    public static HealthStatus LevelFor(string metricId, double? celsius, MonitoringSettings thresholds, string? cpuName) =>
        LimitsFor(metricId, thresholds, cpuName) is { } limits ? Level(celsius, limits.Warn, limits.Crit) : HealthStatus.Unknown;

    /// <summary>How many of CPU, GPU and drive are at or above their warning thresholds right now.</summary>
    public static int ActiveAlerts(TrayReadings readings, MonitoringSettings thresholds)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(thresholds);
        if (!thresholds.AlertsEnabled)
        {
            return 0;
        }

        int count = 0;
        count += LevelFor(OverlayMetricIds.CpuTemp, readings.CpuTempC, thresholds, readings.CpuName) >= HealthStatus.Warning ? 1 : 0;
        count += LevelFor(OverlayMetricIds.GpuTemp, readings.GpuTempC, thresholds, readings.CpuName) >= HealthStatus.Warning ? 1 : 0;
        count += LevelFor(OverlayMetricIds.DiskTemp, readings.DriveTempC, thresholds, readings.CpuName) >= HealthStatus.Warning ? 1 : 0;
        return count;
    }

    /// <summary>"No alerts", "1 alert", "2 alerts".</summary>
    public static string Alerts(int count) => count <= 0 ? "No alerts" : JobResultSummarizer.Plural(count, "alert", "alerts");

    /// <summary>Notification text for a temperature alert.</summary>
    public static (string Title, string Message) AlertNotification(TemperatureAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        string level = alert.Severity >= Severity.Critical ? "critical" : "high";
        var title = JobResultSummarizer.Truncate($"{alert.HardwareName} is running {level}", JobResultSummarizer.MaxTitleLength);
        var message = $"{Temperature(alert.Value)} (your limit is {Temperature(alert.Threshold)}). Open PC Checker → Temperatures to see what is heating up.";
        return (title, JobResultSummarizer.Truncate(message, JobResultSummarizer.MaxMessageLength));
    }

    private static bool IsValid(double? value) => value is { } v && !double.IsNaN(v) && !double.IsInfinity(v);
}

/// <summary>Lets one notification per key through per window (temperature alerts must not flood the tray).</summary>
public sealed class NotificationThrottle
{
    private readonly TimeSpan _window;
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public NotificationThrottle(TimeSpan window)
    {
        _window = window;
    }

    /// <summary>True (and remembers <paramref name="now"/>) when <paramref name="key"/> has not been let through within the window.</summary>
    public bool ShouldShow(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_last.TryGetValue(key, out var last) && now - last < _window)
            {
                return false;
            }

            _last[key] = now;
            return true;
        }
    }
}
