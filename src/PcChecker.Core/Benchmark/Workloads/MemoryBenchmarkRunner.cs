using System.Diagnostics;
using PcChecker.Core.Common;

namespace PcChecker.Core.Benchmark;

/// <summary>Bandwidth samples in GB/s (bytes copied or written per second, 1 GB = 10⁹ bytes).</summary>
public sealed record MemoryBandwidthMeasurement(IReadOnlyList<double> CopyGBps, IReadOnlyList<double> WriteGBps, long BufferBytes, int Threads, int Repeats, TimeSpan Elapsed);

/// <summary>Latency samples in nanoseconds per dependent load.</summary>
public sealed record MemoryLatencyMeasurement(IReadOnlyList<double> Nanoseconds, long BufferBytes, int Repeats, TimeSpan Elapsed);

/// <summary>Pure sizing rules for the memory tests: big enough to miss every cache, small enough to leave the PC room.</summary>
public static class MemoryBenchmarkPlan
{
    public const long Megabyte = 1024 * 1024;
    public const long BandwidthPreferredBytes = 256 * Megabyte;
    public const long BandwidthMinimumBytes = 64 * Megabyte;
    public const long LatencyPreferredBytes = 128 * Megabyte;
    public const long LatencyMinimumBytes = 32 * Megabyte;

    /// <summary>Most threads the bandwidth test uses; more only adds contention.</summary>
    public const int MaxBandwidthThreads = 16;

    /// <summary>Bytes in one cache line: the latency test visits one value per line.</summary>
    public const int CacheLineBytes = 64;

    /// <summary>
    /// One buffer's size: the preferred size, but at most an eighth of the free memory (when known) and never below the
    /// minimum, rounded down to whole megabytes.
    /// </summary>
    public static long BufferBytes(long? freeMemoryBytes, long preferred, long minimum)
    {
        long size = preferred;
        if (freeMemoryBytes is > 0)
        {
            size = Math.Min(size, freeMemoryBytes.Value / 8);
        }

        size = Math.Max(size, minimum);
        return size / Megabyte * Megabyte;
    }

    /// <summary>Threads for the bandwidth test: every logical processor up to <see cref="MaxBandwidthThreads"/>.</summary>
    public static int BandwidthThreads(int logicalProcessors) => Math.Clamp(logicalProcessors, 1, MaxBandwidthThreads);

    /// <summary>[start, length) of chunk <paramref name="index"/> when <paramref name="total"/> bytes are split into <paramref name="count"/> chunks.</summary>
    public static (long Start, long Length) Chunk(long total, int count, int index)
    {
        count = Math.Max(1, count);
        long size = total / count;
        long start = size * index;
        long length = index == count - 1 ? total - start : size;
        return (start, length);
    }
}

/// <summary>
/// Memory bandwidth (several threads copying and writing a large buffer) and latency (a pointer chase through a
/// random single cycle, one cache line per step, so every load waits for the previous one and prefetching cannot
/// help). Pure .NET.
/// </summary>
public static class MemoryBenchmarkRunner
{
    /// <summary>Dependent loads timed per latency repeat.</summary>
    public const int ChaseStepsPerRepeat = 1 << 20;

    public static Task<MemoryBandwidthMeasurement> RunBandwidthAsync(long bufferBytes, int threads, TimeSpan budget, IProgress<ProgressUpdate>? progress, string stage, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(() => RunBandwidth(bufferBytes, threads, budget, progress, stage, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static Task<MemoryLatencyMeasurement> RunLatencyAsync(long bufferBytes, TimeSpan budget, IProgress<ProgressUpdate>? progress, string stage, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(() => RunLatency(bufferBytes, budget, progress, stage, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static MemoryBandwidthMeasurement RunBandwidth(long bufferBytes, int threads, TimeSpan budget, IProgress<ProgressUpdate>? progress, string stage, CancellationToken cancellationToken)
    {
        threads = Math.Max(1, threads);
        int length = (int)Math.Clamp(bufferBytes, 1, Array.MaxLength);
        progress?.Report(new ProgressUpdate(stage, 0, "Preparing the buffers…"));
        var source = GC.AllocateUninitializedArray<byte>(length);
        var target = GC.AllocateUninitializedArray<byte>(length);
        try
        {
            // Warm-up: touches every page of both buffers so the timed passes measure memory, not page faults.
            Fill(source, threads, 0x5A);
            Copy(source, target, threads);

            var copy = new List<double>();
            var write = new List<double>();
            int repeats = 0;
            var watch = Stopwatch.StartNew();
            while (BenchmarkTiming.ShouldContinue(repeats, watch.Elapsed, budget))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long start = Stopwatch.GetTimestamp();
                Copy(source, target, threads);
                copy.Add(GigabytesPerSecond(length, Stopwatch.GetElapsedTime(start)));

                cancellationToken.ThrowIfCancellationRequested();
                start = Stopwatch.GetTimestamp();
                Fill(target, threads, (byte)repeats);
                write.Add(GigabytesPerSecond(length, Stopwatch.GetElapsedTime(start)));

                repeats++;
                if (repeats % 4 == 0)
                {
                    progress?.Report(new ProgressUpdate(stage, BenchmarkTiming.Percent(watch.Elapsed, budget), $"{copy[^1]:0.0} GB/s"));
                }
            }

            return new MemoryBandwidthMeasurement(copy, write, length, threads, repeats, watch.Elapsed);
        }
        finally
        {
            // Hand the large buffers back right away rather than holding hundreds of megabytes until the next collection.
            source = null!;
            target = null!;
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
        }
    }

    public static MemoryLatencyMeasurement RunLatency(long bufferBytes, TimeSpan budget, IProgress<ProgressUpdate>? progress, string stage, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate(stage, 0, "Preparing the buffer…"));
        int slots = (int)Math.Clamp(bufferBytes / MemoryBenchmarkPlan.CacheLineBytes, 2, Array.MaxLength / IntsPerLine);
        var chain = BuildChain(slots, 2024);
        try
        {
            // Warm-up: one full lap pages the buffer in.
            Chase(chain, slots);

            var samples = new List<double>();
            int repeats = 0, index = 0;
            var watch = Stopwatch.StartNew();
            while (BenchmarkTiming.ShouldContinue(repeats, watch.Elapsed, budget))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long start = Stopwatch.GetTimestamp();
                index = Chase(chain, ChaseStepsPerRepeat, index);
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1_000_000 / ChaseStepsPerRepeat);
                repeats++;
                if (repeats % 8 == 0)
                {
                    progress?.Report(new ProgressUpdate(stage, BenchmarkTiming.Percent(watch.Elapsed, budget), $"{samples[^1]:0.0} ns"));
                }
            }

            return new MemoryLatencyMeasurement(samples, (long)slots * MemoryBenchmarkPlan.CacheLineBytes, repeats, watch.Elapsed);
        }
        finally
        {
            chain = null!;
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
        }
    }

    /// <summary>Ints in one cache line; each line holds one link of the chain in its first int.</summary>
    public const int IntsPerLine = MemoryBenchmarkPlan.CacheLineBytes / sizeof(int);

    /// <summary>
    /// A chain through <paramref name="slots"/> cache lines in one random cycle (Sattolo's shuffle), stored as the
    /// array index of the next link in the first int of each line. Starting anywhere, following the links visits every
    /// line exactly once before returning. The same seed always builds the same chain.
    /// </summary>
    public static int[] BuildChain(int slots, int seed)
    {
        if (slots < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(slots), "A chain needs at least two slots.");
        }

        var order = new int[slots];
        for (int i = 0; i < slots; i++)
        {
            order[i] = i;
        }

        var random = new Random(seed);
        for (int i = slots - 1; i > 0; i--)
        {
            int j = random.Next(i); // Sattolo: j < i, which yields a single cycle.
            (order[i], order[j]) = (order[j], order[i]);
        }

        if ((long)slots * IntsPerLine > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(slots), "The chain would be larger than an array can hold.");
        }

        var chain = new int[slots * IntsPerLine];
        for (int i = 0; i < slots; i++)
        {
            chain[i * IntsPerLine] = order[i] * IntsPerLine;
        }

        return chain;
    }

    /// <summary>Follows <paramref name="steps"/> links from <paramref name="start"/> and returns where it ended.</summary>
    public static int Chase(int[] chain, int steps, int start = 0)
    {
        ArgumentNullException.ThrowIfNull(chain);
        int index = start;
        for (int i = 0; i < steps; i++)
        {
            index = chain[index];
        }

        return index;
    }

    public static double GigabytesPerSecond(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : bytes / elapsed.TotalSeconds / 1e9;

    private static void Copy(byte[] source, byte[] target, int threads) =>
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            var (start, length) = MemoryBenchmarkPlan.Chunk(source.Length, threads, t);
            source.AsSpan((int)start, (int)length).CopyTo(target.AsSpan((int)start, (int)length));
        });

    private static void Fill(byte[] target, int threads, byte value) =>
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            var (start, length) = MemoryBenchmarkPlan.Chunk(target.Length, threads, t);
            target.AsSpan((int)start, (int)length).Fill(value);
        });
}
