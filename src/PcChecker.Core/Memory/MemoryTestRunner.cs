using System.Diagnostics;
using PcChecker.Core.Common;

namespace PcChecker.Core.Memory;

/// <summary>
/// The user-mode memory test engine: allocates pinned chunks of currently free memory, fills and verifies every
/// <see cref="MemoryTestPatterns"/> pattern in parallel across cores and reports mismatches with their offsets.
/// Pure .NET, so the same engine runs in demo mode and in unit tests (with a tiny size).
/// </summary>
public static class MemoryTestRunner
{
    /// <summary>Largest single allocation; keeps the garbage collector happy and makes progress granular.</summary>
    public const int MaxChunkBytes = 512 * 1024 * 1024;

    /// <summary>Smallest chunk worth allocating when memory is tight.</summary>
    public const int MinChunkBytes = 16 * 1024 * 1024;

    /// <summary>Free memory the test always leaves alone so Windows and the app keep running.</summary>
    public const long HeadroomBytes = 1024L * 1024 * 1024;

    /// <summary>Upper bound on the errors kept with full details (the count keeps growing).</summary>
    public const int MaxDetailedErrors = 50;

    /// <summary>
    /// How many bytes to test for a given percentage of the currently available memory: clamps the percentage to
    /// 1–90, keeps <see cref="HeadroomBytes"/> free (or 10 % on small systems) and rounds down to whole megabytes.
    /// </summary>
    public static long PlanBytes(long availableBytes, int percentOfAvailable)
    {
        if (availableBytes <= 0)
        {
            return 0;
        }

        int percent = Math.Clamp(percentOfAvailable, 1, 90);
        long headroom = Math.Min(HeadroomBytes, availableBytes / 10);
        long ceiling = Math.Max(0, availableBytes - headroom);
        long wanted = (long)(availableBytes * (percent / 100.0));
        long bytes = Math.Min(wanted, ceiling);
        return bytes / (1024 * 1024) * (1024 * 1024);
    }

    /// <summary>Splits a byte count into chunk sizes of at most <see cref="MaxChunkBytes"/> (whole 64-bit words).</summary>
    public static IReadOnlyList<int> PlanChunks(long bytes)
    {
        var chunks = new List<int>();
        long remaining = Math.Max(0, bytes) / 8 * 8;
        while (remaining > 0)
        {
            int size = (int)Math.Min(remaining, MaxChunkBytes);
            if (size < 8)
            {
                break;
            }

            chunks.Add(size);
            remaining -= size;
        }

        return chunks;
    }

    /// <summary>
    /// Runs the test. Returns a partial result with <c>Completed == false</c> when cancelled instead of throwing.
    /// <paramref name="faultInjector"/> is only used by self-tests to corrupt a block after it was filled.
    /// </summary>
    public static async Task<MemoryTestResult> RunAsync(long bytesToTest, int passes, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken, Action<ulong[], int, int>? faultInjector = null)
    {
        passes = Math.Max(1, passes);
        var stopwatch = Stopwatch.StartNew();
        var plan = PlanChunks(bytesToTest);
        if (plan.Count == 0)
        {
            return new MemoryTestResult(0, passes, 0, [], stopwatch.Elapsed, false,
                "There is not enough free memory to run a meaningful test right now. Close some programs and try again.");
        }

        progress?.Report(new ProgressUpdate("Reserving memory", 0, $"Allocating {ByteSize.Format(bytesToTest)} in {plan.Count} chunk(s)"));
        var chunks = new List<ulong[]>(plan.Count);
        long allocated = 0;
        string? allocationNote = null;
        bool cancelled = false;
        try
        {
            foreach (int size in plan)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var chunk = TryAllocate(size);
                if (chunk is null)
                {
                    allocationNote = $"Only {ByteSize.Format(allocated)} could be reserved; the rest was in use.";
                    break;
                }

                chunks.Add(chunk);
                allocated += (long)chunk.Length * 8;
            }

            if (cancelled)
            {
                return new MemoryTestResult(0, passes, 0, [], stopwatch.Elapsed, false, BuildSummary(0, passes, 0, true, stopwatch.Elapsed));
            }

            if (chunks.Count == 0)
            {
                return new MemoryTestResult(0, passes, 0, [], stopwatch.Elapsed, false,
                    "Windows refused to hand out memory for the test. Close some programs and try a smaller percentage.");
            }

            var errors = new List<MemoryTestError>();
            long errorCount = 0;
            var errorGate = new object();
            int patternCount = MemoryTestPatterns.Names.Count;
            long totalSteps = (long)passes * patternCount * chunks.Count;
            long doneSteps = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount), CancellationToken = cancellationToken };

            for (int pass = 1; pass <= passes && !cancelled; pass++)
            {
                for (int pattern = 0; pattern < patternCount && !cancelled; pattern++)
                {
                    int currentPass = pass, currentPattern = pattern;
                    string patternName = MemoryTestPatterns.Names[pattern];
                    progress?.Report(new ProgressUpdate($"Pass {pass} of {passes}: {patternName}", 100.0 * doneSteps / totalSteps));
                    try
                    {
                        await Task.Run(() => Parallel.For(0, chunks.Count, options, chunkIndex =>
                        {
                            var block = chunks[chunkIndex];
                            int seed = unchecked(currentPass * 7919 + chunkIndex * 104729 + 17);
                            MemoryTestPatterns.Fill(block, currentPattern, seed);
                            faultInjector?.Invoke(block, chunkIndex, currentPattern);
                            var (mismatches, offsets) = MemoryTestPatterns.Verify(block, currentPattern, seed, maxOffsets: 8);
                            if (mismatches > 0)
                            {
                                lock (errorGate)
                                {
                                    errorCount += mismatches;
                                    foreach (int offset in offsets)
                                    {
                                        if (errors.Count >= MaxDetailedErrors)
                                        {
                                            break;
                                        }

                                        errors.Add(new MemoryTestError(chunkIndex, (long)offset * 8, ExpectedWord(currentPattern, seed, offset), block[offset], patternName));
                                    }
                                }
                            }

                            long done = Interlocked.Increment(ref doneSteps);
                            progress?.Report(new ProgressUpdate($"Pass {currentPass} of {passes}: {patternName}", 100.0 * done / totalSteps,
                                $"Chunk {chunkIndex + 1} of {chunks.Count}"));
                        }), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                }
            }

            stopwatch.Stop();
            var details = errors.Select(e => e.ToString()).ToList();
            string summary = BuildSummary(allocated, passes, errorCount, cancelled, stopwatch.Elapsed, allocationNote);
            progress?.Report(new ProgressUpdate(cancelled ? "Stopped" : "Done", 100));
            return new MemoryTestResult(allocated, passes, errorCount, details, stopwatch.Elapsed, !cancelled, summary);
        }
        finally
        {
            chunks.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>Human summary of a run; also used by the demo service.</summary>
    public static string BuildSummary(long bytesTested, int passes, long errors, bool cancelled, TimeSpan elapsed, string? note = null)
    {
        string size = ByteSize.Format(bytesTested);
        string passText = passes == 1 ? "1 pass" : $"{passes} passes";
        string time = elapsed.TotalSeconds < 1 ? "under a second" : elapsed.TotalMinutes >= 1 ? $"{elapsed.TotalMinutes:0.#} min" : $"{elapsed.TotalSeconds:0} s";
        string prefix = cancelled ? $"Stopped early after testing {size} ({time})" : $"Tested {size} of free memory in {passText} ({time})";
        string outcome = errors == 0
            ? ": no errors found."
            : $": {errors} error{(errors == 1 ? string.Empty : "s")} found. Faulty RAM, an unstable XMP/EXPO profile or an overclock is the usual cause. Turn off XMP/EXPO and test again; if errors remain, test one module at a time to find the bad one.";
        string disclaimer = " This is a quick check of the memory that was free right now, not a replacement for a boot-time test: schedule the Windows Memory Diagnostic for a full check.";
        return prefix + outcome + (note is null ? string.Empty : " " + note) + disclaimer;
    }

    private static ulong[]? TryAllocate(int bytes)
    {
        int size = Math.Max(8, bytes);
        while (true)
        {
            try
            {
                return GC.AllocateUninitializedArray<ulong>(size / 8, pinned: true);
            }
            catch (OutOfMemoryException)
            {
                if (size <= MinChunkBytes)
                {
                    return null;
                }

                size /= 2;
            }
        }
    }

    private static ulong ExpectedWord(int pattern, int seed, int index)
    {
        switch (pattern)
        {
            case 0:
                return MemoryTestPatterns.Pattern5;
            case 1:
                return MemoryTestPatterns.PatternA;
            case 2:
                return 1UL << (index % 64);
            default:
                ulong state = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 0x1234567UL);
                for (int i = 0; i <= index; i++)
                {
                    state = MemoryTestPatterns.Next(state);
                }

                return state;
        }
    }
}
