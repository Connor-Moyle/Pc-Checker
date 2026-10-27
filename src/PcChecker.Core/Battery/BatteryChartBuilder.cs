using PcChecker.Core.History;

namespace PcChecker.Core.Battery;

/// <summary>A ready-to-draw capacity line: watt-hour points, the time span, a tidy value range and the design capacity.</summary>
public sealed record BatteryChartSeries(
    IReadOnlyList<HistoryPoint> Points,
    DateTimeOffset From,
    DateTimeOffset To,
    double Minimum,
    double Maximum,
    double DesignWh,
    TimeSpan GapThreshold)
{
    public bool HasData => Points.Count >= BatteryChartBuilder.MinimumPoints;
}

/// <summary>
/// Turns the battery report's capacity history (one row per day or per week) into a series for the History page's
/// chart control: full-charge capacity in watt-hours over time, with the design capacity as the dashed limit line.
/// </summary>
public static class BatteryChartBuilder
{
    /// <summary>A line needs at least two readings; fewer and the page says there is not enough history yet.</summary>
    public const int MinimumPoints = 2;

    /// <summary>
    /// Builds the series. Rows without a full-charge capacity are skipped, rows that start on the same day are merged
    /// (the later one wins) and the points are sorted. <paramref name="offsetOf"/> turns the report's local times into
    /// offsets (defaults to the PC's time zone); tests pass a fixed one.
    /// </summary>
    public static BatteryChartSeries CapacitySeries(IReadOnlyList<BatteryCapacityPoint> history, Func<DateTime, TimeSpan>? offsetOf = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        offsetOf ??= t => TimeZoneInfo.Local.GetUtcOffset(t);

        var rows = history
            .Where(p => p.FullChargeCapacityMwh is > 0)
            .GroupBy(p => p.Start.Date)
            .Select(g => g.Last())
            .OrderBy(p => p.Start)
            .ToList();

        var points = rows.Select(p => new HistoryPoint(At(p.Start, offsetOf), p.FullChargeCapacityMwh!.Value / 1000.0)).ToList();
        int? design = history.LastOrDefault(p => p.DesignCapacityMwh is > 0)?.DesignCapacityMwh;
        double designWh = design is { } d ? d / 1000.0 : double.NaN;

        if (points.Count == 0)
        {
            var now = DateTimeOffset.Now;
            return new BatteryChartSeries([], now.AddDays(-7), now, 0, double.IsNaN(designWh) ? 100 : NiceCeiling(designWh * 1.05), designWh, TimeSpan.FromDays(2));
        }

        var from = points[0].Time;
        var lastEnd = rows[^1].End > rows[^1].Start ? At(rows[^1].End, offsetOf) : points[^1].Time;
        var to = lastEnd > points[^1].Time ? lastEnd : points[^1].Time;
        if (to <= from)
        {
            to = from.AddDays(1);
        }

        var (min, max) = Range(points.Select(p => p.Value), designWh);
        return new BatteryChartSeries(points, from, to, min, max, designWh, GapThreshold(rows));
    }

    /// <summary>
    /// A value range that leaves room above the design line and below the lowest reading, on whole 5 Wh steps (1 Wh
    /// for small batteries), and never below zero.
    /// </summary>
    public static (double Minimum, double Maximum) Range(IEnumerable<double> valuesWh, double designWh)
    {
        ArgumentNullException.ThrowIfNull(valuesWh);
        var values = valuesWh.Where(double.IsFinite).ToList();
        if (double.IsFinite(designWh) && designWh > 0)
        {
            values.Add(designWh);
        }

        if (values.Count == 0)
        {
            return (0, 100);
        }

        double low = values.Min(), high = values.Max();
        double step = high >= 20 ? 5 : 1;
        double min = Math.Max(0, Math.Floor(low * 0.9 / step) * step);
        double max = Math.Ceiling(high * 1.05 / step) * step;
        if (max <= min)
        {
            max = min + step;
        }

        return (min, max);
    }

    /// <summary>
    /// Readings further apart than this are not joined: twice the longest row (a week for weekly rows), at least two
    /// days. Longer gaps mean the report has no data for that time.
    /// </summary>
    public static TimeSpan GapThreshold(IReadOnlyList<BatteryCapacityPoint> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var longest = rows.Count == 0 ? TimeSpan.Zero : rows.Max(r => r.End - r.Start);
        var threshold = TimeSpan.FromTicks(Math.Max(longest.Ticks, TimeSpan.FromDays(1).Ticks) * 2);
        return threshold < TimeSpan.FromDays(2) ? TimeSpan.FromDays(2) : threshold;
    }

    private static double NiceCeiling(double value) => Math.Ceiling(value / 5) * 5;

    private static DateTimeOffset At(DateTime local, Func<DateTime, TimeSpan> offsetOf)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, offsetOf(unspecified));
    }
}
