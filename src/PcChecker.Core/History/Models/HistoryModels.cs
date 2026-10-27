using PcChecker.Core.Common;

namespace PcChecker.Core.History;

/// <summary>How a metric's values are combined when several samples fall into one roll-up bucket.</summary>
public enum HistoryAggregation
{
    /// <summary>Time-weighted average (loads, power, memory).</summary>
    Average,

    /// <summary>The highest value (temperatures, so peaks survive a roll-up).</summary>
    Max,

    /// <summary>The lowest value (health score, so a bad check is not averaged away).</summary>
    Min,

    /// <summary>The most recent value (free space).</summary>
    Last,
}

/// <summary>
/// One recorded line: a point in time, how many seconds it covers (60 for a one-minute sample, 900 for a
/// 15-minute roll-up, 0 for an instant such as a health check) and the metric values measured in that span.
/// </summary>
public sealed record HistorySample(DateTimeOffset Time, int SpanSeconds, IReadOnlyDictionary<string, double> Values)
{
    /// <summary>The value of <paramref name="metric"/>, or null when this sample did not measure it.</summary>
    public double? Get(string metric) => Values.TryGetValue(metric, out var v) ? v : null;
}

/// <summary>A single value of one metric at one time, for charts and statistics.</summary>
public readonly record struct HistoryPoint(DateTimeOffset Time, double Value);

/// <summary>What a notable event is about.</summary>
public enum HistoryEventKind
{
    TemperaturePeak,
    FreeSpaceDrop,
    HealthDrop,
}

/// <summary>Something worth pointing out in the recorded history, such as a temperature peak above the limit.</summary>
public sealed record HistoryEvent(
    DateTimeOffset Time,
    HistoryEventKind Kind,
    Severity Severity,
    string Metric,
    string Title,
    string Detail);

/// <summary>The limits the event detector compares against.</summary>
public sealed record HistoryThresholds(
    double CpuWarnC,
    double CpuCritC,
    double GpuWarnC,
    double GpuCritC,
    double MinFreeSpaceDropGb = 10,
    double MinFreeSpaceDropFraction = 0.05,
    double MinHealthDrop = 5)
{
    /// <summary>The temperature limits from the user's monitoring settings, with the default drop sizes.</summary>
    public static HistoryThresholds From(Settings.MonitoringSettings monitoring)
    {
        ArgumentNullException.ThrowIfNull(monitoring);
        return new HistoryThresholds(monitoring.CpuWarnC, monitoring.CpuCritC, monitoring.GpuWarnC, monitoring.GpuCritC);
    }
}

/// <summary>The samples loaded for a time range, plus how many unreadable lines were skipped.</summary>
public sealed record HistoryData(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<HistorySample> Samples, int CorruptLines = 0)
{
    public static HistoryData Empty(DateTimeOffset from, DateTimeOffset to) => new(from, to, []);

    public bool IsEmpty => Samples.Count == 0;

    /// <summary>Every metric id that has at least one value, in display order.</summary>
    public IReadOnlyList<string> Metrics => HistoryMetrics.Order(Samples.SelectMany(s => s.Values.Keys).Distinct(StringComparer.Ordinal));

    /// <summary>The values of one metric in time order.</summary>
    public IReadOnlyList<HistoryPoint> Series(string metric) => HistoryMath.Series(Samples, metric);
}

/// <summary>How much history is stored on disk.</summary>
public sealed record HistoryStorageInfo(int Files, long Bytes, DateOnly? OldestDay)
{
    public static HistoryStorageInfo None { get; } = new(0, 0, null);
}

/// <summary>A choice in the page's range picker.</summary>
public sealed record HistoryRange(string Label, TimeSpan Span)
{
    public static HistoryRange LastHour { get; } = new("Last hour", TimeSpan.FromHours(1));
    public static HistoryRange Last24Hours { get; } = new("Last 24 hours", TimeSpan.FromHours(24));
    public static HistoryRange Last7Days { get; } = new("Last 7 days", TimeSpan.FromDays(7));
    public static HistoryRange Last30Days { get; } = new("Last 30 days", TimeSpan.FromDays(30));
    public static HistoryRange Last90Days { get; } = new("Last 90 days", TimeSpan.FromDays(90));

    public static IReadOnlyList<HistoryRange> All { get; } = [LastHour, Last24Hours, Last7Days, Last30Days, Last90Days];

    public override string ToString() => Label;
}

/// <summary>User settings for the history recorder. Lives on <c>AppSettings.History</c>.</summary>
public sealed class HistorySettings
{
    public const int MinIntervalSeconds = 30;
    public const int MaxIntervalSeconds = 900;
    public const int MinRetentionDays = 7;
    public const int MaxRetentionDays = 365;

    /// <summary>Records temperatures, loads, power, memory, free space and health scores in the background.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often one sample is written. Readings in between are combined (peak temperature, average load).</summary>
    public int SampleIntervalSeconds { get; set; } = 60;

    /// <summary>Days of history kept on disk; older days are deleted automatically.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>
    /// The retention in force before it was last lowered. Until <see cref="RetentionGrace"/> has passed since
    /// <see cref="RetentionLoweredAt"/>, nothing younger than this is deleted, so a slip of the number box can be undone.
    /// </summary>
    public int? PreviousRetentionDays { get; set; }

    /// <summary>When <see cref="RetentionDays"/> was last lowered (see <see cref="PreviousRetentionDays"/>).</summary>
    public DateTimeOffset? RetentionLoweredAt { get; set; }

    /// <summary>How long a lowered retention waits before older days are actually deleted.</summary>
    public static readonly TimeSpan RetentionGrace = TimeSpan.FromHours(24);

    public static int ClampInterval(double seconds) =>
        double.IsNaN(seconds) ? 60 : (int)Math.Clamp(Math.Round(seconds), MinIntervalSeconds, MaxIntervalSeconds);

    public static int ClampRetention(double days) =>
        double.IsNaN(days) ? 90 : (int)Math.Clamp(Math.Round(days), MinRetentionDays, MaxRetentionDays);

    /// <summary>
    /// The retention maintenance uses at <paramref name="now"/>: the setting, or the earlier, longer one while a recent
    /// reduction is still within <see cref="RetentionGrace"/>.
    /// </summary>
    public int EffectiveRetention(DateTimeOffset now)
    {
        int current = ClampRetention(RetentionDays);
        if (PreviousRetentionDays is { } previous && RetentionLoweredAt is { } lowered && now >= lowered && now - lowered < RetentionGrace)
        {
            return Math.Max(current, ClampRetention(previous));
        }

        return current;
    }

    /// <summary>
    /// Changes <see cref="RetentionDays"/>. Lowering it starts (or restarts) the grace period and remembers the longer
    /// retention in force until then; raising it back to that value or above ends the grace period.
    /// </summary>
    public void SetRetention(double days, DateTimeOffset now)
    {
        int next = ClampRetention(days);
        int effective = EffectiveRetention(now);
        if (next < effective)
        {
            PreviousRetentionDays = effective;
            RetentionLoweredAt = now;
        }
        else
        {
            PreviousRetentionDays = null;
            RetentionLoweredAt = null;
        }

        RetentionDays = next;
    }
}
