using System.Diagnostics;
using PcChecker.Core.Common;

namespace PcChecker.Core.Benchmark;

/// <summary>Throughput samples from a processor test, one list per kernel (units per second, see <see cref="CpuWorkloadSize.WorkUnits"/>).</summary>
public sealed record CpuMeasurement(int Threads, IReadOnlyDictionary<CpuKernel, IReadOnlyList<double>> Throughput, int Repeats, int ChecksumErrors, TimeSpan Elapsed);

/// <summary>
/// Times the <see cref="CpuKernels"/> on one thread (single-core) or on every logical processor at once (multi-core).
/// One repeat runs each kernel once; a warm-up repeat (not counted) lets the JIT finish and records the expected
/// checksums. Every later result is compared with them, so a processor that computes a wrong answer is caught.
/// Pure .NET, so the same code runs in the tests on tiny sizes.
/// </summary>
public static class CpuBenchmarkRunner
{
    public static IReadOnlyList<CpuKernel> Kernels { get; } = Enum.GetValues<CpuKernel>();

    /// <summary>Runs the test on a dedicated thread; throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    public static Task<CpuMeasurement> RunAsync(int threads, CpuWorkloadSize size, TimeSpan budget, IProgress<ProgressUpdate>? progress, string stage, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(() => Run(threads, size, budget, progress, stage, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static CpuMeasurement Run(int threads, CpuWorkloadSize size, TimeSpan budget, IProgress<ProgressUpdate>? progress, string stage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(size);
        threads = Math.Max(1, threads);
        progress?.Report(new ProgressUpdate(stage, 0, "Preparing the test data…"));
        var data = CpuWorkloadData.Create(size);
        var scratch = Enumerable.Range(0, threads).Select(_ => new CpuScratch(size)).ToArray();
        var results = new ulong[threads];

        // Warm-up: every thread runs every kernel once; thread 0's answers become the expected checksums.
        var expected = new Dictionary<CpuKernel, ulong>();
        foreach (var kernel in Kernels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunOnAll(kernel, threads, data, scratch, results);
            expected[kernel] = results[0];
        }

        var samples = Kernels.ToDictionary(k => k, _ => new List<double>());
        int repeats = 0, errors = 0;
        var watch = Stopwatch.StartNew();
        while (BenchmarkTiming.ShouldContinue(repeats, watch.Elapsed, budget))
        {
            foreach (var kernel in Kernels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long start = Stopwatch.GetTimestamp();
                RunOnAll(kernel, threads, data, scratch, results);
                double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                errors += results.Count(r => r != expected[kernel]);
                if (seconds > 0)
                {
                    samples[kernel].Add(threads * size.WorkUnits(kernel) / seconds);
                }
            }

            repeats++;
            progress?.Report(new ProgressUpdate(stage, BenchmarkTiming.Percent(watch.Elapsed, budget), $"Repeat {repeats}"));
        }

        watch.Stop();
        return new CpuMeasurement(threads, samples.ToDictionary(p => p.Key, p => (IReadOnlyList<double>)p.Value), repeats, errors, watch.Elapsed);
    }

    private static void RunOnAll(CpuKernel kernel, int threads, CpuWorkloadData data, CpuScratch[] scratch, ulong[] results)
    {
        if (threads == 1)
        {
            results[0] = CpuKernels.Run(kernel, data, scratch[0]);
            return;
        }

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t => results[t] = CpuKernels.Run(kernel, data, scratch[t]));
    }
}
