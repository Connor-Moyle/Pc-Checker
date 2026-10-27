using System.Diagnostics;
using PcChecker.Core.Common;
using PcChecker.Core.Overclocking.GpuLoad;

namespace PcChecker.Core.Benchmark;

/// <summary>
/// Runs the benchmark tests. Nothing here changes the system: the processor and memory tests only compute, the disk
/// test writes one temporary file that is deleted afterwards, and the graphics test runs a compute shader.
/// </summary>
public interface IBenchmarkService
{
    /// <summary>The drive the disk test uses ("C:"), or null when it cannot be found.</summary>
    string? SystemDrive { get; }

    /// <summary>Size of the disk test's temporary file in megabytes.</summary>
    int DiskTestFileMb { get; }

    /// <summary>Processor, memory and graphics card names for the run's header.</summary>
    Task<BenchmarkSystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken);

    /// <summary>Power source, processor load (over about a second) and temperatures right now, with warnings.</summary>
    Task<BenchmarkConditions> CheckConditionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs one test. A test that cannot run comes back <see cref="BenchmarkTestStatus.Skipped"/> or
    /// <see cref="BenchmarkTestStatus.Failed"/> with the reason; cancellation throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<BenchmarkTestResult> RunTestAsync(BenchmarkTestKind test, BenchmarkOptions options, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}

/// <summary>Result of <see cref="BenchmarkSuite.RunAsync"/>: the run (null when no test finished) and whether it was stopped.</summary>
public sealed record BenchmarkSuiteOutcome(BenchmarkRun? Run, bool Cancelled);

/// <summary>
/// Runs a list of tests one after another and assembles the <see cref="BenchmarkRun"/>. Progress covers the whole run
/// ("Memory latency (4 of 6)"). When the user stops part-way the tests that finished are kept and the run is marked
/// as stopped early.
/// </summary>
public static class BenchmarkSuite
{
    public static async Task<BenchmarkSuiteOutcome> RunAsync(IBenchmarkService service, IReadOnlyList<BenchmarkTestKind> tests, BenchmarkOptions options,
        string? note, IReadOnlyList<string> warnings, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(tests);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(warnings);
        var started = DateTimeOffset.Now;
        var watch = Stopwatch.StartNew();
        var ordered = tests.Distinct().OrderBy(t => t).ToList();
        if (ordered.Count == 0)
        {
            return new BenchmarkSuiteOutcome(null, false);
        }

        BenchmarkSystemInfo system;
        try
        {
            system = await service.GetSystemInfoAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new BenchmarkSuiteOutcome(null, true);
        }

        var results = new List<BenchmarkTestResult>();
        bool cancelled = false;
        for (int i = 0; i < ordered.Count; i++)
        {
            var test = ordered[i];
            string stage = $"{BenchmarkCatalog.Name(test)} ({i + 1} of {ordered.Count})";
            int index = i;
            var mapped = progress is null ? null : new InlineProgress<ProgressUpdate>(p =>
                progress.Report(new ProgressUpdate(stage, OverallPercent(index, ordered.Count, p.Percent), p.Detail)));
            progress?.Report(new ProgressUpdate(stage, OverallPercent(index, ordered.Count, 0), "Starting…"));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await service.RunTestAsync(test, options, mapped, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                results.Add(BenchmarkTestResult.Fail(test, "The test stopped with an error: " + ex.Message));
            }
        }

        if (results.Count == 0)
        {
            return new BenchmarkSuiteOutcome(null, cancelled);
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        var run = new BenchmarkRun(Guid.NewGuid(), started, Math.Round(watch.Elapsed.TotalSeconds, 1), BenchmarkHistoryRules.CleanNote(note), system, results, warnings)
        {
            StoppedEarly = cancelled,
        };
        return new BenchmarkSuiteOutcome(run, cancelled);
    }

    /// <summary>Whole-run percentage for test <paramref name="index"/> of <paramref name="count"/> at <paramref name="testPercent"/>.</summary>
    public static double OverallPercent(int index, int count, double? testPercent)
    {
        if (count <= 0)
        {
            return 0;
        }

        double within = Math.Clamp(testPercent ?? 0, 0, 100) / 100;
        return Math.Clamp((index + within) / count * 100, 0, 100);
    }
}
