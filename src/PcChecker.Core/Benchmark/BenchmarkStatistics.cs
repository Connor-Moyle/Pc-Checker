namespace PcChecker.Core.Benchmark;

/// <summary>
/// Pure statistics for repeated measurements: the median, Tukey outlier removal (1.5 × the interquartile range) and
/// the spread of the repeats. A benchmark reports the median of the repeats left after outliers are dropped, so one
/// repeat slowed down by a background task does not move the score.
/// </summary>
public static class BenchmarkStatistics
{
    /// <summary>Fewer samples than this are never trimmed: there is not enough data to call one an outlier.</summary>
    public const int MinSamplesForOutlierRemoval = 4;

    /// <summary>Median of the finite values; NaN when there are none.</summary>
    public static double Median(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Where(double.IsFinite).OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>
    /// Quartile by linear interpolation between closest ranks (the method spreadsheets use for QUARTILE.INC).
    /// <paramref name="sorted"/> must be sorted ascending and not empty.
    /// </summary>
    public static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        double position = Math.Clamp(q, 0, 1) * (sorted.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Count - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    /// <summary>
    /// The finite values without Tukey outliers (below Q1 − 1.5·IQR or above Q3 + 1.5·IQR), in their original order.
    /// Fewer than <see cref="MinSamplesForOutlierRemoval"/> values come back unchanged.
    /// </summary>
    public static IReadOnlyList<double> RemoveOutliers(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var finite = values.Where(double.IsFinite).ToList();
        if (finite.Count < MinSamplesForOutlierRemoval)
        {
            return finite;
        }

        var sorted = finite.OrderBy(v => v).ToList();
        double q1 = Quantile(sorted, 0.25);
        double q3 = Quantile(sorted, 0.75);
        double fence = 1.5 * (q3 - q1);
        double low = q1 - fence, high = q3 + fence;
        return finite.Where(v => v >= low && v <= high).ToList();
    }

    /// <summary>Median of the repeats after outliers are removed.</summary>
    public static double RobustMedian(IEnumerable<double> values) => Median(RemoveOutliers(values));

    /// <summary>
    /// Coefficient of variation in percent (sample standard deviation ÷ mean × 100); null for fewer than two values or
    /// a zero mean.
    /// </summary>
    public static double? SpreadPercent(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var list = values.Where(double.IsFinite).ToList();
        if (list.Count < 2)
        {
            return null;
        }

        double mean = list.Average();
        if (mean == 0)
        {
            return null;
        }

        double variance = list.Sum(v => (v - mean) * (v - mean)) / (list.Count - 1);
        return Math.Sqrt(variance) / Math.Abs(mean) * 100;
    }

    /// <summary>Geometric mean of the positive values; NaN when there are none.</summary>
    public static double GeometricMean(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var positive = values.Where(v => double.IsFinite(v) && v > 0).ToList();
        return positive.Count == 0 ? double.NaN : Math.Exp(positive.Average(Math.Log));
    }
}

/// <summary>How many repeats a timed test makes: until its time is used, but at least a few and never far over.</summary>
public static class BenchmarkTiming
{
    /// <summary>Measured repeats every timed test makes, however slow the PC.</summary>
    public const int MinRepeats = 3;

    /// <summary>Upper bound on repeats so a very fast PC does not collect thousands of samples.</summary>
    public const int MaxRepeats = 500;

    /// <summary>A test stops after this multiple of its time even before <see cref="MinRepeats"/> (a very slow PC).</summary>
    public const double HardLimitFactor = 1.75;

    /// <summary>True while another repeat should run.</summary>
    public static bool ShouldContinue(int repeats, TimeSpan elapsed, TimeSpan budget)
    {
        if (repeats >= MaxRepeats)
        {
            return false;
        }

        if (repeats > 0 && elapsed.TotalSeconds >= budget.TotalSeconds * HardLimitFactor)
        {
            return false;
        }

        return repeats < MinRepeats || elapsed < budget;
    }

    /// <summary>Progress through a timed test in percent (0–99 while running; 100 is reported when it ends).</summary>
    public static double Percent(TimeSpan elapsed, TimeSpan budget) =>
        budget <= TimeSpan.Zero ? 0 : Math.Clamp(elapsed.TotalSeconds / budget.TotalSeconds * 100, 0, 99);
}
