namespace PcChecker.Core.Benchmark;

/// <summary>
/// Pure scoring: each measured value is divided by the reference PC's value (inverted for times, where lower is
/// better) and multiplied by 1000. A test's score is the geometric mean of its values' scores, and the overall score is
/// the geometric mean of the tests that completed, so doubling any one number raises the result by the same factor
/// wherever it happens.
/// </summary>
public static class BenchmarkScoring
{
    public const int MinScore = 1;
    public const int MaxScore = 1_000_000;

    /// <summary>Score bars run from 0 to this; the reference (1000) sits in the middle.</summary>
    public const double BarScale = 2 * BenchmarkBaseline.ReferenceScore;

    /// <summary>Score of one measurement against the reference; 0 when either number is missing or not positive.</summary>
    public static int Score(double measured, double reference, bool lowerIsBetter = false)
    {
        if (!double.IsFinite(measured) || !double.IsFinite(reference) || measured <= 0 || reference <= 0)
        {
            return 0;
        }

        double ratio = lowerIsBetter ? reference / measured : measured / reference;
        return (int)Math.Clamp(Math.Round(ratio * BenchmarkBaseline.ReferenceScore), MinScore, MaxScore);
    }

    /// <summary>Geometric mean of the positive scores, rounded; null when there are none.</summary>
    public static int? Combine(IEnumerable<int> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        double mean = BenchmarkStatistics.GeometricMean(scores.Where(s => s > 0).Select(s => (double)s));
        return double.IsNaN(mean) ? null : (int)Math.Clamp(Math.Round(mean), MinScore, MaxScore);
    }

    /// <summary>
    /// A test's score from its metrics: every baseline value of the test that has a matching metric is scored and the
    /// scores combined. Null when no scored metric is present.
    /// </summary>
    public static int? ScoreTest(BenchmarkTestKind test, IEnumerable<BenchmarkMetric> metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var byKey = metrics.GroupBy(m => m.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var scores = new List<int>();
        foreach (var baseline in BenchmarkBaseline.For(test))
        {
            if (byKey.TryGetValue(baseline.Key, out var metric))
            {
                int score = Score(metric.Value, baseline.Reference, baseline.LowerIsBetter);
                if (score > 0)
                {
                    scores.Add(score);
                }
            }
        }

        return Combine(scores);
    }

    /// <summary>Overall score: geometric mean of the completed tests' scores; null when none completed.</summary>
    public static int? Overall(IEnumerable<BenchmarkTestResult> tests)
    {
        ArgumentNullException.ThrowIfNull(tests);
        return Combine(tests.Where(t => t.HasScore).Select(t => t.Score!.Value));
    }

    /// <summary>Length of a score bar from 0 to 1, where 0.5 is the reference PC.</summary>
    public static double BarFraction(int? score) => score is > 0 ? Math.Clamp(score.Value / BarScale, 0, 1) : 0;

    /// <summary>One-line verdict for a score compared with the reference PC.</summary>
    public static string Rating(int? score) => score switch
    {
        null or <= 0 => "No score",
        >= 1300 => "Well above the reference PC",
        >= 1080 => "Above the reference PC",
        >= 920 => "About the same as the reference PC",
        >= 650 => "Below the reference PC",
        _ => "Well below the reference PC",
    };
}

/// <summary>Names and one-line descriptions of the tests, in run order.</summary>
public static class BenchmarkCatalog
{
    public static IReadOnlyList<BenchmarkTestKind> All { get; } = Enum.GetValues<BenchmarkTestKind>();

    public static string Name(BenchmarkTestKind test) => test switch
    {
        BenchmarkTestKind.CpuSingleCore => "Processor, single-core",
        BenchmarkTestKind.CpuMultiCore => "Processor, multi-core",
        BenchmarkTestKind.MemoryBandwidth => "Memory bandwidth",
        BenchmarkTestKind.MemoryLatency => "Memory latency",
        BenchmarkTestKind.Disk => "System drive",
        BenchmarkTestKind.GpuCompute => "Graphics compute",
        _ => test.ToString(),
    };

    public static string Description(BenchmarkTestKind test) => test switch
    {
        BenchmarkTestKind.CpuSingleCore => "Compression, a prime sieve, matrix maths and SHA-256 on one core. Drives how snappy apps and most games feel.",
        BenchmarkTestKind.CpuMultiCore => "The same work on every core at once. Matters for video export, compiling and heavy multitasking.",
        BenchmarkTestKind.MemoryBandwidth => "How fast a large block of memory can be copied, using several cores.",
        BenchmarkTestKind.MemoryLatency => "How long the processor waits for one value from memory. Lower is better.",
        BenchmarkTestKind.Disk => "Sequential and random 4K reads and writes on the Windows drive, with a small temporary file.",
        BenchmarkTestKind.GpuCompute => "Shader maths on the graphics card through DirectX 11. Skipped when there is no DirectX 11 card.",
        _ => string.Empty,
    };

    /// <summary>True for tests whose length follows the "seconds per test" option (disk and graphics set their own).</summary>
    public static bool IsTimed(BenchmarkTestKind test) => test is not BenchmarkTestKind.Disk;
}
