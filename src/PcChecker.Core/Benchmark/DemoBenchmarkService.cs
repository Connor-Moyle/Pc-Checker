using PcChecker.Core.Common;
using PcChecker.Core.Storage;

namespace PcChecker.Core.Benchmark;

/// <summary>
/// Sample measurements for the demo desktop (Core i7-13700K, DDR5-6000, Samsung 990 PRO, GeForce RTX 4070). They go
/// through the same <see cref="BenchmarkResultBuilder"/> as real measurements, so the demo shows real scoring.
/// </summary>
public static class DemoBenchmarkData
{
    public const int Threads = 24;

    private static readonly double[] CpuSingle = [150, 900, 8.6, 2600];
    private static readonly double[] CpuMulti = [1450, 7200, 78, 26000];

    public static BenchmarkSystemInfo System { get; } =
        new(DemoData.ComputerName, DemoData.CpuName, Threads, DemoData.TotalMemoryBytes, DemoData.GpuName, "C:");

    /// <summary>
    /// A result for <paramref name="test"/> at <paramref name="factor"/> × the demo PC's usual speed, with a little
    /// repeat-to-repeat jitter from <paramref name="random"/>.
    /// </summary>
    public static BenchmarkTestResult Result(BenchmarkTestKind test, double factor, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        IReadOnlyList<double> Samples(double value, int count = 24) =>
            Enumerable.Range(0, count).Select(_ => DemoData.Jitter(random, value * factor, value * factor * 0.02)).ToList();

        switch (test)
        {
            case BenchmarkTestKind.CpuSingleCore:
            case BenchmarkTestKind.CpuMultiCore:
                bool multi = test == BenchmarkTestKind.CpuMultiCore;
                var values = multi ? CpuMulti : CpuSingle;
                var throughput = CpuBenchmarkRunner.Kernels.ToDictionary(k => k, k => Samples(values[(int)k]));
                return BenchmarkResultBuilder.FromCpu(new CpuMeasurement(multi ? Threads : 1, throughput, 24, 0, TimeSpan.FromSeconds(12)), multi, 12.4);
            case BenchmarkTestKind.MemoryBandwidth:
                return BenchmarkResultBuilder.FromBandwidth(
                    new MemoryBandwidthMeasurement(Samples(34.5, 60), Samples(51, 60), 256 * MemoryBenchmarkPlan.Megabyte, 16, 60, TimeSpan.FromSeconds(12)), 13.1);
            case BenchmarkTestKind.MemoryLatency:
                return BenchmarkResultBuilder.FromLatency(
                    new MemoryLatencyMeasurement(Enumerable.Range(0, 110).Select(_ => DemoData.Jitter(random, 96 / factor, 1.5)).ToList(), 128 * MemoryBenchmarkPlan.Megabyte, 110, TimeSpan.FromSeconds(12)), 12.6);
            case BenchmarkTestKind.Disk:
                var disk = new DiskBenchmarkResult("C:", Math.Round(7120 * factor), Math.Round(6540 * factor), Math.Round(82 * factor, 1), Math.Round(215 * factor, 1),
                    Math.Round(21000 * factor), Math.Round(55000 * factor), 256 * MemoryBenchmarkPlan.Megabyte, string.Empty);
                return BenchmarkResultBuilder.FromDisk(disk, 4.8);
            case BenchmarkTestKind.GpuCompute:
                return BenchmarkResultBuilder.FromGpu(Samples(9.99, 40), 1024L * 1024 * 1024 / 16, 343, DemoData.GpuName, 15.2);
            default:
                return BenchmarkTestResult.Skip(test, "Unknown test.");
        }
    }

    /// <summary>
    /// Three runs on the demo PC: before Optimize (with a busy-processor warning), after Optimize, and after a GPU
    /// overclock two days ago. Newest first.
    /// </summary>
    public static IReadOnlyList<BenchmarkRun> SampleRuns(DateTimeOffset now)
    {
        var random = DemoData.CreateRandom(1000);
        var busy = BenchmarkConditionRules.Evaluate(new BenchmarkConditionFacts(false, null, 23, 54, 41)).Warnings;
        BenchmarkRun Run(int id, DateTimeOffset started, string note, double cpu, double memory, double disk, double gpu, IReadOnlyList<string> warnings)
        {
            var tests = BenchmarkCatalog.All.Select(t => Result(t, t switch
            {
                BenchmarkTestKind.CpuSingleCore or BenchmarkTestKind.CpuMultiCore => cpu,
                BenchmarkTestKind.MemoryBandwidth or BenchmarkTestKind.MemoryLatency => memory,
                BenchmarkTestKind.Disk => disk,
                _ => gpu,
            }, random)).ToList();
            return new BenchmarkRun(new Guid(id, 0x2024, 0x1027, 0, 0, 0, 0, 0, 0, 0, (byte)id), started, 82.5, note, System, tests, warnings);
        }

        var day = now.Date;
        DateTimeOffset At(int daysAgo, int hour, int minute) => new(day.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute), now.Offset);
        return
        [
            Run(3, At(2, 21, 10), "After GPU overclock", 1.0, 1.0, 1.0, 1.08, []),
            Run(2, At(9, 20, 25), "After Optimize", 1.0, 1.0, 1.0, 1.0, []),
            Run(1, At(9, 19, 40), "Before Optimize", 0.95, 0.98, 0.96, 0.98, busy),
        ];
    }
}

/// <summary>Demo benchmark: animated progress and sample results, without loading the PC.</summary>
public sealed class DemoBenchmarkService : IBenchmarkService
{
    private readonly Random _random = DemoData.CreateRandom(2026);
    private readonly TimeSpan _tick;

    /// <param name="tick">Delay between progress steps (shorter in tests).</param>
    public DemoBenchmarkService(TimeSpan? tick = null)
    {
        _tick = tick ?? TimeSpan.FromMilliseconds(120);
    }

    public string? SystemDrive => "C:";

    public int DiskTestFileMb => 256;

    public Task<BenchmarkSystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken) => Task.FromResult(DemoBenchmarkData.System);

    public async Task<BenchmarkConditions> CheckConditionsAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(_tick, cancellationToken).ConfigureAwait(false);
        return BenchmarkConditionRules.Evaluate(new BenchmarkConditionFacts(false, null, 4, 46, 38));
    }

    public async Task<BenchmarkTestResult> RunTestAsync(BenchmarkTestKind test, BenchmarkOptions options, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        const int steps = 10;
        for (int i = 0; i < steps; i++)
        {
            await Task.Delay(_tick, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ProgressUpdate(BenchmarkCatalog.Name(test), 100.0 * (i + 1) / steps, $"Repeat {i + 1} (demo)"));
        }

        lock (_random)
        {
            return DemoBenchmarkData.Result(test, 1.0, _random);
        }
    }
}
