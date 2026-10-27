namespace PcChecker.Core.Benchmark;

/// <summary>Direction of a change between two runs, from the user's point of view.</summary>
public enum BenchmarkChange
{
    /// <summary>Within normal run-to-run variation.</summary>
    Same,
    Better,
    Worse,

    /// <summary>One side is missing, so there is nothing to compare.</summary>
    NotComparable,
}

/// <summary>One row of a comparison: a test (or a raw value) before and after, and the change in percent.</summary>
public sealed record BenchmarkComparisonRow(
    string Name,
    BenchmarkTestKind? Test,
    double? Before,
    double? After,
    string Unit,
    double? ChangePercent,
    BenchmarkChange Change)
{
    /// <summary>True for a raw value inside a test, false for a score row.</summary>
    public bool IsDetail { get; init; }
}

/// <summary>Two runs side by side: the overall row, one row per test, the raw values under each test, and notes.</summary>
public sealed record BenchmarkComparison(
    BenchmarkRun Before,
    BenchmarkRun After,
    BenchmarkComparisonRow Overall,
    IReadOnlyList<BenchmarkComparisonRow> Tests,
    IReadOnlyList<BenchmarkComparisonRow> Details,
    IReadOnlyList<string> Notes);

/// <summary>
/// Pure comparison maths. A change smaller than <see cref="NoiseThresholdPercent"/> counts as "the same", because
/// repeated runs on an unchanged PC differ by about that much. For values where lower is better (latency) a drop is
/// an improvement. The overall row is only compared when both runs completed the same tests, since leaving a test out
/// changes the overall score by itself.
/// </summary>
public static class BenchmarkComparer
{
    /// <summary>Changes within ±3 % are treated as normal variation.</summary>
    public const double NoiseThresholdPercent = 3;

    /// <summary>(after − before) ÷ before × 100; null when either is missing or before is not positive.</summary>
    public static double? ChangePercent(double? before, double? after)
    {
        if (before is not { } b || after is not { } a || !double.IsFinite(b) || !double.IsFinite(a) || b <= 0)
        {
            return null;
        }

        return (a - b) / b * 100;
    }

    /// <summary>Classifies a change in percent; for lower-is-better values a negative change is an improvement.</summary>
    public static BenchmarkChange Classify(double? changePercent, bool lowerIsBetter = false, double threshold = NoiseThresholdPercent)
    {
        if (changePercent is not { } change || !double.IsFinite(change))
        {
            return BenchmarkChange.NotComparable;
        }

        if (Math.Abs(change) < threshold)
        {
            return BenchmarkChange.Same;
        }

        bool up = change > 0;
        return up != lowerIsBetter ? BenchmarkChange.Better : BenchmarkChange.Worse;
    }

    /// <summary>True when both runs completed exactly the same tests (so their overall scores mean the same thing).</summary>
    public static bool SameTests(BenchmarkRun a, BenchmarkRun b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.ScoredTests.Count > 0 && a.ScoredTests.OrderBy(t => t).SequenceEqual(b.ScoredTests.OrderBy(t => t));
    }

    public static BenchmarkComparison Compare(BenchmarkRun before, BenchmarkRun after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var notes = new List<string>();
        bool sameTests = SameTests(before, after);

        var overall = sameTests
            ? ScoreRow("Overall score", null, before.OverallScore, after.OverallScore)
            : new BenchmarkComparisonRow("Overall score", null, before.OverallScore, after.OverallScore, "points", null, BenchmarkChange.NotComparable);
        if (!sameTests)
        {
            notes.Add("The runs did not complete the same tests, so their overall scores are not compared. Compare the tests they share instead.");
        }

        var tests = new List<BenchmarkComparisonRow>();
        var details = new List<BenchmarkComparisonRow>();
        foreach (var kind in BenchmarkCatalog.All)
        {
            var b = before.Find(kind);
            var a = after.Find(kind);
            if (b is null && a is null)
            {
                continue;
            }

            tests.Add(ScoreRow(BenchmarkCatalog.Name(kind), kind, b?.HasScore == true ? b.Score : null, a?.HasScore == true ? a.Score : null));
            if (b?.HasScore == true && a?.HasScore == true)
            {
                details.AddRange(CompareMetrics(kind, b.Metrics, a.Metrics));
            }
        }

        if (before.ScoringVersion != after.ScoringVersion)
        {
            notes.Add("These runs were scored with different reference numbers (an app update changed them). Compare the raw values rather than the scores.");
        }

        if (!string.Equals(before.System.CpuName, after.System.CpuName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(before.System.GpuName ?? string.Empty, after.System.GpuName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("The processor or graphics card is different between these runs.");
        }

        if (before.Warnings.Count > 0 || after.Warnings.Count > 0)
        {
            notes.Add("At least one run started with a warning (battery, heat or background activity); its scores may be lower than the PC can do.");
        }

        return new BenchmarkComparison(before, after, overall, tests, details, notes);
    }

    /// <summary>Rows for the raw values both tests measured (not settings such as threads), matched by key, in the order of the newer run.</summary>
    public static IReadOnlyList<BenchmarkComparisonRow> CompareMetrics(BenchmarkTestKind test, IReadOnlyList<BenchmarkMetric> before, IReadOnlyList<BenchmarkMetric> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var rows = new List<BenchmarkComparisonRow>();
        foreach (var a in after.Where(m => !m.Informational))
        {
            var b = before.FirstOrDefault(m => m.Key == a.Key && m.Unit == a.Unit);
            if (b is null)
            {
                continue;
            }

            var change = ChangePercent(b.Value, a.Value);
            rows.Add(new BenchmarkComparisonRow(a.Name, test, b.Value, a.Value, a.Unit, change, Classify(change, a.LowerIsBetter)) { IsDetail = true });
        }

        return rows;
    }

    private static BenchmarkComparisonRow ScoreRow(string name, BenchmarkTestKind? test, int? before, int? after)
    {
        var change = ChangePercent(before, after);
        return new BenchmarkComparisonRow(name, test, before, after, "points", change, Classify(change));
    }
}
