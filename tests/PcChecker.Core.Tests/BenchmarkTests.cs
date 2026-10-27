using System.Globalization;
using System.Security.Cryptography;
using PcChecker.Core.Benchmark;
using PcChecker.Core.Common;
using PcChecker.Core.Storage;
using Xunit;

namespace PcChecker.Core.Tests;

public class BenchmarkTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // ---- Scoring ----------------------------------------------------------------------------------------------

    [Fact]
    public void Score_is_1000_at_the_reference_and_scales_linearly()
    {
        Assert.Equal(1000, BenchmarkScoring.Score(120, 120));
        Assert.Equal(2000, BenchmarkScoring.Score(240, 120));
        Assert.Equal(500, BenchmarkScoring.Score(60, 120));
        Assert.Equal(1500, BenchmarkScoring.Score(1.5, 1));
    }

    [Fact]
    public void Score_inverts_values_where_lower_is_better_and_rejects_bad_input()
    {
        Assert.Equal(2000, BenchmarkScoring.Score(50, 100, lowerIsBetter: true));
        Assert.Equal(800, BenchmarkScoring.Score(125, 100, lowerIsBetter: true));
        Assert.Equal(0, BenchmarkScoring.Score(0, 100));
        Assert.Equal(0, BenchmarkScoring.Score(-5, 100));
        Assert.Equal(0, BenchmarkScoring.Score(double.NaN, 100));
        Assert.Equal(0, BenchmarkScoring.Score(100, 0));
        Assert.Equal(BenchmarkScoring.MinScore, BenchmarkScoring.Score(1e-9, 100));
    }

    [Fact]
    public void Scores_combine_by_geometric_mean()
    {
        Assert.Equal(1000, BenchmarkScoring.Combine([500, 2000]));
        Assert.Equal(1000, BenchmarkScoring.Combine([1000, 1000, 1000]));
        Assert.Equal(1414, BenchmarkScoring.Combine([1000, 2000]));
        Assert.Null(BenchmarkScoring.Combine([]));
        Assert.Equal(1000, BenchmarkScoring.Combine([1000, 0]));
    }

    [Fact]
    public void Test_score_uses_only_baseline_values_and_overall_skips_tests_without_a_score()
    {
        var metrics = BenchmarkBaseline.CpuSingleCore.Select(b => new BenchmarkMetric(b.Key, b.Name, b.Reference * 2, b.Unit)).ToList();
        metrics.Add(new BenchmarkMetric("cpu1.extra", "Not scored", 0.001, "x", Informational: true));
        Assert.Equal(2000, BenchmarkScoring.ScoreTest(BenchmarkTestKind.CpuSingleCore, metrics));
        Assert.Null(BenchmarkScoring.ScoreTest(BenchmarkTestKind.GpuCompute, metrics));

        var latency = new[] { new BenchmarkMetric("mem.latency", "Latency", 50, "ns", LowerIsBetter: true) };
        Assert.Equal(2000, BenchmarkScoring.ScoreTest(BenchmarkTestKind.MemoryLatency, latency));

        BenchmarkTestResult[] tests =
        [
            Completed(BenchmarkTestKind.CpuSingleCore, 500),
            Completed(BenchmarkTestKind.CpuMultiCore, 2000),
            BenchmarkTestResult.Skip(BenchmarkTestKind.GpuCompute, "No DirectX 11 graphics card was found."),
            BenchmarkTestResult.Fail(BenchmarkTestKind.Disk, "Drive not ready."),
        ];
        Assert.Equal(1000, BenchmarkScoring.Overall(tests));
        Assert.Null(BenchmarkScoring.Overall([BenchmarkTestResult.Skip(BenchmarkTestKind.GpuCompute, "none")]));
    }

    [Fact]
    public void Bar_puts_the_reference_in_the_middle_and_clamps()
    {
        Assert.Equal(0.5, BenchmarkScoring.BarFraction(1000));
        Assert.Equal(0.25, BenchmarkScoring.BarFraction(500));
        Assert.Equal(1, BenchmarkScoring.BarFraction(5000));
        Assert.Equal(0, BenchmarkScoring.BarFraction(null));
        Assert.Equal("About the same as the reference PC", BenchmarkScoring.Rating(1000));
        Assert.Equal("Well above the reference PC", BenchmarkScoring.Rating(1600));
        Assert.Equal("Well below the reference PC", BenchmarkScoring.Rating(300));
    }

    [Fact]
    public void Every_test_has_a_baseline_and_kernel_baselines_follow_the_enum_order()
    {
        foreach (var test in BenchmarkCatalog.All)
        {
            Assert.NotEmpty(BenchmarkBaseline.For(test));
            Assert.All(BenchmarkBaseline.For(test), b => Assert.True(b.Reference > 0));
            Assert.False(string.IsNullOrWhiteSpace(BenchmarkCatalog.Name(test)));
        }

        Assert.Equal("cpu1.matrix", BenchmarkBaseline.ForKernel(CpuKernel.MatrixMultiply, multiCore: false).Key);
        Assert.Equal("cpuN.sha256", BenchmarkBaseline.ForKernel(CpuKernel.Sha256, multiCore: true).Key);
        Assert.Equal("cpuN.compression", BenchmarkBaseline.ForKernel(CpuKernel.Compression, multiCore: true).Key);
    }

    // ---- Statistics -------------------------------------------------------------------------------------------

    [Fact]
    public void Median_handles_odd_even_and_non_finite_values()
    {
        Assert.Equal(3, BenchmarkStatistics.Median([5, 1, 3]));
        Assert.Equal(2.5, BenchmarkStatistics.Median([4, 1, 3, 2]));
        Assert.Equal(2, BenchmarkStatistics.Median([double.NaN, 2, double.PositiveInfinity]));
        Assert.True(double.IsNaN(BenchmarkStatistics.Median([])));
    }

    [Fact]
    public void Outlier_removal_drops_a_slow_repeat_but_keeps_small_samples()
    {
        double[] samples = [100, 101, 99, 102, 100, 98, 40, 101];
        var kept = BenchmarkStatistics.RemoveOutliers(samples);
        Assert.DoesNotContain(40, kept);
        Assert.Equal(7, kept.Count);
        Assert.Equal(100, BenchmarkStatistics.RobustMedian(samples));

        double[] few = [100, 40, 101];
        Assert.Equal(few, BenchmarkStatistics.RemoveOutliers(few));
        Assert.Equal(15, BenchmarkStatistics.Quantile([10, 20], 0.5));
    }

    [Fact]
    public void Spread_and_geometric_mean_are_correct()
    {
        Assert.Equal(10.0, BenchmarkStatistics.SpreadPercent([90, 100, 110])!.Value, 6);
        Assert.Null(BenchmarkStatistics.SpreadPercent([100]));
        Assert.Equal(4, BenchmarkStatistics.GeometricMean([2, 8]), 9);
        Assert.True(double.IsNaN(BenchmarkStatistics.GeometricMean([0, -1])));
    }

    [Fact]
    public void Timing_runs_minimum_repeats_then_stops_at_the_budget_or_the_hard_limit()
    {
        var budget = TimeSpan.FromSeconds(12);
        Assert.True(BenchmarkTiming.ShouldContinue(0, TimeSpan.Zero, budget));
        Assert.True(BenchmarkTiming.ShouldContinue(1, TimeSpan.FromSeconds(13), budget)); // slow PC: at least 3 repeats
        Assert.False(BenchmarkTiming.ShouldContinue(2, TimeSpan.FromSeconds(22), budget)); // but never far past the time
        Assert.True(BenchmarkTiming.ShouldContinue(10, TimeSpan.FromSeconds(11), budget));
        Assert.False(BenchmarkTiming.ShouldContinue(10, TimeSpan.FromSeconds(12), budget));
        Assert.False(BenchmarkTiming.ShouldContinue(BenchmarkTiming.MaxRepeats, TimeSpan.FromSeconds(1), budget));
        Assert.Equal(50, BenchmarkTiming.Percent(TimeSpan.FromSeconds(6), budget));
        Assert.Equal(99, BenchmarkTiming.Percent(TimeSpan.FromSeconds(30), budget));
        Assert.Equal(50, BenchmarkSuite.OverallPercent(2, 4, 0));
        Assert.Equal(62.5, BenchmarkSuite.OverallPercent(2, 4, 50));
    }

    // ---- Comparison -------------------------------------------------------------------------------------------

    [Fact]
    public void Change_is_relative_to_before_and_small_changes_count_as_the_same()
    {
        Assert.Equal(10, BenchmarkComparer.ChangePercent(1000, 1100)!.Value, 9);
        Assert.Equal(-25, BenchmarkComparer.ChangePercent(200, 150)!.Value, 9);
        Assert.Null(BenchmarkComparer.ChangePercent(0, 100));
        Assert.Null(BenchmarkComparer.ChangePercent(null, 100));

        Assert.Equal(BenchmarkChange.Same, BenchmarkComparer.Classify(2.9));
        Assert.Equal(BenchmarkChange.Better, BenchmarkComparer.Classify(3.0));
        Assert.Equal(BenchmarkChange.Worse, BenchmarkComparer.Classify(-8));
        Assert.Equal(BenchmarkChange.Better, BenchmarkComparer.Classify(-8, lowerIsBetter: true));
        Assert.Equal(BenchmarkChange.Worse, BenchmarkComparer.Classify(8, lowerIsBetter: true));
        Assert.Equal(BenchmarkChange.NotComparable, BenchmarkComparer.Classify(null));
    }

    [Fact]
    public void Comparison_matches_tests_and_raw_values_and_explains_what_it_cannot_compare()
    {
        var before = Run(DateTimeOffset.Parse("2026-10-01T10:00:00Z", Invariant),
            [Completed(BenchmarkTestKind.CpuSingleCore, 1000, 120), Completed(BenchmarkTestKind.MemoryLatency, 1000, latencyNs: 100)]);
        var after = Run(DateTimeOffset.Parse("2026-10-02T10:00:00Z", Invariant),
            [Completed(BenchmarkTestKind.CpuSingleCore, 1100, 132), Completed(BenchmarkTestKind.MemoryLatency, 1250, latencyNs: 80)]);

        var c = BenchmarkComparer.Compare(before, after);
        Assert.Equal(BenchmarkChange.Better, c.Overall.Change);
        Assert.Equal(2, c.Tests.Count);
        Assert.Equal(10, c.Tests[0].ChangePercent!.Value, 6);
        var latency = Assert.Single(c.Details, d => d.Unit == "ns");
        Assert.Equal(-20, latency.ChangePercent!.Value, 6);
        Assert.Equal(BenchmarkChange.Better, latency.Change);
        Assert.DoesNotContain(c.Details, d => d.Name == "Threads used");
        Assert.Empty(c.Notes);

        var partial = Run(after.Started.AddDays(1), [Completed(BenchmarkTestKind.CpuSingleCore, 1100, 132)]) with
        {
            Warnings = ["On battery."],
            ScoringVersion = BenchmarkBaseline.Version + 1,
        };
        var c2 = BenchmarkComparer.Compare(before, partial);
        Assert.Equal(BenchmarkChange.NotComparable, c2.Overall.Change);
        Assert.Equal(BenchmarkChange.NotComparable, c2.Tests.Single(t => t.Test == BenchmarkTestKind.MemoryLatency).Change);
        Assert.Equal(3, c2.Notes.Count);
    }

    // ---- Formatting -------------------------------------------------------------------------------------------

    [Fact]
    public void Numbers_changes_and_durations_are_formatted_plainly()
    {
        Assert.Equal("1,234", BenchmarkFormatter.Score(1234, Invariant));
        Assert.Equal("—", BenchmarkFormatter.Score(null, Invariant));
        Assert.Equal("12,346", BenchmarkFormatter.Number(12345.6, Invariant));
        Assert.Equal("45.6", BenchmarkFormatter.Number(45.62, Invariant));
        Assert.Equal("4.56", BenchmarkFormatter.Number(4.561, Invariant));
        Assert.Equal("+12.3 %", BenchmarkFormatter.Change(12.34, Invariant));
        Assert.Equal("-4.0 %", BenchmarkFormatter.Change(-4, Invariant));
        Assert.Equal("0.0 %", BenchmarkFormatter.Change(0.01, Invariant));
        Assert.Equal("—", BenchmarkFormatter.Change(null, Invariant));
        Assert.Equal("45 s", BenchmarkFormatter.Duration(44.6));
        Assert.Equal("2 min 05 s", BenchmarkFormatter.Duration(125));
        Assert.Equal("32 GB", BenchmarkFormatter.Memory(32L * 1024 * 1024 * 1024));
        Assert.Equal("1,234,567", BenchmarkFormatter.Score(1234567, CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("1.234", BenchmarkFormatter.Score(1234, CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void Exports_are_plain_text_with_scores_values_and_warnings()
    {
        var before = Run(DateTimeOffset.Parse("2026-10-01T10:00:00+00:00", Invariant), [Completed(BenchmarkTestKind.CpuSingleCore, 1000, 120)]);
        var after = Run(DateTimeOffset.Parse("2026-10-02T10:00:00+00:00", Invariant),
            [Completed(BenchmarkTestKind.CpuSingleCore, 1100, 132), BenchmarkTestResult.Skip(BenchmarkTestKind.GpuCompute, "No DirectX 11 graphics card was found.")]) with
        {
            Note = "After overclock",
            Warnings = ["The laptop is on battery."],
        };

        var text = BenchmarkFormatter.ExportRun(after);
        Assert.Contains("Date:     2026-10-02 10:00", text);
        Assert.Contains("Note:     After overclock", text);
        Assert.Contains("Overall:  1,100 (reference PC = 1,000)", text);
        Assert.Contains("Skipped: No DirectX 11 graphics card was found.", text);
        Assert.Contains("132 MB/s", text);
        Assert.Contains("- The laptop is on battery.", text);

        var comparison = BenchmarkFormatter.ExportComparison(BenchmarkComparer.Compare(before, after));
        Assert.Contains("After:    2026-10-02 10:00  After overclock", comparison);
        Assert.Matches(@"Overall score\s+1,000\s+1,100\s+\+10\.0 %  better", comparison);
        Assert.Matches(@"  Compression \(MB/s\)\s+120\s+132\s+\+10\.0 %  better", comparison);
    }

    // ---- Workloads --------------------------------------------------------------------------------------------

    [Fact]
    public void Prime_sieve_counts_known_values()
    {
        var scratch = new byte[CpuKernels.SieveSlots(1_000_000)];
        Assert.Equal(0, CpuKernels.CountPrimes(1, scratch));
        Assert.Equal(1, CpuKernels.CountPrimes(2, scratch));
        Assert.Equal(4, CpuKernels.CountPrimes(10, scratch));
        Assert.Equal(25, CpuKernels.CountPrimes(100, scratch));
        Assert.Equal(78_498, CpuKernels.CountPrimes(1_000_000, scratch));
        Assert.Equal(25, CpuKernels.CountPrimes(100, scratch)); // reuse of a dirty buffer
    }

    [Fact]
    public void Matrix_multiply_matches_a_naive_multiply_exactly()
    {
        foreach (int n in new[] { 1, 3, 9, 24 })
        {
            var a = CpuKernels.CreateMatrix(n, 7, 13);
            var b = CpuKernels.CreateMatrix(n, 11, 5);
            var product = new double[n * n];
            ulong checksum = CpuKernels.MatrixMultiply(a, b, product, n);

            double total = 0;
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < n; k++)
                    {
                        sum += a[i * n + k] * b[k * n + j];
                    }

                    Assert.Equal(sum, product[i * n + j]);
                    total += sum;
                }
            }

            Assert.Equal((ulong)BitConverter.DoubleToInt64Bits(total), checksum);
        }
    }

    [Fact]
    public void Workloads_are_deterministic_and_compression_round_trips()
    {
        var size = CpuWorkloadSize.Tiny;
        var first = CpuWorkloadData.Create(size, 7);
        var second = CpuWorkloadData.Create(size, 7);
        Assert.Equal(first.Text, second.Text);
        Assert.NotEqual(first.Text, CpuWorkloadData.Create(size, 8).Text);
        Assert.Equal(size.CompressionBytes, first.Text.Length);

        var scratch = new CpuScratch(size);
        foreach (var kernel in CpuBenchmarkRunner.Kernels)
        {
            ulong a = CpuKernels.Run(kernel, first, scratch);
            ulong b = CpuKernels.Run(kernel, second, new CpuScratch(size));
            Assert.Equal(a, b);
        }

        Assert.Equal(first.TextHash, CpuKernels.Compression(first, scratch));
        var digest = SHA256.HashData(first.HashInput);
        Assert.Equal(BitConverter.ToUInt64(digest), CpuKernels.Sha256(first.HashInput, 1));
        Assert.True(size.WorkUnits(CpuKernel.MatrixMultiply) > 0);
    }

    [Fact]
    public void Cpu_runner_times_every_kernel_without_checksum_errors()
    {
        var single = CpuBenchmarkRunner.Run(1, CpuWorkloadSize.Tiny, TimeSpan.FromMilliseconds(150), null, "test", CancellationToken.None);
        var multi = CpuBenchmarkRunner.Run(3, CpuWorkloadSize.Tiny, TimeSpan.FromMilliseconds(150), null, "test", CancellationToken.None);
        foreach (var m in new[] { single, multi })
        {
            Assert.Equal(0, m.ChecksumErrors);
            Assert.True(m.Repeats >= BenchmarkTiming.MinRepeats);
            Assert.All(CpuBenchmarkRunner.Kernels, k => Assert.Equal(m.Repeats, m.Throughput[k].Count));
            Assert.All(m.Throughput.Values.SelectMany(v => v), v => Assert.True(v > 0));
        }

        var result = BenchmarkResultBuilder.FromCpu(multi, multiCore: true, 0.2);
        Assert.Equal(BenchmarkTestStatus.Completed, result.Status);
        Assert.Contains(result.Metrics, m => m.Key == "cpuN.threads" && m.Value == 3 && m.Informational);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => CpuBenchmarkRunner.Run(1, CpuWorkloadSize.Tiny, TimeSpan.FromSeconds(1), null, "test", cts.Token));
    }

    [Fact]
    public void Pointer_chase_chain_is_one_cycle_through_every_cache_line()
    {
        const int slots = 1000;
        var chain = MemoryBenchmarkRunner.BuildChain(slots, 42);
        Assert.Equal(chain, MemoryBenchmarkRunner.BuildChain(slots, 42));

        var seen = new HashSet<int>();
        int index = 0;
        for (int i = 0; i < slots; i++)
        {
            Assert.True(seen.Add(index), "a line was visited twice before the lap ended");
            Assert.Equal(0, index % MemoryBenchmarkRunner.IntsPerLine);
            index = chain[index];
        }

        Assert.Equal(0, index);
        Assert.Equal(slots, seen.Count);
        Assert.Equal(0, MemoryBenchmarkRunner.Chase(chain, slots));
        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryBenchmarkRunner.BuildChain(1, 1));
    }

    [Fact]
    public void Memory_plan_sizes_buffers_and_splits_work_evenly()
    {
        const long mb = MemoryBenchmarkPlan.Megabyte;
        Assert.Equal(256 * mb, MemoryBenchmarkPlan.BufferBytes(null, 256 * mb, 64 * mb));
        Assert.Equal(256 * mb, MemoryBenchmarkPlan.BufferBytes(16_000 * mb, 256 * mb, 64 * mb));
        Assert.Equal(125 * mb, MemoryBenchmarkPlan.BufferBytes(1000 * mb, 256 * mb, 64 * mb));
        Assert.Equal(64 * mb, MemoryBenchmarkPlan.BufferBytes(100 * mb, 256 * mb, 64 * mb));
        Assert.Equal(16, MemoryBenchmarkPlan.BandwidthThreads(32));
        Assert.Equal(1, MemoryBenchmarkPlan.BandwidthThreads(0));

        long total = 1001;
        var chunks = Enumerable.Range(0, 4).Select(i => MemoryBenchmarkPlan.Chunk(total, 4, i)).ToList();
        Assert.Equal(total, chunks.Sum(c => c.Length));
        Assert.All(chunks.Zip(chunks.Skip(1)), p => Assert.Equal(p.First.Start + p.First.Length, p.Second.Start));

        var small = MemoryBenchmarkRunner.RunLatency(64 * 1024, TimeSpan.FromMilliseconds(20), null, "test", CancellationToken.None);
        Assert.True(small.Repeats >= BenchmarkTiming.MinRepeats);
        Assert.Equal(BenchmarkTestStatus.Completed, BenchmarkResultBuilder.FromLatency(small, 0.1).Status);
    }

    // ---- Results, conditions, history -------------------------------------------------------------------------

    [Fact]
    public void Disk_and_gpu_results_are_scored_from_their_raw_numbers()
    {
        var disk = BenchmarkResultBuilder.FromDisk(new DiskBenchmarkResult("C:", 3500, 3000, 60, 160, 15000, 40000, 256L * 1024 * 1024, ""), 5);
        Assert.Equal(1000, disk.Score);
        Assert.Contains("256 MB", disk.Note);

        var failed = BenchmarkResultBuilder.FromDisk(new DiskBenchmarkResult("C:", 0, 0, 0, 0, 0, 0, 0, "C: is not ready."), 0);
        Assert.Equal(BenchmarkTestStatus.Failed, failed.Status);
        Assert.Equal("C: is not ready.", failed.Note);

        Assert.Equal(160e9, BenchmarkResultBuilder.GpuStepsPerSecond(10, 64_000_000, 250), 3);
        var gpu = BenchmarkResultBuilder.FromGpu([10, 10, 10.2, 9.8, 2], 64_000_000, 250, "Test GPU", 15);
        Assert.Equal(1000, gpu.Score);
        Assert.Equal(4, gpu.Repeats); // the stalled sample was dropped
        Assert.Equal(BenchmarkTestStatus.Failed, BenchmarkResultBuilder.FromGpu([], 64_000_000, 250, "Test GPU", 15).Status);
    }

    [Fact]
    public void Conditions_warn_about_battery_load_and_heat()
    {
        Assert.Empty(BenchmarkConditionRules.Evaluate(new BenchmarkConditionFacts(false, null, 5, 50, 45)).Warnings);
        Assert.Empty(BenchmarkConditionRules.Evaluate(new BenchmarkConditionFacts(null, null, null, null, null)).Warnings);
        Assert.Empty(BenchmarkConditionRules.Evaluate(new BenchmarkConditionFacts(false, null, 15, 79.9, 79)).Warnings);

        var bad = BenchmarkConditionRules.Evaluate(new BenchmarkConditionFacts(true, 64, 37.4, 88, 81));
        Assert.Equal(4, bad.Warnings.Count);
        Assert.Contains(bad.Warnings, w => w.Contains("on battery (64 %)"));
        Assert.Contains(bad.Warnings, w => w.Contains("37 % busy"));
        Assert.False(bad.IsIdeal);

        Assert.Equal(25, BenchmarkConditionRules.CpuBusyPercent(0, 0, 0, 750, 500, 500)!.Value, 6);
        Assert.Null(BenchmarkConditionRules.CpuBusyPercent(10, 10, 10, 10, 10, 10));
        Assert.Null(BenchmarkConditionRules.CpuBusyPercent(10, 10, 10, 5, 20, 20));
    }

    [Fact]
    public void History_is_newest_first_without_duplicates_and_picks_a_fair_default_comparison()
    {
        var t0 = DateTimeOffset.Parse("2026-10-01T10:00:00Z", Invariant);
        var full1 = Run(t0, AllTests(1000));
        var full2 = Run(t0.AddDays(1), AllTests(1050));
        var partial = Run(t0.AddDays(2), [Completed(BenchmarkTestKind.GpuCompute, 1200)]);
        var newest = Run(t0.AddDays(3), AllTests(1100));

        var list = BenchmarkHistoryRules.Normalize([full1, newest, full2, partial, full2]);
        Assert.Equal([newest.Id, partial.Id, full2.Id, full1.Id], list.Select(r => r.Id));
        Assert.Equal(2, BenchmarkHistoryRules.Normalize(list, max: 2).Count);

        var pair = BenchmarkHistoryRules.DefaultComparison(list)!.Value;
        Assert.Equal(newest.Id, pair.After.Id);
        Assert.Equal(full2.Id, pair.Before.Id); // skips the partial run
        Assert.Null(BenchmarkHistoryRules.DefaultComparison([newest]));

        Assert.Equal("Before overclock", BenchmarkHistoryRules.CleanNote("  Before\noverclock "));
        Assert.Null(BenchmarkHistoryRules.CleanNote("   "));
        Assert.Equal(BenchmarkHistoryRules.MaxNoteLength, BenchmarkHistoryRules.CleanNote(new string('x', 200))!.Length);
    }

    [Fact]
    public async Task Json_history_round_trips_and_sets_aside_an_unreadable_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcchecker-bench-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, JsonBenchmarkHistoryStore.FileName);
            var store = new JsonBenchmarkHistoryStore(path);
            Assert.Empty(await store.LoadAsync(CancellationToken.None));

            var run = DemoBenchmarkData.SampleRuns(DateTimeOffset.Parse("2026-10-27T12:00:00+01:00", Invariant))[2];
            await store.AddAsync(run, CancellationToken.None);
            var loaded = Assert.Single(await new JsonBenchmarkHistoryStore(path).LoadAsync(CancellationToken.None));
            Assert.Equal(run.Id, loaded.Id);
            Assert.Equal(run.OverallScore, loaded.OverallScore);
            Assert.Equal(run.Warnings, loaded.Warnings);
            Assert.Equal(run.Tests.Select(t => t.Score), loaded.Tests.Select(t => t.Score));
            Assert.Equal(run.Tests[0].Metrics, loaded.Tests[0].Metrics);
            Assert.Equal(run.Started, loaded.Started);

            Assert.Empty(await store.DeleteAsync(run.Id, CancellationToken.None));

            await File.WriteAllTextAsync(path, "{ not json");
            Assert.Empty(await store.LoadAsync(CancellationToken.None));
            Assert.True(File.Exists(path + ".bad"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Demo_suite_runs_every_test_and_keeps_finished_tests_when_stopped()
    {
        var service = new DemoBenchmarkService(TimeSpan.FromMilliseconds(1));
        var updates = new List<ProgressUpdate>();
        var outcome = await BenchmarkSuite.RunAsync(service, BenchmarkCatalog.All.Reverse().ToList(), BenchmarkOptions.Default, "  Before Optimize ", [],
            new SyncProgress(updates.Add), CancellationToken.None);
        var run = outcome.Run!;
        Assert.False(outcome.Cancelled);
        Assert.Equal(BenchmarkCatalog.All, run.Tests.Select(t => t.Test));
        Assert.All(run.Tests, t => Assert.True(t.HasScore));
        Assert.InRange(run.OverallScore!.Value, 1100, 1800);
        Assert.Equal("Before Optimize", run.Note);
        Assert.Contains(updates, u => u.Stage.StartsWith("Memory latency (4 of 6)", StringComparison.Ordinal));

        using var cts = new CancellationTokenSource();
        var stopping = new SyncProgress(p =>
        {
            if (p.Stage.StartsWith("Memory bandwidth", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        });
        var stopped = await BenchmarkSuite.RunAsync(service, BenchmarkCatalog.All, BenchmarkOptions.Default, null, [], stopping, cts.Token);
        Assert.True(stopped.Cancelled);
        Assert.True(stopped.Run!.StoppedEarly);
        Assert.Equal([BenchmarkTestKind.CpuSingleCore, BenchmarkTestKind.CpuMultiCore], stopped.Run.Tests.Select(t => t.Test));

        var samples = DemoBenchmarkData.SampleRuns(DateTimeOffset.Now);
        Assert.Equal(3, samples.Count);
        Assert.True(samples[0].OverallScore > samples[2].OverallScore);
        Assert.NotEmpty(samples[2].Warnings);
        Assert.Equal(BenchmarkOptions.MinSeconds, new BenchmarkOptions(5).ClampedSeconds);
        Assert.Equal(BenchmarkOptions.MaxSeconds, new BenchmarkOptions(99).ClampedSeconds);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    private static BenchmarkTestResult Completed(BenchmarkTestKind test, int score, double compression = 120, double latencyNs = 100)
    {
        var metrics = test switch
        {
            BenchmarkTestKind.CpuSingleCore => new List<BenchmarkMetric>
            {
                new("cpu1.compression", "Compression", compression, "MB/s"),
                new("cpuN.threads", "Threads used", 1, "threads", Informational: true),
            },
            BenchmarkTestKind.MemoryLatency => [new("mem.latency", "Latency", latencyNs, "ns", LowerIsBetter: true)],
            _ => [],
        };
        return new BenchmarkTestResult(test, BenchmarkTestStatus.Completed, score, metrics, null, 12) { Repeats = 20 };
    }

    private static IReadOnlyList<BenchmarkTestResult> AllTests(int score) => BenchmarkCatalog.All.Select(t => Completed(t, score)).ToList();

    private static BenchmarkRun Run(DateTimeOffset started, IReadOnlyList<BenchmarkTestResult> tests) =>
        new(Guid.NewGuid(), started, 80, null, new BenchmarkSystemInfo("PC", "Test CPU", 12, 32L * 1024 * 1024 * 1024, "Test GPU", "C:"), tests, []);

    private sealed class SyncProgress(Action<ProgressUpdate> handler) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value) => handler(value);
    }
}
