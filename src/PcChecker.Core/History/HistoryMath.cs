namespace PcChecker.Core.History;

/// <summary>Statistics over one metric: lowest and highest value with when they happened, the average and the latest.</summary>
public sealed record HistoryStats(
    double Min,
    DateTimeOffset MinTime,
    double Max,
    DateTimeOffset MaxTime,
    double Average,
    double Last,
    DateTimeOffset LastTime,
    int Count);

/// <summary>Pure helpers over recorded history: series extraction, statistics, gaps, hover lookup and energy.</summary>
public static class HistoryMath
{
    /// <summary>The values of <paramref name="metric"/> in time order (samples without it are skipped).</summary>
    public static IReadOnlyList<HistoryPoint> Series(IEnumerable<HistorySample> samples, string metric)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return samples
            .Where(s => s.Values.TryGetValue(metric, out var v) && double.IsFinite(v))
            .Select(s => new HistoryPoint(s.Time, s.Values[metric]))
            .OrderBy(p => p.Time)
            .ToList();
    }

    /// <summary>Min, max (with their times), average and latest value. Null for an empty series.</summary>
    public static HistoryStats? Summarize(IReadOnlyList<HistoryPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        HistoryPoint? min = null, max = null, last = null;
        double sum = 0;
        int count = 0;
        foreach (var p in points)
        {
            if (!double.IsFinite(p.Value))
            {
                continue;
            }

            if (min is null || p.Value < min.Value.Value)
            {
                min = p;
            }

            if (max is null || p.Value > max.Value.Value)
            {
                max = p;
            }

            if (last is null || p.Time >= last.Value.Time)
            {
                last = p;
            }

            sum += p.Value;
            count++;
        }

        if (count == 0)
        {
            return null;
        }

        return new HistoryStats(min!.Value.Value, min.Value.Time, max!.Value.Value, max.Value.Time, sum / count,
            last!.Value.Value, last.Value.Time, count);
    }

    /// <summary>
    /// The point closest in time to <paramref name="time"/> in a time-ordered series (binary search), or null when the
    /// series is empty. Used for the chart's hover value.
    /// </summary>
    public static HistoryPoint? Nearest(IReadOnlyList<HistoryPoint> points, DateTimeOffset time)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return null;
        }

        int lo = 0, hi = points.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (points[mid].Time < time)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        if (lo > 0 && (time - points[lo - 1].Time).Duration() <= (points[lo].Time - time).Duration())
        {
            return points[lo - 1];
        }

        return points[lo];
    }

    /// <summary>
    /// Splits a time-ordered series wherever two neighbours are more than <paramref name="maxGap"/> apart (the PC was
    /// off or recording was paused), so a chart draws separate segments instead of a misleading straight line.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<HistoryPoint>> SplitAtGaps(IReadOnlyList<HistoryPoint> points, TimeSpan maxGap)
    {
        ArgumentNullException.ThrowIfNull(points);
        var segments = new List<IReadOnlyList<HistoryPoint>>();
        var current = new List<HistoryPoint>();
        for (int i = 0; i < points.Count; i++)
        {
            if (current.Count > 0 && points[i].Time - current[^1].Time > maxGap)
            {
                segments.Add(current);
                current = [];
            }

            current.Add(points[i]);
        }

        if (current.Count > 0)
        {
            segments.Add(current);
        }

        return segments;
    }

    /// <summary>The gap that counts as "no data" for a chart drawn with <paramref name="bucket"/>-sized points.</summary>
    public static TimeSpan GapThreshold(TimeSpan bucket) => TimeSpan.FromTicks(Math.Max(bucket.Ticks * 3, TimeSpan.FromMinutes(5).Ticks));

    /// <summary>
    /// Energy used, in kWh, from the power metric: each sample's average watts times the seconds it covers. Instant
    /// samples (span 0) add nothing.
    /// </summary>
    public static double EnergyKwh(IEnumerable<HistorySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double wattSeconds = 0;
        foreach (var s in samples)
        {
            if (s.Values.TryGetValue(HistoryMetrics.Power, out var w) && double.IsFinite(w) && w > 0)
            {
                wattSeconds += w * s.SpanSeconds;
            }
        }

        return wattSeconds / 3_600_000d;
    }

    /// <summary>Picks a chart's vertical range: a fixed 0–100 for percentages, otherwise the data range with some headroom.</summary>
    public static (double Min, double Max) AxisRange(string metric, HistoryStats? stats)
    {
        if (HistoryMetrics.UnitOf(metric) == "%" || metric == HistoryMetrics.Health)
        {
            return (0, 100);
        }

        if (stats is null)
        {
            return (0, 1);
        }

        double span = Math.Max(stats.Max - stats.Min, Math.Max(1, Math.Abs(stats.Max) * 0.05));
        double min = stats.Min - span * 0.1;
        double max = stats.Max + span * 0.1;
        if (stats.Min >= 0 && min < 0)
        {
            min = 0;
        }

        return (Math.Floor(min), Math.Ceiling(max));
    }
}
