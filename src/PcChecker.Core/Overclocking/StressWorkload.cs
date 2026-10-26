using System.Security.Cryptography;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// Deterministic CPU workload used by the stress test: SHA-256 over a seeded buffer plus a floating-point loop.
/// Every worker computes the same reference, so any mismatch means the CPU produced a wrong result (instability).
/// Pure: no Windows dependency.
/// </summary>
public static class StressWorkload
{
    public const int BufferBytes = 64 * 1024;
    public const int FloatIterations = 20_000;

    /// <summary>Computes the workload once and returns a checksum of both halves.</summary>
    public static ulong Compute(int seed)
    {
        var buffer = new byte[BufferBytes];
        var rng = new Random(seed);
        rng.NextBytes(buffer);

        var hash = SHA256.HashData(buffer);
        ulong integerPart = BitConverter.ToUInt64(hash, 0) ^ BitConverter.ToUInt64(hash, 8) ^ BitConverter.ToUInt64(hash, 16) ^ BitConverter.ToUInt64(hash, 24);

        // Floating point: a chaotic-but-deterministic recurrence stressing the FPU/AVX units.
        double x = 0.5 + seed % 97 / 1000.0;
        double acc = 0;
        for (int i = 1; i <= FloatIterations; i++)
        {
            x = 3.7 * x * (1 - x);
            acc += Math.Sqrt(x * i) * Math.Sin(x) / (1 + Math.Cos(i * 0.001));
        }

        ulong floatPart = (ulong)BitConverter.DoubleToInt64Bits(Math.Round(acc, 6));
        return integerPart ^ (floatPart * 0x9E3779B97F4A7C15UL);
    }

    /// <summary>Returns true when a worker's result matches the reference for the same seed.</summary>
    public static bool Verify(int seed, ulong result) => Compute(seed) == result;

    /// <summary>Builds the per-iteration seed so workers and the reference agree.</summary>
    public static int SeedFor(int worker, long iteration) => unchecked((int)(worker * 7919 + iteration * 104729 + 13));
}

/// <summary>Outcome of a CPU burn: how many iterations ran and how many produced a wrong result.</summary>
public sealed record StressRunStats(long Iterations, int Errors, int Workers);

/// <summary>Runs <see cref="StressWorkload"/> on every core until cancelled, verifying each result against a reference.</summary>
public sealed class CpuStressRunner
{
    private readonly int _workers;

    public CpuStressRunner(int? workers = null)
    {
        _workers = Math.Max(1, workers ?? Environment.ProcessorCount);
    }

    public long Iterations => Interlocked.Read(ref _iterations);
    public int Errors => Volatile.Read(ref _errors);

    private long _iterations;
    private int _errors;

    /// <summary>Burns all cores until the token is cancelled. Never throws for cancellation.</summary>
    public async Task<StressRunStats> RunAsync(CancellationToken cancellationToken)
    {
        // A small table of references so the verify step is cheap and does not double the work.
        const int referenceCount = 8;
        var references = new ulong[referenceCount];
        for (int i = 0; i < referenceCount; i++)
        {
            references[i] = StressWorkload.Compute(StressWorkload.SeedFor(0, i));
        }

        var tasks = new Task[_workers];
        for (int w = 0; w < _workers; w++)
        {
            tasks[w] = Task.Factory.StartNew(() => Worker(references, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return new StressRunStats(Iterations, Errors, _workers);
    }

    private void Worker(ulong[] references, CancellationToken cancellationToken)
    {
        long local = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            int index = (int)(local % references.Length);
            ulong result = StressWorkload.Compute(StressWorkload.SeedFor(0, index));
            if (result != references[index])
            {
                Interlocked.Increment(ref _errors);
            }

            local++;
            Interlocked.Increment(ref _iterations);
        }
    }
}
