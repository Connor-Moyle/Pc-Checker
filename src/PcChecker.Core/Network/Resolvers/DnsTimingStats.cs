namespace PcChecker.Core.Network.Resolvers;

/// <summary>Lookup times of one kind for one server. Times are null when nothing was answered.</summary>
public sealed record DnsTimingSummary(int Count, double? MedianMs, double? P90Ms, double? MinMs, double? MeanMs)
{
    public static DnsTimingSummary Empty { get; } = new(0, null, null, null, null);
}

/// <summary>Statistics for the DNS benchmark: median, 90th percentile and reliability. Pure.</summary>
public static class DnsTimingStats
{
    /// <summary>The middle value (the mean of the two middle values for an even count); null when empty.</summary>
    public static double? Median(IReadOnlyList<double> values) => Percentile(values, 50);

    /// <summary>
    /// The <paramref name="percent"/>th percentile with linear interpolation between the closest ranks (the same as
    /// Excel's PERCENTILE.INC); null when empty. NaN and negative values are ignored.
    /// </summary>
    public static double? Percentile(IReadOnlyList<double> values, double percent)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Where(v => !double.IsNaN(v) && v >= 0).OrderBy(v => v).ToList();
        if (sorted.Count == 0)
        {
            return null;
        }

        double p = Math.Clamp(percent, 0, 100) / 100.0;
        double rank = p * (sorted.Count - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }

    /// <summary>The share of lookups that got a usable answer, 0 to 1; 0 when nothing was sent.</summary>
    public static double Reliability(int answered, int sent) =>
        sent <= 0 ? 0 : Math.Clamp(answered / (double)sent, 0, 1);

    public static DnsTimingSummary Summarize(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var valid = values.Where(v => !double.IsNaN(v) && v >= 0).ToList();
        if (valid.Count == 0)
        {
            return DnsTimingSummary.Empty;
        }

        return new DnsTimingSummary(valid.Count, Median(valid), Percentile(valid, 90), valid.Min(), valid.Average());
    }
}
