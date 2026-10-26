using PcChecker.Core.Common;
using PcChecker.Core.Storage;
using Xunit;

namespace PcChecker.Core.Tests;

public class StorageTests
{
    // ---- ScanPlan ---------------------------------------------------------------------------------------------

    [Fact]
    public void Block_count_rounds_up()
    {
        Assert.Equal(0, ScanPlan.BlockCount(0, 8 * 1024 * 1024));
        Assert.Equal(1, ScanPlan.BlockCount(1, 8 * 1024 * 1024));
        Assert.Equal(3, ScanPlan.BlockCount(8 * 1024 * 1024 * 2L + 1, 8 * 1024 * 1024));
    }

    [Fact]
    public void Full_plan_covers_every_block_in_order()
    {
        var plan = ScanPlan.Build(1000, SurfaceScanOptions.Full());
        Assert.Equal(1000, plan.Count);
        Assert.Equal(0, plan[0]);
        Assert.Equal(999, plan[^1]);
        Assert.True(plan.Zip(plan.Skip(1)).All(p => p.Second == p.First + 1));
    }

    [Fact]
    public void Quick_plan_samples_edges_and_middle_deterministically()
    {
        const long total = 500_000; // a 4 TB drive at 8 MB blocks
        var options = SurfaceScanOptions.Quick();
        var plan = ScanPlan.Build(total, options);
        long edge = (long)Math.Ceiling(total * 0.02);

        Assert.True(plan.Count < total / 10, "quick scan must read far fewer blocks than the full drive");
        Assert.Equal(2 * edge + options.RandomSamples, plan.Count);
        Assert.Equal(0, plan[0]);
        Assert.Equal(total - 1, plan[^1]);
        Assert.Contains(edge - 1, plan);
        Assert.Contains(total - edge, plan);
        Assert.True(plan.Zip(plan.Skip(1)).All(p => p.Second > p.First), "plan must be ascending and distinct");
        Assert.True(plan.Any(i => i >= edge && i < total - edge), "some middle samples expected");

        var again = ScanPlan.Build(total, options);
        Assert.Equal(plan, again);
    }

    [Fact]
    public void Quick_plan_on_tiny_drive_reads_everything_at_most_once()
    {
        var plan = ScanPlan.Build(50, SurfaceScanOptions.Quick());
        Assert.Equal(50, plan.Count);
        Assert.Equal(plan.Distinct().Count(), plan.Count);
        Assert.Empty(ScanPlan.Build(0, SurfaceScanOptions.Quick()));
    }

    [Fact]
    public void Scan_estimate_uses_media_type()
    {
        var hdd = ScanPlan.Estimate(500_000, 8 * 1024 * 1024, ScanPlan.TypicalMBps("HDD", "SATA"));
        var nvme = ScanPlan.Estimate(500_000, 8 * 1024 * 1024, ScanPlan.TypicalMBps("SSD", "NVMe"));
        Assert.True(hdd > TimeSpan.FromHours(5));
        Assert.True(nvme < TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.Zero, ScanPlan.Estimate(0, 1, 100));
        Assert.True(SurfaceScanOptions.Quick(999).EffectiveBlockSizeMb == SurfaceScanOptions.MaxBlockSizeMb);
    }

    // ---- SurfaceScanMath / RunningMedian --------------------------------------------------------------------

    [Theory]
    [InlineData(600, 10, true)]   // over the 500 ms floor and 3x median
    [InlineData(25, 10, false)]   // under the floor
    [InlineData(1500, 600, false)] // under 3x median even though over the floor
    [InlineData(2000, 600, true)]
    public void Slow_block_threshold_requires_both_floor_and_multiple(double blockMs, double medianMs, bool slow)
    {
        Assert.Equal(slow, SurfaceScanMath.IsSlow(blockMs, medianMs));
    }

    [Fact]
    public void Running_median_tracks_the_window()
    {
        var median = new RunningMedian(5);
        Assert.Equal(0, median.Value);
        foreach (var v in new double[] { 10, 20, 30, 40, 50 })
        {
            median.Add(v);
        }

        Assert.Equal(30, median.Value);
        median.Add(1000);
        median.Add(1000);
        Assert.Equal(50, median.Value); // window is now 30,40,50,1000,1000
        Assert.Equal(25, SurfaceScanMath.Median([10, 20, 30, 40]));
    }

    [Fact]
    public void Eta_and_duration_formatting()
    {
        Assert.Null(SurfaceScanMath.Eta(0, 100, TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(90), SurfaceScanMath.Eta(10, 100, TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.Zero, SurfaceScanMath.Eta(100, 100, TimeSpan.FromSeconds(10)));
        Assert.Equal("2h 05m", SurfaceScanMath.FormatDuration(TimeSpan.FromMinutes(125)));
        Assert.Equal("12m 30s", SurfaceScanMath.FormatDuration(TimeSpan.FromSeconds(750)));
        Assert.Equal("45 s", SurfaceScanMath.FormatDuration(TimeSpan.FromSeconds(45)));
        Assert.Equal(100, SurfaceScanMath.BlockMBps(100L * 1024 * 1024, 1000), 3);
    }

    [Fact]
    public void Scan_summary_and_guidance_reflect_bad_blocks()
    {
        var bad = SurfaceScanMath.BuildSummary("WD40", ScanMode.Quick, 3, 0, 400, true);
        Assert.Contains("3 unreadable blocks", bad);
        Assert.Contains("Back up", bad);
        var good = SurfaceScanMath.BuildSummary("990 PRO", ScanMode.Full, 0, 0, 1000, true);
        Assert.Contains("every block read back cleanly", good);
        var stopped = SurfaceScanMath.BuildSummary("990 PRO", ScanMode.Full, 0, 2, 100, false);
        Assert.Contains("stopped early", stopped);

        Assert.Contains(SurfaceScanMath.Guidance(1, 0, isSystemDisk: true), g => g.Contains("Windows drive"));
        Assert.DoesNotContain(SurfaceScanMath.Guidance(1, 0, isSystemDisk: false), g => g.Contains("Windows drive"));
        Assert.Single(SurfaceScanMath.Guidance(0, 0, false));
        Assert.Contains(SurfaceScanMath.Guidance(0, 5, false), g => g.Contains("Re-run"));
    }

    // ---- Block maps ---------------------------------------------------------------------------------------------

    [Fact]
    public void Accumulator_keeps_worst_state_per_cell()
    {
        var acc = new BlockMapAccumulator(1000, 10);
        Assert.Equal(10, acc.CellCount);
        Assert.Equal(0, acc.CellOf(0));
        Assert.Equal(5, acc.CellOf(550));
        Assert.Equal(9, acc.CellOf(999));
        Assert.Equal(9, acc.CellOf(5000));

        acc.Set(550, BlockState.Ok);
        acc.Set(551, BlockState.Slow);
        acc.Set(552, BlockState.Ok);
        acc.Set(10, BlockState.Bad);
        acc.Set(11, BlockState.Ok);
        var snapshot = acc.Snapshot();
        Assert.Equal(BlockState.Slow, snapshot[5]);
        Assert.Equal(BlockState.Bad, snapshot[0]);
        Assert.Equal(BlockState.Untested, snapshot[9]);

        var small = new BlockMapAccumulator(3, 2000);
        Assert.Equal(3, small.CellCount);
    }

    [Fact]
    public void Downsample_matches_accumulator()
    {
        var blocks = new BlockState[1000];
        Array.Fill(blocks, BlockState.Ok);
        blocks[123] = BlockState.Bad;
        blocks[777] = BlockState.Slow;
        var acc = new BlockMapAccumulator(1000, 100);
        for (int i = 0; i < blocks.Length; i++)
        {
            acc.Set(i, blocks[i]);
        }

        Assert.Equal(BlockMapBuilder.Downsample(blocks, 100), acc.Snapshot());
        Assert.Equal(125, BlockMapBuilder.CreateEmpty(1000L * 1024 * 1024, 8).Length);
    }

    // ---- Benchmark reference ------------------------------------------------------------------------------------

    [Fact]
    public void Benchmark_comparison_verdicts()
    {
        Assert.Equal("As expected", BenchmarkReference.Verdict(500, 540));
        Assert.Equal("Below typical", BenchmarkReference.Verdict(300, 540));
        Assert.Equal("Much slower than typical", BenchmarkReference.Verdict(100, 540));
        Assert.Equal("No data", BenchmarkReference.Verdict(0, 540));
        Assert.Same(BenchmarkReference.Hdd, BenchmarkReference.Expected("HDD", "SATA"));
        Assert.Same(BenchmarkReference.NvmeGen3, BenchmarkReference.Expected("SSD", "NVMe"));
        Assert.Same(BenchmarkReference.SataSsd, BenchmarkReference.Expected("SSD", "SATA"));

        var result = new DiskBenchmarkResult("C:", 7120, 6540, 82, 215, 21000, 55000, 1 << 30, string.Empty);
        var rows = BenchmarkReference.Compare(result, BenchmarkReference.NvmeGen4);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal("As expected", r.Verdict));
        Assert.Contains("No action needed", BenchmarkReference.Summarize(result, BenchmarkReference.NvmeGen4));

        var slow = new DiskBenchmarkResult("D:", 60, 55, 0.3, 0.5, 80, 120, 1 << 30, string.Empty);
        Assert.Contains("much slower", BenchmarkReference.Summarize(slow, BenchmarkReference.Hdd));
    }

    // ---- Disk events ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(7, "Bad block", Severity.High)]
    [InlineData(11, "Controller error", Severity.Medium)]
    [InlineData(51, "Paging error", Severity.Medium)]
    [InlineData(55, "File system corruption", Severity.High)]
    [InlineData(98, "Volume check result", Severity.Info)]
    [InlineData(129, "Reset to device", Severity.Medium)]
    [InlineData(140, "Flush failed", Severity.High)]
    [InlineData(153, "I/O operation retried", Severity.Low)]
    [InlineData(157, "Disk surprise removed", Severity.Medium)]
    public void Known_disk_events_have_explanations(long id, string title, Severity severity)
    {
        var e = DiskEventCatalog.Explain(id);
        Assert.Equal(title, e.Title);
        Assert.Equal(severity, e.Severity);
        Assert.False(string.IsNullOrWhiteSpace(e.Advice));
        Assert.True(DiskEventCatalog.IsKnown(id));
        Assert.Contains(id, DiskEventCatalog.EventIds);
    }

    [Fact]
    public void Unknown_event_gets_generic_explanation_and_worst_severity_is_computed()
    {
        var e = DiskEventCatalog.Explain(9999);
        Assert.False(DiskEventCatalog.IsKnown(9999));
        Assert.Equal("Event 9999", e.Title);
        var now = DateTimeOffset.Now;
        Assert.Equal(Severity.Info, DiskEventCatalog.Worst([]));
        Assert.Equal(Severity.High, DiskEventCatalog.Worst([new DiskErrorEvent(now, "disk", 153, "", null), new DiskErrorEvent(now, "disk", 7, "", null)]));
    }

    [Theory]
    [InlineData(@"The device, \Device\Harddisk1\DR1, has a bad block.", @"\Device\Harddisk1\DR1")]
    [InlineData(@"Reset to device, \Device\RaidPort1, was issued.", @"\Device\RaidPort1")]
    [InlineData(@"Volume D: (\Device\HarddiskVolume5) is healthy.", @"\Device\HarddiskVolume5")]
    [InlineData("A corruption was discovered in the file system structure on volume E:.", "E:")]
    [InlineData("Nothing useful here.", null)]
    [InlineData("", null)]
    public void Device_is_extracted_from_event_text(string message, string? expected)
    {
        Assert.Equal(expected, DiskEventCatalog.ExtractDevice(message));
    }

    // ---- Codes and parsers ---------------------------------------------------------------------------------------

    [Fact]
    public void Storage_codes_map_wmi_values()
    {
        Assert.Equal("HDD", StorageCodes.MediaType(3));
        Assert.Equal("SSD", StorageCodes.MediaType(4));
        Assert.Equal("Unknown", StorageCodes.MediaType(null));
        Assert.Equal("NVMe", StorageCodes.BusType(17));
        Assert.Equal("SATA", StorageCodes.BusType(11));
        Assert.Equal("USB", StorageCodes.BusType(7));
        Assert.Equal(HealthStatus.Good, StorageCodes.Health(0));
        Assert.Equal(HealthStatus.Warning, StorageCodes.Health(1));
        Assert.Equal(HealthStatus.Critical, StorageCodes.Health(2));
        Assert.Equal(HealthStatus.Unknown, StorageCodes.Health(5));
        Assert.Equal(HealthStatus.Warning, StorageCodes.HealthFromStatusText("Pred Fail"));
        Assert.Equal("NVMe", StorageCodes.BusTypeFromInterface("SCSI", @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG_SSD_990\5&1"));
        Assert.Equal("USB", StorageCodes.BusTypeFromInterface("USB", @"USBSTOR\DISK&VEN_WD"));
        Assert.Equal("SATA", StorageCodes.BusTypeFromInterface("IDE", null));
    }

    [Theory]
    [InlineData("C", "C:")]
    [InlineData("c:", "C:")]
    [InlineData(@"D:\", "D:")]
    [InlineData(@"C:\Windows", null)]
    [InlineData("", null)]
    [InlineData("1:", null)]
    public void Drive_letters_normalise(string input, string? expected)
    {
        Assert.Equal(expected, StorageCodes.NormalizeDriveLetter(input));
    }

    [Fact]
    public void System_drive_detection()
    {
        Assert.True(StorageCodes.ContainsSystemDrive(["C:", "E:"], @"C:\"));
        Assert.False(StorageCodes.ContainsSystemDrive(["D:"], @"C:\"));
        Assert.False(StorageCodes.ContainsSystemDrive(["C:"], null));
    }

    [Fact]
    public void Chkdsk_output_is_interpreted()
    {
        var scheduled = StorageCommandParsers.ParseChkdsk("Chkdsk cannot run because the volume is in use by another process.\r\nThis volume will be checked the next time the system restarts.", 0, "C:");
        Assert.True(scheduled.RebootScheduled);
        Assert.Contains("next time Windows starts", scheduled.Summary);

        var clean = StorageCommandParsers.ParseChkdsk("Windows has scanned the file system and found no problems.\r\nNo further action is required.", 0, "D:");
        Assert.False(clean.RebootScheduled);
        Assert.True(clean.NoProblems);
        Assert.False(clean.ProblemsFound);
        Assert.Contains("no file system problems", clean.Summary);

        var bad = StorageCommandParsers.ParseChkdsk("  3907584000 KB total disk space.\r\n  1,024 KB in bad sectors.", 0, "D:");
        Assert.Equal(1024, bad.BadSectorKb);
        Assert.True(bad.ProblemsFound);
        Assert.Contains("bad sectors", bad.Summary);

        var busy = StorageCommandParsers.ParseChkdsk(string.Empty, 3, "E:");
        Assert.Contains("could not check", busy.Summary);
    }

    [Fact]
    public void Repair_and_optimize_switches_and_status_text()
    {
        Assert.Equal("-Scan", StorageCommandParsers.RepairVolumeSwitch(RepairMode.Scan));
        Assert.Equal("-SpotFix", StorageCommandParsers.RepairVolumeSwitch(RepairMode.SpotFix));
        Assert.Equal("-OfflineScanAndFix", StorageCommandParsers.RepairVolumeSwitch(RepairMode.OfflineScanAndFix));
        Assert.Equal("-Defrag", StorageCommandParsers.OptimizeSwitch("HDD"));
        Assert.Equal("-ReTrim", StorageCommandParsers.OptimizeSwitch("SSD"));
        Assert.Equal("C: /scan", StorageCommandParsers.ChkdskArguments("C:", true, false));
        Assert.Equal("C: /spotfix", StorageCommandParsers.ChkdskArguments("C:", false, false));
        Assert.Equal("C: /f /r", StorageCommandParsers.ChkdskArguments("C:", false, true));
        Assert.Contains("no corruption", StorageCommandParsers.DescribeRepairVolumeStatus("NoErrorsFound", RepairMode.Scan, "C:"));
        Assert.Contains("found and fixed", StorageCommandParsers.DescribeRepairVolumeStatus("ErrorsFixed", RepairMode.SpotFix, "C:"));
        Assert.Contains("Schedule an offline", StorageCommandParsers.DescribeRepairVolumeStatus("ErrorsNotFixed", RepairMode.SpotFix, "C:"));
    }

    // ---- Vendor rules --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Samsung SSD 990 PRO 2TB", "Samsung")]
    [InlineData("WD_BLACK SN850X 1000GB", "Western Digital")]
    [InlineData("ST2000DM008-2FR102", "Seagate")]
    [InlineData("KIOXIA-EXCERIA PLUS G3", "KIOXIA")]
    [InlineData("SK hynix PC801", "SK hynix")]
    [InlineData("Solidigm P44 Pro", "Solidigm")]
    [InlineData("ADATA XPG GAMMIX S70", "ADATA")]
    public void Vendor_tools_match_more_models(string model, string vendor)
    {
        var link = StorageVendorRules.ToolFor(model);
        Assert.NotNull(link);
        Assert.Equal(vendor, link!.Vendor);
        Assert.StartsWith("https://", link.Url);
    }

    [Fact]
    public void Vendor_tools_are_deduplicated_per_vendor()
    {
        var devices = new[]
        {
            new StorageDevice(0, "Samsung SSD 990 PRO 2TB", null, null, 1, "SSD", "NVMe", HealthStatus.Good, ["C:"], true),
            new StorageDevice(1, "Samsung SSD 870 EVO 1TB", null, null, 1, "SSD", "SATA", HealthStatus.Good, ["D:"], false),
            new StorageDevice(2, "Generic Flash Disk", null, null, 1, "Removable", "USB", HealthStatus.Unknown, ["E:"], false),
        };
        Assert.Single(StorageVendorRules.ToolsFor(devices));
        Assert.Empty(StorageVendorRules.ToolsFor([]));
    }

    // ---- Demo service -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Demo_quick_scan_reports_progress_and_finds_the_bad_blocks()
    {
        var demo = new DemoStorageToolsService();
        var reports = new List<SurfaceScanProgress>();
        var result = await ((ISurfaceScanner)demo).SurfaceScanAsync(1, SurfaceScanOptions.Quick(), new SyncProgress<SurfaceScanProgress>(reports.Add), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(2, result.BadBlocks);
        Assert.Equal(2, result.BadBlockOffsets.Count);
        Assert.NotEmpty(reports);
        Assert.Equal(reports[^1].BlocksTotal, reports[^1].BlocksDone);
        Assert.True(reports[^1].BlocksTotal < 400, "quick scan samples the demo drive");
        Assert.Contains(BlockState.Untested, result.Map);
        Assert.Contains("Back up", result.Summary);

        var ssd = await ((ISurfaceScanner)demo).SurfaceScanAsync(0, SurfaceScanOptions.Quick(), null, CancellationToken.None);
        Assert.Equal(0, ssd.BadBlocks);
        Assert.True(ssd.AvgMBps > 1000);
    }

    [Fact]
    public async Task Demo_tools_stream_output_and_report_reboot_when_scheduled()
    {
        var demo = new DemoStorageToolsService();
        var lines = new List<string>();
        var progress = new SyncProgress<ProgressUpdate>(p => { if (p.Detail is not null) { lines.Add(p.Detail); } });

        var scan = await demo.CheckDiskAsync("C:", true, false, progress, CancellationToken.None);
        Assert.True(scan.Success);
        Assert.False(scan.RebootRequired);
        Assert.True(lines.Count >= 7);

        var scheduled = await demo.CheckDiskAsync("C:", false, true, null, CancellationToken.None);
        Assert.True(scheduled.RebootRequired);

        var repair = await demo.RepairVolumeAsync("D:", RepairMode.OfflineScanAndFix, null, CancellationToken.None);
        Assert.True(repair.Success);
        Assert.True(repair.RebootRequired);

        var optimize = await demo.OptimizeAsync("D:", null, CancellationToken.None);
        Assert.Contains("defragmented", optimize.Message);

        var benchmark = await demo.BenchmarkAsync("C:", 1024, null, CancellationToken.None);
        Assert.Equal("C:", benchmark.Drive);
        Assert.Contains("No action needed", benchmark.Summary);

        var events = await demo.GetDiskErrorEventsAsync(30, CancellationToken.None);
        Assert.All(events, e => Assert.True(e.Time >= DateTimeOffset.Now.AddDays(-31)));
        Assert.True((await demo.GetDiskErrorEventsAsync(60, CancellationToken.None)).Count > events.Count);
    }

    /// <summary>Progress that invokes synchronously (no SynchronizationContext games in tests).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
