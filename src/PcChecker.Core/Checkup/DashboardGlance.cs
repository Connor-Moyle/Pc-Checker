using PcChecker.Core.DiskSpace;
using PcChecker.Core.History;
using PcChecker.Core.WindowsUpdate;

namespace PcChecker.Core.Checkup;

/// <summary>What the Dashboard's Windows Update tile says.</summary>
public enum UpdateGlanceState
{
    /// <summary>Nothing is known yet (no check-up and no status).</summary>
    Unknown,

    /// <summary>The update service is turned off.</summary>
    ServiceOff,

    /// <summary>Installed updates wait for a restart.</summary>
    RestartPending,

    /// <summary>The last check found updates that are not installed.</summary>
    UpdatesWaiting,

    /// <summary>Updates are paused in Settings.</summary>
    Paused,

    /// <summary>The last check found nothing to install.</summary>
    UpToDate,

    /// <summary>Only the time of Windows' own last check is known.</summary>
    CheckedByWindows,
}

/// <summary>The Windows Update tile: its state, how many updates wait, and when Windows last looked.</summary>
public sealed record UpdateGlance(UpdateGlanceState State, int Waiting, DateTimeOffset? LastCheck, DateTimeOffset? PausedUntil);

/// <summary>A temperature trend for a Dashboard sparkline: the points (oldest first) and the peak.</summary>
public sealed record TemperatureTrend(IReadOnlyList<double> Points, double? Peak, double? Latest)
{
    public static TemperatureTrend Empty { get; } = new([], null, null);

    public bool HasData => Points.Count >= 2;
}

/// <summary>Pure helpers behind the Dashboard's Windows Update, temperature trend and fullest drive tiles.</summary>
public static class DashboardGlance
{
    /// <summary>Points in a 24-hour sparkline (one every 30 minutes).</summary>
    public const int TrendPoints = 48;

    /// <summary>
    /// The Windows Update tile. The service being off comes first, then a pending restart, then what the last check-up
    /// found (<paramref name="scan"/>, optional because checking takes a while), then a pause, then Windows' own last check.
    /// </summary>
    public static UpdateGlance Updates(WindowsUpdateStatus? status, PendingUpdateScan? scan)
    {
        int waiting = scan is null ? 0 : UpdateInstallSummary.InstallAllSelection(scan.Updates).Count;
        var lastCheck = scan?.Captured ?? status?.LastCheck;
        var pausedUntil = status?.Pause is { IsPaused: true } pause ? pause.PausedUntil : null;
        UpdateGlanceState state;
        if (status?.ServiceDisabled == true)
        {
            state = UpdateGlanceState.ServiceOff;
        }
        else if (status?.RestartPending == true)
        {
            state = UpdateGlanceState.RestartPending;
        }
        else if (waiting > 0)
        {
            state = UpdateGlanceState.UpdatesWaiting;
        }
        else if (status?.Pause.IsPaused == true)
        {
            state = UpdateGlanceState.Paused;
        }
        else if (scan is not null)
        {
            state = UpdateGlanceState.UpToDate;
        }
        else
        {
            state = status?.LastCheck is null ? UpdateGlanceState.Unknown : UpdateGlanceState.CheckedByWindows;
        }

        return new UpdateGlance(state, waiting, lastCheck, pausedUntil);
    }

    /// <summary>The drive with the largest used share (ties: the system drive, then the smaller free space), or null.</summary>
    public static DriveSpaceInfo? FullestDrive(IEnumerable<DriveSpaceInfo> drives)
    {
        ArgumentNullException.ThrowIfNull(drives);
        return drives
            .Where(d => d.TotalBytes > 0)
            .OrderByDescending(UsedPercent)
            .ThenByDescending(d => d.IsSystemDrive)
            .ThenBy(d => d.FreeBytes)
            .FirstOrDefault();
    }

    /// <summary>Used share of a drive in percent (0–100).</summary>
    public static double UsedPercent(DriveSpaceInfo drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        return drive.TotalBytes <= 0 ? 0 : Math.Clamp(100.0 * drive.UsedBytes / drive.TotalBytes, 0, 100);
    }

    /// <summary>
    /// The trend of a temperature metric over <paramref name="data"/>: samples rolled up into equal buckets (peaks kept,
    /// as the History page does for temperatures) so a day fits in <paramref name="points"/> points.
    /// </summary>
    public static TemperatureTrend Trend(HistoryData data, string metric, int points = TrendPoints)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);
        if (data.IsEmpty || points < 2)
        {
            return TemperatureTrend.Empty;
        }

        var span = data.To - data.From;
        var bucket = TimeSpan.FromSeconds(Math.Max(60, Math.Ceiling(span.TotalSeconds / points)));
        var series = HistoryMath.Series(HistoryRollup.Compact(data.Samples, bucket), metric)
            .Where(p => double.IsFinite(p.Value) && p.Value > 0)
            .ToList();
        if (series.Count == 0)
        {
            return TemperatureTrend.Empty;
        }

        return new TemperatureTrend(series.Select(p => p.Value).ToList(), series.Max(p => p.Value), series[^1].Value);
    }
}
