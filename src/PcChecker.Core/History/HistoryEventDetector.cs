using System.Globalization;
using PcChecker.Core.Common;

namespace PcChecker.Core.History;

/// <summary>
/// Finds the notable moments in recorded history: CPU or GPU temperatures above the warning limit (one event per
/// hot spell, at its peak), a sudden drop in a drive's free space, and a health score that fell noticeably.
/// Pure: plain samples in, events out, newest first.
/// </summary>
public static class HistoryEventDetector
{
    /// <summary>
    /// Two hot readings further apart than this count as separate hot spells. Rolled-up readings (15 minutes or an
    /// hour each) may be up to 1.5 times their own span apart instead; see <see cref="SpellGapFor"/>.
    /// </summary>
    public static readonly TimeSpan HotSpellGap = TimeSpan.FromMinutes(10);

    /// <summary>Free space readings further apart than this are not compared (the drop was not "sudden").</summary>
    public static readonly TimeSpan MaxFreeSpaceGap = TimeSpan.FromHours(26);

    public static IReadOnlyList<HistoryEvent> Detect(IReadOnlyList<HistorySample> samples, HistoryThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(thresholds);
        var events = new List<HistoryEvent>();
        events.AddRange(TemperaturePeaks(samples, HistoryMetrics.CpuTemp, "CPU", thresholds.CpuWarnC, thresholds.CpuCritC));
        events.AddRange(TemperaturePeaks(samples, HistoryMetrics.GpuTemp, "GPU", thresholds.GpuWarnC, thresholds.GpuCritC));

        var drives = samples.SelectMany(s => s.Values.Keys).Where(HistoryMetrics.IsDiskFree).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var metric in drives)
        {
            events.AddRange(FreeSpaceDrops(HistoryMath.Series(samples, metric), metric, thresholds));
        }

        events.AddRange(HealthDrops(HistoryMath.Series(samples, HistoryMetrics.Health), thresholds.MinHealthDrop));
        return events.OrderByDescending(e => e.Time).ThenByDescending(e => e.Severity).ToList();
    }

    /// <summary>
    /// The longest pause between two hot readings that still counts as one spell: <see cref="HotSpellGap"/>, or 1.5
    /// times the longer of the two readings' spans for rolled-up data (so a long gaming session stored as 15-minute or
    /// hourly points stays one event).
    /// </summary>
    public static TimeSpan SpellGapFor(int spanSecondsBefore, int spanSecondsAfter)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Max(spanSecondsBefore, spanSecondsAfter)) * 1.5);
        return span > HotSpellGap ? span : HotSpellGap;
    }

    /// <summary>One event per spell of <paramref name="metric"/> readings at or above <paramref name="warn"/>, at the spell's peak.</summary>
    public static IEnumerable<HistoryEvent> TemperaturePeaks(IEnumerable<HistorySample> samples, string metric, string part, double warn, double crit)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var readings = samples
            .Where(s => s.Values.TryGetValue(metric, out var v) && double.IsFinite(v))
            .OrderBy(s => s.Time)
            .Select(s => (Point: new HistoryPoint(s.Time, s.Values[metric]), Span: s.SpanSeconds))
            .ToList();
        return TemperaturePeaks(readings, metric, part, warn, crit);
    }

    /// <summary>One event per spell of readings at or above <paramref name="warn"/>, at the spell's peak (readings of one minute or less).</summary>
    public static IEnumerable<HistoryEvent> TemperaturePeaks(IReadOnlyList<HistoryPoint> series, string metric, string part, double warn, double crit)
    {
        ArgumentNullException.ThrowIfNull(series);
        return TemperaturePeaks(series.Select(p => (p, 0)).ToList(), metric, part, warn, crit);
    }

    private static IEnumerable<HistoryEvent> TemperaturePeaks(IReadOnlyList<(HistoryPoint Point, int Span)> series, string metric, string part,
        double warn, double crit)
    {
        HistoryPoint? peak = null;
        DateTimeOffset start = default, end = default;
        int endSpan = 0;
        int count = 0;
        foreach (var (p, span) in series)
        {
            bool hot = p.Value >= warn;
            if (peak is not null && (!hot || p.Time - end > SpellGapFor(endSpan, span)))
            {
                yield return Peak(metric, part, peak.Value, start, end, count, warn, crit);
                peak = null;
            }

            if (!hot)
            {
                continue;
            }

            if (peak is null)
            {
                start = p.Time;
                peak = p;
                count = 0;
            }
            else if (p.Value > peak.Value.Value)
            {
                peak = p;
            }

            end = p.Time;
            endSpan = span;
            count++;
        }

        if (peak is not null)
        {
            yield return Peak(metric, part, peak.Value, start, end, count, warn, crit);
        }
    }

    /// <summary>A drop between two neighbouring readings of at least the larger of the fixed and relative minimum.</summary>
    public static IEnumerable<HistoryEvent> FreeSpaceDrops(IReadOnlyList<HistoryPoint> series, string metric, HistoryThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(thresholds);
        string drive = HistoryMetrics.DriveOf(metric) ?? metric;
        for (int i = 1; i < series.Count; i++)
        {
            var before = series[i - 1];
            var after = series[i];
            double drop = before.Value - after.Value;
            if (after.Time - before.Time > MaxFreeSpaceGap || drop < Math.Max(thresholds.MinFreeSpaceDropGb, before.Value * thresholds.MinFreeSpaceDropFraction))
            {
                continue;
            }

            var severity = after.Value < before.Value * 0.5 || after.Value < 20 ? Severity.High : Severity.Medium;
            yield return new HistoryEvent(after.Time, HistoryEventKind.FreeSpaceDrop, severity, metric,
                $"Free space on {drive} dropped by {Gb(drop)}",
                $"From {Gb(before.Value)} to {Gb(after.Value)} free between {Clock(before.Time)} and {Clock(after.Time)}.");
        }
    }

    /// <summary>A health check that scored at least <paramref name="minDrop"/> points lower than the one before.</summary>
    public static IEnumerable<HistoryEvent> HealthDrops(IReadOnlyList<HistoryPoint> series, double minDrop)
    {
        ArgumentNullException.ThrowIfNull(series);
        for (int i = 1; i < series.Count; i++)
        {
            double drop = series[i - 1].Value - series[i].Value;
            if (drop < minDrop)
            {
                continue;
            }

            var severity = series[i].Value < 60 || drop >= 20 ? Severity.High : Severity.Medium;
            yield return new HistoryEvent(series[i].Time, HistoryEventKind.HealthDrop, severity, HistoryMetrics.Health,
                $"Health score fell from {Score(series[i - 1].Value)} to {Score(series[i].Value)}",
                "Open the Health page to see what changed since the previous check.");
        }
    }

    private static HistoryEvent Peak(string metric, string part, HistoryPoint peak, DateTimeOffset start, DateTimeOffset end, int count,
        double warn, double crit)
    {
        bool critical = peak.Value >= crit;
        string limit = critical ? $"critical limit of {Temp(crit)}" : $"warning limit of {Temp(warn)}";
        string detail = count <= 1
            ? $"Above the {limit}."
            : $"Above the {limit} for about {Duration(end - start)}, from {Clock(start)}.";
        return new HistoryEvent(peak.Time, HistoryEventKind.TemperaturePeak, critical ? Severity.Critical : Severity.High, metric,
            $"{part} reached {Temp(peak.Value)}", detail);
    }

    private static string Duration(TimeSpan span) => span.TotalMinutes < 90
        ? Math.Max(1, (int)Math.Round(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " min"
        : span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + " h";

    private static string Temp(double celsius) => Math.Round(celsius).ToString("0", CultureInfo.InvariantCulture) + " °C";

    private static string Gb(double gb) => gb >= 1000
        ? (gb / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " TB"
        : Math.Round(gb).ToString("0", CultureInfo.InvariantCulture) + " GB";

    private static string Score(double score) => Math.Round(score).ToString("0", CultureInfo.InvariantCulture);

    private static string Clock(DateTimeOffset time) => time.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture);
}
