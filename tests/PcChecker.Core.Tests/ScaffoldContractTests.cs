using PcChecker.Core.Common;
using PcChecker.Core.Displays;
using PcChecker.Core.Fans;
using PcChecker.Core.Latency;
using PcChecker.Core.Lighting;
using PcChecker.Core.Memory;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Storage;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Smoke tests for the pure helpers the v2 scaffold ships. Module tests go deeper in their own files.</summary>
public class ScaffoldContractTests
{
    [Fact]
    public void FanCurve_interpolates_between_points_and_clamps_outside()
    {
        var curve = new FanCurve { Points = [new(40, 30), new(80, 100)], HysteresisC = 0, RampStepPercent = 100 };
        Assert.Equal(30, FanCurveMath.Evaluate(curve, 20, null));
        Assert.Equal(65, FanCurveMath.Evaluate(curve, 60, null));
        Assert.Equal(100, FanCurveMath.Evaluate(curve, 95, null));
    }

    [Fact]
    public void FanCurve_ramp_limits_change_per_step()
    {
        var curve = new FanCurve { Points = [new(40, 30), new(80, 100)], HysteresisC = 0, RampStepPercent = 5 };
        Assert.Equal(35, FanCurveMath.Evaluate(curve, 80, 30));
        Assert.Equal(95, FanCurveMath.Evaluate(curve, 40, 100));
    }

    [Fact]
    public void FanCurve_hysteresis_holds_speed_until_temperature_drops_enough()
    {
        var curve = new FanCurve { Points = [new(40, 30), new(80, 100)], HysteresisC = 5, RampStepPercent = 100 };
        // At 60 °C the curve says 65 %; last was 70 % (set at ~63 °C). With +5 °C hysteresis the curve gives 73.75 ≥ 70, so hold.
        Assert.Equal(70, FanCurveMath.Evaluate(curve, 60, 70));
        // At 50 °C even with hysteresis (55 °C → 56.25 %) we are below 70, so it drops to the hysteresis value.
        Assert.Equal(56.25, FanCurveMath.Evaluate(curve, 50, 70), 2);
    }

    [Theory]
    [InlineData("Silent")]
    [InlineData("Balanced")]
    [InlineData("Performance")]
    public void FanCurve_presets_are_monotonic_and_end_at_full_speed(string preset)
    {
        var curve = FanCurveMath.Default("cpu.package", preset);
        Assert.Equal("cpu.package", curve.TemperatureSensorId);
        Assert.True(curve.Points.Count >= 3);
        for (int i = 1; i < curve.Points.Count; i++)
        {
            Assert.True(curve.Points[i].TemperatureC > curve.Points[i - 1].TemperatureC);
            Assert.True(curve.Points[i].Percent >= curve.Points[i - 1].Percent);
        }

        Assert.Equal(100, curve.Points[^1].Percent);
        Assert.Equal(3, FanCurveMath.BuiltInProfiles("cpu", "gpu").Count);
    }

    [Theory]
    [InlineData("#4CC2FF", 0x4C, 0xC2, 0xFF)]
    [InlineData("4cc2ff", 0x4C, 0xC2, 0xFF)]
    [InlineData("#FFF", 0xFF, 0xFF, 0xFF)]
    [InlineData("0x000000", 0, 0, 0)]
    public void RgbColor_hex_parses_and_round_trips(string hex, int r, int g, int b)
    {
        Assert.True(RgbColorMath.TryParseHex(hex, out var c));
        Assert.Equal(new RgbColor((byte)r, (byte)g, (byte)b), c);
        Assert.Equal(c, RgbColor.FromHex(c.ToHex()));
    }

    [Fact]
    public void RgbColor_rejects_bad_hex()
    {
        Assert.False(RgbColorMath.TryParseHex("#12345", out _));
        Assert.False(RgbColorMath.TryParseHex("nope", out _));
        Assert.Throws<FormatException>(() => RgbColor.FromHex("zz"));
    }

    [Fact]
    public void Hsv_conversions_round_trip_primary_colours()
    {
        Assert.Equal(new RgbColor(255, 0, 0), RgbColorMath.FromHsv(0, 1, 1));
        Assert.Equal(new RgbColor(0, 255, 0), RgbColorMath.FromHsv(120, 1, 1));
        Assert.Equal(new RgbColor(0, 0, 255), RgbColorMath.FromHsv(240, 1, 1));
        var (h, s, v) = RgbColorMath.ToHsv(new RgbColor(0, 0, 255));
        Assert.Equal(240, h, 1);
        Assert.Equal(1, s, 3);
        Assert.Equal(1, v, 3);
        Assert.Equal(new RgbColor(128, 0, 0), RgbColorMath.Scale(new RgbColor(255, 0, 0), 50));
        Assert.Equal(6, RgbColorMath.Rainbow(6).Count);
        Assert.Equal(new RgbColor(255, 0, 0), RgbColorMath.Rainbow(6)[0]);
    }

    [Fact]
    public void BlockMap_downsample_keeps_worst_state_per_cell()
    {
        var blocks = new BlockState[100];
        Array.Fill(blocks, BlockState.Ok);
        blocks[55] = BlockState.Bad;
        blocks[10] = BlockState.Slow;
        var cells = BlockMapBuilder.Downsample(blocks, 10);
        Assert.Equal(10, cells.Count);
        Assert.Equal(BlockState.Slow, cells[1]);
        Assert.Equal(BlockState.Bad, cells[5]);
        Assert.Equal(BlockState.Ok, cells[0]);
        Assert.Equal(100, BlockMapBuilder.Downsample(blocks, 2000).Count);
        Assert.Empty(BlockMapBuilder.Downsample([], 10));
    }

    [Fact]
    public void Benchmark_math_computes_throughput_and_iops()
    {
        Assert.Equal(100, BenchmarkMath.MegabytesPerSecond(100L * 1024 * 1024, TimeSpan.FromSeconds(1)), 3);
        Assert.Equal(2000, BenchmarkMath.Iops(4000, TimeSpan.FromSeconds(2)), 3);
        Assert.Equal(0, BenchmarkMath.MegabytesPerSecond(1, TimeSpan.Zero));
        Assert.True(BenchmarkMath.IsSlowBlock(600, 10));
        Assert.False(BenchmarkMath.IsSlowBlock(25, 10));
        Assert.Equal("SATA SSD class", BenchmarkMath.ClassifySequentialRead(530));
    }

    [Theory]
    [InlineData("Samsung SSD 990 PRO 2TB", "Samsung")]
    [InlineData("WDC WD40EZAZ-00SF3B0", "Western Digital")]
    [InlineData("ST4000DM004-2CV104", "Seagate")]
    [InlineData("CT1000P3SSD8", "Crucial")]
    [InlineData("KINGSTON SNV2S1000G", "Kingston")]
    public void Storage_vendor_rules_match_models(string model, string vendor)
    {
        var link = StorageVendorRules.ToolFor(model);
        Assert.NotNull(link);
        Assert.Equal(vendor, link!.Vendor);
        Assert.Null(StorageVendorRules.ToolFor("Generic Flash Disk"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Memory_patterns_verify_clean_fill_and_detect_corruption(int pattern)
    {
        var block = new ulong[4096];
        MemoryTestPatterns.Fill(block, pattern, seed: 7);
        var (clean, _) = MemoryTestPatterns.Verify(block, pattern, seed: 7);
        Assert.Equal(0, clean);
        block[100] ^= 1;
        block[2000] = ~block[2000];
        var (errors, offsets) = MemoryTestPatterns.Verify(block, pattern, seed: 7);
        Assert.Equal(2, errors);
        Assert.Equal([100, 2000], offsets);
    }

    [Fact]
    public void PageFile_rules_recommend_sensible_sizes_and_warn()
    {
        var (initial, max) = PageFileRules.Recommend(32L * 1024 * 1024 * 1024);
        Assert.Equal(PageFileRules.MaximumRecommendedMb, initial); // 1.5× would be 48 GB; capped
        Assert.Equal(initial, max);
        var (mid, midMax) = PageFileRules.Recommend(16L * 1024 * 1024 * 1024);
        Assert.Equal(24576, mid);
        Assert.Equal(32768, midMax);
        var (big, bigMax) = PageFileRules.Recommend(128L * 1024 * 1024 * 1024);
        Assert.Equal(PageFileRules.MaximumRecommendedMb, big);
        Assert.Equal(PageFileRules.MaximumRecommendedMb, bigMax);
        var (small, smallMax) = PageFileRules.Recommend(8L * 1024 * 1024 * 1024);
        Assert.Equal(12288, small);
        Assert.Equal(16384, smallMax);
        Assert.Empty(PageFileRules.Evaluate(16L << 30, true, []));
        Assert.Single(PageFileRules.Evaluate(16L << 30, false, []));
        Assert.Contains(PageFileRules.Evaluate(16L << 30, false, [new PageFileInfo("C:", 256, 512, false)]), w => w.Contains("very small"));
    }

    [Fact]
    public void Display_mode_math_dedupes_sorts_and_finds_closest()
    {
        var modes = new List<DisplayMode> { new(1920, 1080, 60, 32), new(2560, 1440, 144, 32), new(2560, 1440, 144, 32), new(2560, 1440, 60, 32), new(0, 0, 0, 0) };
        var normalized = DisplayModeMath.Normalize(modes);
        Assert.Equal(3, normalized.Count);
        Assert.Equal(new DisplayMode(2560, 1440, 144, 32), normalized[0]);
        Assert.Equal([144, 60], DisplayModeMath.RefreshRatesFor(modes, 2560, 1440));
        Assert.Equal(new DisplayMode(2560, 1440, 144, 32), DisplayModeMath.Closest(modes, new DisplayMode(2560, 1440, 165, 32)));
        Assert.Equal("16:9", DisplayModeMath.AspectRatioText(2560, 1440));
        Assert.Equal("21:9", DisplayModeMath.AspectRatioText(3440, 1440));
        Assert.Equal("16:10", DisplayModeMath.AspectRatioText(1920, 1200));
        Assert.Equal(150, DisplayModeMath.ScaleFromDpi(144));
    }

    [Fact]
    public void BcdEdit_parser_reads_values_and_booleans()
    {
        const string output = """
            Windows Boot Loader
            -------------------
            identifier              {current}
            device                  partition=C:
            description             Windows 11
            useplatformclock        Yes
            disabledynamictick      No
            timeout                 30
            """;
        var values = BcdEditParser.Parse(output);
        Assert.Equal("{current}", values["identifier"]);
        Assert.Equal("Windows 11", values["description"]);
        Assert.True(BcdEditParser.GetBool(values, "useplatformclock"));
        Assert.False(BcdEditParser.GetBool(values, "DisableDynamicTick"));
        Assert.Null(BcdEditParser.GetBool(values, "missing"));
        Assert.Equal(30, BcdEditParser.GetInt(values, "timeout"));
    }

    [Fact]
    public void PowerCfg_parser_reads_ac_dc_indexes_and_active_scheme()
    {
        const string query = """
              Power Setting GUID: 48e6b7a6-50f5-4782-a5d4-53bb50ff  (USB selective suspend setting)
                Current AC Power Setting Index: 0x00000001
                Current DC Power Setting Index: 0x00000000
            """;
        var (ac, dc) = PowerCfgQueryParser.ParseIndexes(query);
        Assert.Equal(1, ac);
        Assert.Equal(0, dc);
        var (id, name) = PowerCfgQueryParser.ParseActiveScheme("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)");
        Assert.Equal(new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), id);
        Assert.Equal("Balanced", name);
        Assert.Equal((null, null), PowerCfgQueryParser.ParseIndexes(null));
    }

    [Fact]
    public void Latency_rules_produce_findings_and_score()
    {
        var facts = LatencyReport.Empty() with { DpcTimePercent = 5, HpetForced = true, PowerPlan = "Balanced", PointerPrecision = true, HagsEnabled = false };
        var findings = LatencyRules.BuildFindings(facts, hasNvidiaGpu: true);
        Assert.Contains(findings, f => f.Id == "latency.dpc.high" && f.Severity == Severity.High);
        Assert.Contains(findings, f => f.ActionId == LatencyActionIds.HpetOff && f.AdvancedOnly);
        Assert.Contains(findings, f => f.ActionId == LatencyActionIds.HagsOn);
        int score = LatencyRules.Score(findings);
        Assert.InRange(score, 0, 99);
        Assert.Equal(100, LatencyRules.Score([]));
        Assert.Equal(2, LatencyRules.Jitter([10, 12, 10, 12]));
        Assert.Equal(0.5, LatencyRules.HundredNanosecondsToMs(5000));
        Assert.Equal(13, LatencyActionIds.All.Count);
    }

    [Fact]
    public void Overclock_capabilities_keep_old_constructor_and_gain_support_matrix()
    {
        var caps = new OverclockCapabilities(GpuVendor.Amd, "RX", GpuControlMethod.AmdAdl, null, "CPU", CpuControlMethod.RyzenAdj, "ryzenadj.exe", "Enabled", 6000, 6000, []);
        Assert.Empty(caps.SupportMatrix);
        var withMatrix = caps with { SupportMatrix = [("GPU core offset", "AMD", SupportLevel.Experimental, "Overdrive8")] };
        Assert.Single(withMatrix.SupportMatrix);
        Assert.True(withMatrix.CanTuneCpu);
        Assert.Equal(GpuControlMethod.IntelIgcl, Enum.Parse<GpuControlMethod>("IntelIgcl"));
        Assert.Equal(CpuControlMethod.RyzenMasterCli, Enum.Parse<CpuControlMethod>("RyzenMasterCli"));
    }
}
