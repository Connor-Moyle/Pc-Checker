using System.Text.Json.Serialization;

namespace PcChecker.Core.Benchmark;

/// <summary>The tests the Benchmark page can run, in the order a full run goes through them.</summary>
public enum BenchmarkTestKind
{
    CpuSingleCore,
    CpuMultiCore,
    MemoryBandwidth,
    MemoryLatency,
    Disk,
    GpuCompute,
}

/// <summary>How one test ended.</summary>
public enum BenchmarkTestStatus
{
    /// <summary>The test ran and has a score.</summary>
    Completed,

    /// <summary>The test could not run on this PC (no DirectX 11 graphics card, no system drive); no score.</summary>
    Skipped,

    /// <summary>The test started but could not finish (drive error, graphics driver reset); no score.</summary>
    Failed,
}

/// <summary>
/// One raw measurement, e.g. "Compression · 412 MB/s". <paramref name="Key"/> is a stable id used to match the same
/// number across runs; <paramref name="LowerIsBetter"/> is true for times such as memory latency;
/// <paramref name="Informational"/> marks a setting of the test (threads, buffer size) rather than a measurement.
/// </summary>
public sealed record BenchmarkMetric(string Key, string Name, double Value, string Unit, bool LowerIsBetter = false, bool Informational = false);

/// <summary>
/// Result of one test: its score (1000 = the reference PC), the raw numbers behind it, and how steady the repeats were.
/// </summary>
public sealed record BenchmarkTestResult(
    BenchmarkTestKind Test,
    BenchmarkTestStatus Status,
    int? Score,
    IReadOnlyList<BenchmarkMetric> Metrics,
    string? Note,
    double Seconds)
{
    /// <summary>Measured repeats that went into the median (after outliers were dropped).</summary>
    public int Repeats { get; init; }

    /// <summary>Spread of the repeats around the median in percent (coefficient of variation); null when unknown.</summary>
    public double? SpreadPercent { get; init; }

    [JsonIgnore]
    public bool HasScore => Status == BenchmarkTestStatus.Completed && Score is > 0;

    public static BenchmarkTestResult Skip(BenchmarkTestKind test, string reason) =>
        new(test, BenchmarkTestStatus.Skipped, null, [], reason, 0);

    public static BenchmarkTestResult Fail(BenchmarkTestKind test, string reason, double seconds = 0) =>
        new(test, BenchmarkTestStatus.Failed, null, [], reason, seconds);
}

/// <summary>The PC a run was made on, so a shared result or a comparison says what it describes.</summary>
public sealed record BenchmarkSystemInfo(
    string ComputerName,
    string CpuName,
    int LogicalProcessors,
    long MemoryBytes,
    string? GpuName,
    string? SystemDrive)
{
    public static BenchmarkSystemInfo Unknown { get; } = new(Environment.MachineName, "Unknown processor", Environment.ProcessorCount, 0, null, null);
}

/// <summary>
/// One benchmark run as it is kept in the history: when, an optional note ("Before overclock"), the PC, every test
/// result, and the warnings that applied when it started (on battery, busy, hot).
/// </summary>
public sealed record BenchmarkRun(
    Guid Id,
    DateTimeOffset Started,
    double Seconds,
    string? Note,
    BenchmarkSystemInfo System,
    IReadOnlyList<BenchmarkTestResult> Tests,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Version of the reference numbers the scores were computed with (see <see cref="BenchmarkBaseline.Version"/>).</summary>
    public int ScoringVersion { get; init; } = BenchmarkBaseline.Version;

    /// <summary>True when the user stopped the run before every chosen test had finished.</summary>
    public bool StoppedEarly { get; init; }

    /// <summary>Overall score: the geometric mean of the completed tests, or null when none completed.</summary>
    [JsonIgnore]
    public int? OverallScore => BenchmarkScoring.Overall(Tests);

    /// <summary>The result for <paramref name="test"/>, or null when it was not part of this run.</summary>
    public BenchmarkTestResult? Find(BenchmarkTestKind test) => Tests.FirstOrDefault(t => t.Test == test);

    /// <summary>The tests that produced a score, in run order.</summary>
    [JsonIgnore]
    public IReadOnlyList<BenchmarkTestKind> ScoredTests => Tests.Where(t => t.HasScore).Select(t => t.Test).ToList();
}

/// <summary>What the service measured just before a run: power source, how busy the processor is and temperatures.</summary>
public sealed record BenchmarkConditionFacts(
    bool? OnBattery,
    int? BatteryPercent,
    double? CpuBusyPercent,
    double? CpuTempC,
    double? GpuTempC);

/// <summary>The facts plus the plain-language warnings derived from them (empty when conditions are good).</summary>
public sealed record BenchmarkConditions(BenchmarkConditionFacts Facts, IReadOnlyList<string> Warnings)
{
    public bool IsIdeal => Warnings.Count == 0;
}

/// <summary>How long each timed test runs. Disk and graphics tests take what their workload needs.</summary>
public sealed record BenchmarkOptions(int SecondsPerTest)
{
    public const int MinSeconds = 10;
    public const int MaxSeconds = 20;
    public const int DefaultSeconds = 12;

    public static BenchmarkOptions Default { get; } = new(DefaultSeconds);

    /// <summary><see cref="SecondsPerTest"/> kept within 10–20 s.</summary>
    public int ClampedSeconds => Math.Clamp(SecondsPerTest, MinSeconds, MaxSeconds);
}
