using System.Diagnostics;
using PcChecker.Core.Common;
using PcChecker.Core.Localization;

namespace PcChecker.Core.RamTuning;

/// <summary>Why a quick check ended.</summary>
public enum QuickCheckStop
{
    Finished,
    Cancelled,

    /// <summary>Windows ran low on free memory, so the check let go of its memory early.</summary>
    LowMemory,

    /// <summary>Not enough memory could be reserved to start.</summary>
    NotEnoughMemory,
}

/// <summary>Live progress of a running check.</summary>
public sealed record QuickCheckProgress(double Percent, TimeSpan Elapsed, TimeSpan Duration, long Errors, int Rounds, long BytesVerified, string Stage);

/// <summary>The result of a quick check.</summary>
public sealed record QuickCheckResult(long BytesTested, TimeSpan Elapsed, int Rounds, long Errors, QuickCheckStop Stop, string Summary);

/// <summary>
/// The built-in "Quick memory check": a short screen of a chosen share of the free memory with walking ones and zeros,
/// random patterns and moving inversions on every core, at below-normal priority. It is not a replacement for TestMem5,
/// Karhu or MemTest86 and changes nothing on the PC. The sizing and the stop rules are pure and tested.
/// </summary>
public static class QuickMemoryCheck
{
    public const int DefaultPercent = 70;
    public const int MinPercent = 10;
    public const int MaxPercent = 90;

    /// <summary>Free memory always left to Windows and other programs.</summary>
    public const long ReserveBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The check stops when Windows' free memory drops below this while it runs.</summary>
    public const long LowMemoryStopBytes = 512L * 1024 * 1024;

    /// <summary>Largest single block (keeps every allocation well under the 2 GB array limit and progress granular).</summary>
    public const int MaxBlockBytes = 128 * 1024 * 1024;

    /// <summary>Smallest block worth testing.</summary>
    public const int MinBlockBytes = 1024 * 1024;

    /// <summary>The durations offered on the page.</summary>
    public static IReadOnlyList<TimeSpan> Durations { get; } =
        [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60)];

    /// <summary>
    /// Bytes to test: <paramref name="percent"/> (clamped 10–90) of the free memory, but always leaving
    /// <see cref="ReserveBytes"/> free, rounded down to whole megabytes. Zero when there is not enough.
    /// </summary>
    public static long PlanBytes(long freeBytes, int percent)
    {
        if (freeBytes <= ReserveBytes)
        {
            return 0;
        }

        int p = Math.Clamp(percent, MinPercent, MaxPercent);
        long share = (long)(freeBytes * (p / 100.0));
        long cap = freeBytes - ReserveBytes;
        long bytes = Math.Min(share, cap);
        bytes = bytes / (1024 * 1024) * (1024 * 1024);
        return bytes < MinBlockBytes ? 0 : bytes;
    }

    /// <summary>Block sizes (whole 64-bit words): at least one block per worker so every core has its own memory.</summary>
    public static IReadOnlyList<int> PlanBlocks(long bytes, int workers)
    {
        var blocks = new List<int>();
        if (bytes < MinBlockBytes)
        {
            return blocks;
        }

        workers = Math.Max(1, workers);
        long perBlock = Math.Clamp(bytes / workers, MinBlockBytes, MaxBlockBytes);
        perBlock = perBlock / 8 * 8;
        long remaining = bytes / 8 * 8;
        while (remaining >= MinBlockBytes || (blocks.Count == 0 && remaining >= 8))
        {
            int size = (int)Math.Min(perBlock, remaining);
            blocks.Add(size);
            remaining -= size;
        }

        return blocks;
    }

    /// <summary>True when the check must stop because Windows is low on memory (unknown free memory never stops it).</summary>
    public static bool ShouldStopForMemory(long? freeBytes) => freeBytes is { } f && f < LowMemoryStopBytes;

    /// <summary>
    /// Runs the check for <paramref name="duration"/> on <paramref name="bytes"/> of memory. <paramref name="freeMemory"/>
    /// is polled every second; <paramref name="faultInjector"/> is only for self-tests. Never throws on cancellation.
    /// </summary>
    public static async Task<QuickCheckResult> RunAsync(long bytes, TimeSpan duration, Func<long?> freeMemory, IProgress<QuickCheckProgress>? progress,
        CancellationToken cancellationToken, QuickMemoryPatterns.SpanAction? faultInjector = null, Translator? tr = null)
    {
        ArgumentNullException.ThrowIfNull(freeMemory);
        tr ??= Translator.English;
        var stopwatch = Stopwatch.StartNew();
        int workers = Math.Max(1, Environment.ProcessorCount);
        var plan = PlanBlocks(bytes, workers);
        if (plan.Count == 0)
        {
            return new QuickCheckResult(0, stopwatch.Elapsed, 0, 0, QuickCheckStop.NotEnoughMemory, Summary(0, TimeSpan.Zero, 0, 0, QuickCheckStop.NotEnoughMemory, tr));
        }

        progress?.Report(new QuickCheckProgress(0, TimeSpan.Zero, duration, 0, 0, 0, tr.F("Reserving {0}", ByteSize.Format(bytes))));
        var blocks = new List<ulong[]>(plan.Count);
        long reserved = 0;
        try
        {
            foreach (int size in plan)
            {
                if (cancellationToken.IsCancellationRequested || ShouldStopForMemory(freeMemory()))
                {
                    break;
                }

                ulong[] block;
                try
                {
                    block = GC.AllocateUninitializedArray<ulong>(size / 8, pinned: true);
                }
                catch (OutOfMemoryException)
                {
                    break;
                }

                blocks.Add(block);
                reserved += (long)block.Length * 8;
            }

            if (blocks.Count == 0)
            {
                var stop = cancellationToken.IsCancellationRequested ? QuickCheckStop.Cancelled : QuickCheckStop.NotEnoughMemory;
                return new QuickCheckResult(0, stopwatch.Elapsed, 0, 0, stop, Summary(0, stopwatch.Elapsed, 0, 0, stop, tr));
            }

            return await RunWorkersAsync(blocks, reserved, duration, freeMemory, progress, cancellationToken, faultInjector, stopwatch, tr).ConfigureAwait(false);
        }
        finally
        {
            blocks.Clear();
            GC.Collect();
        }
    }

    private static async Task<QuickCheckResult> RunWorkersAsync(List<ulong[]> blocks, long reserved, TimeSpan duration, Func<long?> freeMemory,
        IProgress<QuickCheckProgress>? progress, CancellationToken cancellationToken, QuickMemoryPatterns.SpanAction? faultInjector, Stopwatch stopwatch, Translator tr)
    {
        int workerCount = Math.Min(Math.Max(1, Environment.ProcessorCount), blocks.Count);
        long errors = 0;
        long verifiedBytes = 0;
        int stopFlag = 0;
        var rounds = new int[workerCount];
        var threads = new List<Thread>(workerCount);
        for (int w = 0; w < workerCount; w++)
        {
            int worker = w;
            var thread = new Thread(() =>
            {
                ulong seed = (ulong)(worker + 1) * 0x9E3779B97F4A7C15UL;
                while (Volatile.Read(ref stopFlag) == 0)
                {
                    foreach (var pattern in QuickMemoryPatterns.All)
                    {
                        for (int b = worker; b < blocks.Count; b += workerCount)
                        {
                            if (Volatile.Read(ref stopFlag) != 0)
                            {
                                return;
                            }

                            seed = unchecked(seed + 0xD1B54A32D192ED03UL);
                            long found = QuickMemoryPatterns.Run(blocks[b], pattern, seed, faultInjector);
                            if (found > 0)
                            {
                                Interlocked.Add(ref errors, found);
                            }

                            Interlocked.Add(ref verifiedBytes, (long)blocks[b].Length * 8);
                        }
                    }

                    rounds[worker]++;
                }
            })
            {
                IsBackground = true,
                Name = $"PcChecker quick memory check {worker + 1}",
            };

            try
            {
                // Below normal so the PC stays usable while the check runs.
                thread.Priority = ThreadPriority.BelowNormal;
            }
            catch (Exception ex) when (ex is ThreadStateException or PlatformNotSupportedException)
            {
            }

            threads.Add(thread);
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        var stop = QuickCheckStop.Finished;
        var lastMemoryCheck = TimeSpan.Zero;
        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                stop = QuickCheckStop.Cancelled;
                break;
            }

            if (stopwatch.Elapsed >= duration)
            {
                break;
            }

            // The watchdog asks Windows for its free memory once a second.
            if (stopwatch.Elapsed - lastMemoryCheck >= TimeSpan.FromSeconds(1))
            {
                lastMemoryCheck = stopwatch.Elapsed;
                if (ShouldStopForMemory(freeMemory()))
                {
                    stop = QuickCheckStop.LowMemory;
                    break;
                }
            }

            double percent = Math.Min(100, 100.0 * stopwatch.Elapsed.Ticks / Math.Max(1, duration.Ticks));
            progress?.Report(new QuickCheckProgress(percent, stopwatch.Elapsed, duration, Interlocked.Read(ref errors), rounds.Min(),
                Interlocked.Read(ref verifiedBytes), tr.F("Testing {0} on {1} threads", ByteSize.Format(reserved), workerCount)));
        }

        Interlocked.Exchange(ref stopFlag, 1);
        await Task.Run(() =>
        {
            foreach (var thread in threads)
            {
                thread.Join();
            }
        }, CancellationToken.None).ConfigureAwait(false);

        stopwatch.Stop();
        long total = Interlocked.Read(ref errors);
        int done = rounds.Min();
        progress?.Report(new QuickCheckProgress(100, stopwatch.Elapsed, duration, total, done, Interlocked.Read(ref verifiedBytes), tr.T("Done")));
        return new QuickCheckResult(reserved, stopwatch.Elapsed, done, total, stop, Summary(reserved, stopwatch.Elapsed, done, total, stop, tr));
    }

    /// <summary>The plain summary of a check.</summary>
    public static string Summary(long bytes, TimeSpan elapsed, int rounds, long errors, QuickCheckStop stop, Translator? tr = null)
    {
        tr ??= Translator.English;
        if (stop == QuickCheckStop.NotEnoughMemory)
        {
            return tr.T("Not enough free memory to run the check. Close some programs and try again.");
        }

        string time = elapsed.TotalMinutes >= 1 ? tr.F("{0:0.#} min", elapsed.TotalMinutes) : tr.F("{0:0} s", elapsed.TotalSeconds);
        string head = stop switch
        {
            QuickCheckStop.Cancelled => tr.F("Stopped after {0}: {1} tested, {2} full round(s).", time, ByteSize.Format(bytes), rounds),
            QuickCheckStop.LowMemory => tr.F("Stopped after {0} because Windows was running low on memory: {1} tested, {2} full round(s).", time, ByteSize.Format(bytes), rounds),
            _ => tr.F("{0} tested for {1}, {2} full round(s).", ByteSize.Format(bytes), time, rounds),
        };
        string outcome = errors == 0
            ? tr.T("No errors found.")
            : tr.F("{0} error(s) found: the memory setting is not stable. Undo the last BIOS change or go one speed step lower, then test again.", errors);
        return head + " " + outcome + " " + tr.T("This is a quick screen, not a replacement for the stability tools above.");
    }
}
