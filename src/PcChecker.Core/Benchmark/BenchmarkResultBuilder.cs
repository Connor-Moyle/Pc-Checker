using PcChecker.Core.Storage;

namespace PcChecker.Core.Benchmark;

/// <summary>
/// Pure conversion of raw measurements into <see cref="BenchmarkTestResult"/>s: the median of the repeats after outlier
/// removal for each value, the score, and a note when the repeats were unsteady or a check failed. The Windows and
/// demo services both build their results here, so the scoring is identical.
/// </summary>
public static class BenchmarkResultBuilder
{
    /// <summary>Repeats that varied more than this (coefficient of variation, %) get a note.</summary>
    public const double UnsteadySpreadPercent = 10;

    public static BenchmarkTestResult FromCpu(CpuMeasurement measurement, bool multiCore, double seconds)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        var test = multiCore ? BenchmarkTestKind.CpuMultiCore : BenchmarkTestKind.CpuSingleCore;
        var metrics = new List<BenchmarkMetric>();
        var spreads = new List<double>();
        foreach (var kernel in CpuBenchmarkRunner.Kernels)
        {
            var baseline = BenchmarkBaseline.ForKernel(kernel, multiCore);
            var samples = measurement.Throughput.TryGetValue(kernel, out var list) ? list : [];
            var kept = BenchmarkStatistics.RemoveOutliers(samples);
            double median = BenchmarkStatistics.Median(kept);
            if (double.IsFinite(median))
            {
                metrics.Add(new BenchmarkMetric(baseline.Key, baseline.Name, median, baseline.Unit));
            }

            if (BenchmarkStatistics.SpreadPercent(kept) is { } spread)
            {
                spreads.Add(spread);
            }
        }

        if (multiCore)
        {
            metrics.Add(new BenchmarkMetric("cpuN.threads", "Threads used", measurement.Threads, "threads", Informational: true));
        }

        double? spreadPercent = spreads.Count == 0 ? null : spreads.Average();
        string? note = measurement.ChecksumErrors > 0
            ? $"The processor returned {measurement.ChecksumErrors} wrong answers. That points to an unstable overclock, undervolt or overheating; run the stress test on the Overclock page."
            : UnsteadyNote(spreadPercent);
        return Build(test, metrics, note, seconds, measurement.Repeats, spreadPercent);
    }

    public static BenchmarkTestResult FromBandwidth(MemoryBandwidthMeasurement measurement, double seconds)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        var copy = BenchmarkStatistics.RemoveOutliers(measurement.CopyGBps);
        double copyMedian = BenchmarkStatistics.Median(copy);
        double writeMedian = BenchmarkStatistics.RobustMedian(measurement.WriteGBps);
        var metrics = new List<BenchmarkMetric>();
        if (double.IsFinite(copyMedian))
        {
            metrics.Add(new BenchmarkMetric("mem.copy", "Copy", copyMedian, "GB/s"));
        }

        if (double.IsFinite(writeMedian))
        {
            metrics.Add(new BenchmarkMetric("mem.write", "Write", writeMedian, "GB/s"));
        }

        metrics.Add(new BenchmarkMetric("mem.buffer", "Buffer size", measurement.BufferBytes / (double)MemoryBenchmarkPlan.Megabyte, "MB", Informational: true));
        metrics.Add(new BenchmarkMetric("mem.threads", "Threads used", measurement.Threads, "threads", Informational: true));
        double? spread = BenchmarkStatistics.SpreadPercent(copy);
        return Build(BenchmarkTestKind.MemoryBandwidth, metrics, UnsteadyNote(spread), seconds, measurement.Repeats, spread);
    }

    public static BenchmarkTestResult FromLatency(MemoryLatencyMeasurement measurement, double seconds)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        var kept = BenchmarkStatistics.RemoveOutliers(measurement.Nanoseconds);
        double median = BenchmarkStatistics.Median(kept);
        var metrics = new List<BenchmarkMetric>();
        if (double.IsFinite(median))
        {
            metrics.Add(new BenchmarkMetric("mem.latency", "Latency", median, "ns", LowerIsBetter: true));
        }

        metrics.Add(new BenchmarkMetric("mem.chain", "Buffer size", measurement.BufferBytes / (double)MemoryBenchmarkPlan.Megabyte, "MB", Informational: true));
        double? spread = BenchmarkStatistics.SpreadPercent(kept);
        return Build(BenchmarkTestKind.MemoryLatency, metrics, UnsteadyNote(spread), seconds, measurement.Repeats, spread);
    }

    /// <summary>Scores a Storage benchmark result; a result without speeds is a failure with the service's reason.</summary>
    public static BenchmarkTestResult FromDisk(DiskBenchmarkResult result, double seconds)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.SeqReadMBps <= 0 && result.SeqWriteMBps <= 0)
        {
            return BenchmarkTestResult.Fail(BenchmarkTestKind.Disk,
                string.IsNullOrWhiteSpace(result.Summary) ? "The drive test did not return any speeds." : result.Summary, seconds);
        }

        var metrics = new List<BenchmarkMetric>
        {
            new("disk.seqread", "Sequential read", result.SeqReadMBps, "MB/s"),
            new("disk.seqwrite", "Sequential write", result.SeqWriteMBps, "MB/s"),
            new("disk.rand4kread", "Random 4K read", result.Rand4kReadMBps, "MB/s"),
            new("disk.rand4kwrite", "Random 4K write", result.Rand4kWriteMBps, "MB/s"),
            new("disk.rand4kreadiops", "Random 4K read", result.Rand4kReadIops, "IOPS"),
            new("disk.rand4kwriteiops", "Random 4K write", result.Rand4kWriteIops, "IOPS"),
        };
        return Build(BenchmarkTestKind.Disk, metrics, $"Drive {result.Drive}, {result.TestFileBytes / MemoryBenchmarkPlan.Megabyte} MB test file (deleted afterwards).", seconds, 1, null);
    }

    /// <summary>
    /// Scores the GPU load: throughput = median dispatch rate × elements per dispatch × loop steps per element.
    /// </summary>
    public static BenchmarkTestResult FromGpu(IReadOnlyList<double> dispatchesPerSecond, long elements, int iterations, string adapterName, double seconds)
    {
        ArgumentNullException.ThrowIfNull(dispatchesPerSecond);
        var kept = BenchmarkStatistics.RemoveOutliers(dispatchesPerSecond.Where(r => r > 0));
        double rate = BenchmarkStatistics.Median(kept);
        if (!double.IsFinite(rate) || elements <= 0 || iterations <= 0)
        {
            return BenchmarkTestResult.Fail(BenchmarkTestKind.GpuCompute, $"{adapterName} did not report any finished work.", seconds);
        }

        var metrics = new List<BenchmarkMetric>
        {
            new("gpu.throughput", "Shader throughput", GpuStepsPerSecond(rate, elements, iterations) / 1e9, "G steps/s"),
            new("gpu.dispatches", "Dispatches", rate, "per second"),
        };
        double? spread = BenchmarkStatistics.SpreadPercent(kept);
        string note = adapterName + (UnsteadyNote(spread) is { } unsteady ? ". " + unsteady : ".");
        return Build(BenchmarkTestKind.GpuCompute, metrics, note, seconds, kept.Count, spread);
    }

    /// <summary>
    /// The dispatch rates that belong to the final calibration: samples taken with <paramref name="finalIterations"/>
    /// once that setting had run for at least <paramref name="settle"/> (the rate meter's window, so no sample mixes in
    /// dispatches of another size) and not before <paramref name="warmUp"/>. Samples that do not report their
    /// iterations (0) count after the warm-up only. Falls back to every sample with the final iterations when none had
    /// settled, so a slow card still gets a result.
    /// </summary>
    public static IReadOnlyList<double> SteadyGpuRates(IReadOnlyList<GpuRateSample> samples, int finalIterations, TimeSpan warmUp, TimeSpan settle)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var ordered = samples.Where(s => s.DispatchesPerSecond > 0).OrderBy(s => s.Elapsed).ToList();
        if (ordered.Count > 0 && ordered.All(s => s.Iterations == 0))
        {
            return ordered.Where(s => s.Elapsed >= warmUp).Select(s => s.DispatchesPerSecond).ToList();
        }

        // The final setting was in use by the first sample that reports it after the last sample that used another one.
        TimeSpan lastOther = TimeSpan.MinValue;
        foreach (var s in ordered)
        {
            if (s.Iterations != 0 && s.Iterations != finalIterations)
            {
                lastOther = s.Elapsed;
            }
        }

        var final = ordered.Where(s => s.Iterations == finalIterations && s.Elapsed > lastOther).ToList();
        if (final.Count == 0)
        {
            return [];
        }

        var since = final[0].Elapsed;
        var steady = final.Where(s => s.Elapsed >= warmUp && s.Elapsed - since >= settle).Select(s => s.DispatchesPerSecond).ToList();
        return steady.Count > 0 ? steady : final.Select(s => s.DispatchesPerSecond).ToList();
    }

    /// <summary>Shader loop steps per second for a dispatch rate.</summary>
    public static double GpuStepsPerSecond(double dispatchesPerSecond, long elements, int iterations) =>
        dispatchesPerSecond <= 0 || elements <= 0 || iterations <= 0 ? 0 : dispatchesPerSecond * elements * iterations;

    /// <summary>A note asking to rerun when the repeats varied by more than <see cref="UnsteadySpreadPercent"/>.</summary>
    public static string? UnsteadyNote(double? spreadPercent) =>
        spreadPercent is { } s && s > UnsteadySpreadPercent
            ? $"Repeats varied by about {Math.Round(s):0} %, so something else was probably using the PC. Close other programs and run it again."
            : null;

    private static BenchmarkTestResult Build(BenchmarkTestKind test, IReadOnlyList<BenchmarkMetric> metrics, string? note, double seconds, int repeats, double? spread)
    {
        int? score = BenchmarkScoring.ScoreTest(test, metrics);
        if (score is null)
        {
            return BenchmarkTestResult.Fail(test, note ?? "The test did not produce a measurement.", seconds);
        }

        return new BenchmarkTestResult(test, BenchmarkTestStatus.Completed, score, metrics, note, Math.Round(seconds, 1))
        {
            Repeats = repeats,
            SpreadPercent = spread is { } s ? Math.Round(s, 1) : null,
        };
    }
}

/// <summary>One dispatch-rate reading of the graphics test, with the iterations per element in use at the time.</summary>
public readonly record struct GpuRateSample(TimeSpan Elapsed, double DispatchesPerSecond, int Iterations);

