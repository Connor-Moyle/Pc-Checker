using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class NvApiMathTests
{
    [Theory]
    [InlineData(150000, 150)]
    [InlineData(-200000, -200)]
    [InlineData(1500, 2)]
    [InlineData(0, 0)]
    public void Khz_to_mhz_rounds_away_from_zero(int khz, int expected) => Assert.Equal(expected, NvApiMath.KhzToMhz(khz));

    [Fact]
    public void Mhz_to_khz_and_back_round_trips()
    {
        Assert.Equal(75, NvApiMath.KhzToMhz(NvApiMath.MhzToKhz(75)));
        Assert.Equal(-1000, NvApiMath.KhzToMhz(NvApiMath.MhzToKhz(-1000)));
    }

    [Fact]
    public void Power_per_cent_mille_conversions()
    {
        Assert.Equal(100, NvApiMath.PcmToPercent(100000));
        Assert.Equal(115000u, NvApiMath.PercentToPcm(115));
        Assert.Equal(70, NvApiMath.PcmToPercent(70000));
    }

    [Fact]
    public void Delta_range_detects_locked_domains_and_normalises_order()
    {
        var locked = NvApiMath.DeltaRangeToMhz(0, 0);
        Assert.False(locked.Editable);
        var swapped = NvApiMath.DeltaRangeToMhz(300000, -500000);
        Assert.True(swapped.Editable);
        Assert.Equal(-500, swapped.MinMhz);
        Assert.Equal(300, swapped.MaxMhz);
    }

    [Fact]
    public void Offset_is_clamped_into_the_driver_range()
    {
        Assert.Equal(300000, NvApiMath.OffsetMhzToDeltaKhz(900, -500000, 300000));
        Assert.Equal(-500000, NvApiMath.OffsetMhzToDeltaKhz(-900, -500000, 300000));
        Assert.Equal(50000, NvApiMath.OffsetMhzToDeltaKhz(50, -500000, 300000));
    }

    [Fact]
    public void Build_limits_collapses_missing_policies()
    {
        var limits = NvApiMath.BuildLimits((-500000, 300000), (-2000000, 1500000), null, null, false);
        Assert.Equal(-500, limits.CoreOffsetMinMhz);
        Assert.Equal(1500, limits.MemoryOffsetMaxMhz);
        Assert.Equal(limits.PowerLimitMinPercent, limits.PowerLimitMaxPercent);
        Assert.Equal(0, limits.TempLimitMaxC);
        var full = NvApiMath.BuildLimits((-500000, 300000), null, (70000u, 115000u), (65, 90), true);
        Assert.Equal(70, full.PowerLimitMinPercent);
        Assert.Equal(115, full.PowerLimitMaxPercent);
        Assert.Equal(90, full.TempLimitMaxC);
        Assert.True(full.FanControl);
        Assert.Equal(0, full.MemoryOffsetMaxMhz);
    }
}

public class GpuVendorDetectorTests
{
    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043", GpuVendor.Nvidia)]
    [InlineData(@"PCI\VEN_1002&DEV_744C", GpuVendor.Amd)]
    [InlineData(@"pci\ven_8086&dev_56a0", GpuVendor.Intel)]
    [InlineData(@"PCI\VEN_1AF4&DEV_1050", GpuVendor.Unknown)]
    [InlineData(null, GpuVendor.Unknown)]
    public void Vendor_from_pnp_id(string? pnp, GpuVendor expected) => Assert.Equal(expected, GpuVendorDetector.FromPnpId(pnp));

    [Fact]
    public void Name_fallback_and_integrated_detection()
    {
        Assert.Equal(GpuVendor.Amd, GpuVendorDetector.FromName("AMD Radeon RX 7800 XT"));
        Assert.Equal(GpuVendor.Intel, GpuVendorDetector.FromName("Intel(R) Arc(TM) A770 Graphics"));
        var igpu = new GpuInfo("Intel(R) UHD Graphics 770", "Intel", null, null, 128L * 1024 * 1024, null, null, null, null, null, null, null, @"PCI\VEN_8086&DEV_A780", "Intel", false, new Dictionary<string, string>());
        var dgpu = new GpuInfo("NVIDIA GeForce RTX 4070", "NVIDIA", null, null, 12L * 1024 * 1024 * 1024, null, null, null, null, null, null, null, @"PCI\VEN_10DE&DEV_2786", "NVIDIA", false, new Dictionary<string, string>());
        Assert.True(GpuVendorDetector.LooksIntegrated(igpu));
        Assert.False(GpuVendorDetector.LooksIntegrated(dgpu));
        Assert.Same(dgpu, GpuVendorDetector.PickPrimary([igpu, dgpu]));
        Assert.Equal(GpuVendor.Nvidia, GpuVendorDetector.Detect(dgpu));
    }
}

public class XmpStateEvaluatorTests
{
    [Fact]
    public void Disabled_when_configured_below_rated()
    {
        var r = XmpStateEvaluator.Evaluate([(4800, 6000), (4800, 6000)], "DDR5");
        Assert.Equal(XmpEvaluation.Disabled, r.State);
        Assert.Equal(4800, r.ConfiguredMhz);
        Assert.Equal(6000, r.MaxMhz);
        Assert.Contains("EXPO", r.Advice);
    }

    [Fact]
    public void Enabled_when_speeds_match_and_unknown_when_missing()
    {
        Assert.Equal(XmpEvaluation.Enabled, XmpStateEvaluator.Evaluate([(6000, 6000)]).State);
        Assert.Equal(XmpEvaluation.Enabled, XmpStateEvaluator.Evaluate([(5980, 6000)]).State);
        Assert.Equal(XmpEvaluation.Unknown, XmpStateEvaluator.Evaluate([(null, 6000)]).State);
        Assert.Equal(XmpEvaluation.Unknown, XmpStateEvaluator.Evaluate([]).State);
    }
}

public class XtuOutputParserTests
{
    [Fact]
    public void Parses_colon_separated_and_key_value_formats()
    {
        const string output = """
            # Intel XTU tunables
            59 : Processor Core Ratio : 53 : x
            60 : Core Voltage Offset : -0.050 : V
            id=61, name="Turbo Boost Power Max", value="125", unit="W"
            garbage line
            """;
        var t = XtuOutputParser.Parse(output);
        Assert.Equal(3, t.Count);
        Assert.Equal(59, t[0].Id);
        Assert.Equal("Processor Core Ratio", t[0].Name);
        Assert.Equal("53", t[0].Value);
        Assert.Equal("x", t[0].Unit);
        Assert.Equal("Turbo Boost Power Max", t[2].Name);
        Assert.Equal("W", t[2].Unit);
    }

    [Fact]
    public void Find_matches_exact_then_contains_and_parse_number_strips_units()
    {
        var list = XtuOutputParser.Parse("1 : Performance Core Ratio : 55\n2 : Core Voltage Offset : -0.1\n3 : Efficient Core Ratio : 42");
        Assert.Equal(1, XtuOutputParser.Find(list, XtuOutputParser.CoreRatioNames)!.Id);
        Assert.Equal(2, XtuOutputParser.Find(list, XtuOutputParser.VoltageOffsetNames)!.Id);
        Assert.Null(XtuOutputParser.Find(list, "Package Power Limit 1"));
        Assert.Equal(-0.05, XtuOutputParser.ParseNumber("-0.050 V"));
        Assert.Equal(53, XtuOutputParser.ParseNumber("53x"));
        Assert.Null(XtuOutputParser.ParseNumber("n/a"));
        Assert.Empty(XtuOutputParser.Parse(null));
    }

    [Fact]
    public void Formats_values_for_the_cli_invariantly()
    {
        Assert.Equal("-0.050", IntelXtuCli.FormatVoltageOffset(-50));
        Assert.Equal("0.100", IntelXtuCli.FormatVoltageOffset(100));
        Assert.Equal("53", IntelXtuCli.FormatRatio(53));
    }
}

public class StressWorkloadTests
{
    [Fact]
    public void Workload_is_deterministic_and_seed_sensitive()
    {
        ulong a = StressWorkload.Compute(42);
        ulong b = StressWorkload.Compute(42);
        ulong c = StressWorkload.Compute(43);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.True(StressWorkload.Verify(42, a));
        Assert.False(StressWorkload.Verify(42, a ^ 1));
    }

    [Fact]
    public async Task Runner_reports_iterations_and_no_errors_on_a_healthy_cpu()
    {
        var runner = new CpuStressRunner(2);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var stats = await runner.RunAsync(cts.Token);
        Assert.Equal(2, stats.Workers);
        Assert.True(stats.Iterations > 0);
        Assert.Equal(0, stats.Errors);
    }
}

public class ThrottleHeuristicTests
{
    [Fact]
    public void Sustained_clock_drop_under_load_is_flagged()
    {
        Assert.True(ThrottleHeuristic.Suspect([5300, 5300, 5280, 4400, 4350, 4300]));
        Assert.False(ThrottleHeuristic.Suspect([5300, 5280, 5310, 5250, 5290]));
        Assert.False(ThrottleHeuristic.Suspect([5300, 5300, 4400, 5300])); // single sample dip
        Assert.False(ThrottleHeuristic.Suspect([5300, 4000]));             // too few samples
    }

    [Fact]
    public void Idle_samples_are_ignored_when_loads_are_known()
    {
        double[] clocks = [5300, 5300, 800, 800, 800];
        double?[] loads = [100, 100, 5, 4, 3];
        Assert.False(ThrottleHeuristic.Suspect(clocks, loads));
        Assert.True(ThrottleHeuristic.Suspect(clocks, [100, 100, 100, 100, 100]));

        // A loaded drop confirmed only by the idle tail of the run is not throttling either.
        Assert.False(ThrottleHeuristic.Suspect([5300, 5300, 5300, 4000, 800, 800], [100, 100, 100, 100, 5, 5]));
        Assert.True(ThrottleHeuristic.Suspect([5300, 5300, 5300, 4000, 4000, 800], [100, 100, 100, 100, 100, 5]));
    }

    [Fact]
    public void Summary_explains_each_outcome()
    {
        Assert.Contains("watchdog", ThrottleHeuristic.Summarize(StressTarget.Cpu, 0, true, false, 95, null, 90, 87));
        Assert.Contains("Unstable", ThrottleHeuristic.Summarize(StressTarget.Cpu, 3, false, false, 80, null, 90, 87));
        Assert.Contains("throttling", ThrottleHeuristic.Summarize(StressTarget.Both, 0, false, true, 80, 70, 90, 87));
        Assert.StartsWith("Stable", ThrottleHeuristic.Summarize(StressTarget.Gpu, 0, false, false, null, 71, 90, 87));
    }

    [Fact]
    public void Missing_temperature_data_is_called_out_instead_of_passing()
    {
        Assert.True(ThrottleHeuristic.LacksTemperatureData(StressTarget.Cpu, null, 70));
        Assert.False(ThrottleHeuristic.LacksTemperatureData(StressTarget.Cpu, 80, null));
        Assert.True(ThrottleHeuristic.LacksTemperatureData(StressTarget.Gpu, 80, null));
        Assert.True(ThrottleHeuristic.LacksTemperatureData(StressTarget.Both, null, null));
        Assert.False(ThrottleHeuristic.LacksTemperatureData(StressTarget.Both, null, 70));

        string summary = ThrottleHeuristic.Summarize(StressTarget.Cpu, 0, false, false, null, null, 90, 87);
        Assert.DoesNotContain("Stable", summary);
        Assert.Contains("no temperature data", summary);
        Assert.Contains("watchdog", summary);
    }
}

public class OverclockSessionStoreTests
{
    [Fact]
    public void Session_round_trips_through_json()
    {
        var session = new OverclockSession
        {
            Profile = new OverclockProfile { Name = "Mild", GpuCoreOffsetMhz = 50, GpuMemoryOffsetMhz = 200, GpuTempLimitC = 83 },
            Clean = false,
            GpuMethod = GpuControlMethod.NvApi,
            CpuMethod = CpuControlMethod.IntelXtuCli,
            CpuTouched = true,
            XtuTunableIds = [59, 60],
        };
        var json = OverclockSessionStore.Serialize(session);
        Assert.Contains("\"NvApi\"", json);
        var back = OverclockSessionStore.Deserialize(json);
        Assert.NotNull(back);
        Assert.Equal("Mild", back.Profile.Name);
        Assert.Equal(50, back.Profile.GpuCoreOffsetMhz);
        Assert.Equal(83, back.Profile.GpuTempLimitC);
        Assert.False(back.Clean);
        Assert.Equal([59, 60], back.XtuTunableIds);
        Assert.Equal(CpuControlMethod.IntelXtuCli, back.CpuMethod);
    }

    [Fact]
    public void Malformed_or_empty_content_is_rejected()
    {
        Assert.Null(OverclockSessionStore.Deserialize(null));
        Assert.Null(OverclockSessionStore.Deserialize("   "));
        Assert.Null(OverclockSessionStore.Deserialize("{ not json"));
    }

    [Fact]
    public void Save_load_and_delete_use_the_file_system()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcchecker-oc-" + Guid.NewGuid().ToString("N"));
        var path = OverclockSessionStore.PathFor(dir);
        try
        {
            Assert.Null(OverclockSessionStore.Load(path));
            Assert.True(OverclockSessionStore.Save(path, new OverclockSession { Clean = false, Profile = new OverclockProfile { Name = "X" } }));
            var loaded = OverclockSessionStore.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal("X", loaded.Profile.Name);
            OverclockSessionStore.Delete(path);
            Assert.Null(OverclockSessionStore.Load(path));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}

public class SupportMatrixBuilderTests
{
    [Fact]
    public void Nvidia_with_full_limits_reports_full_support_and_bios_guidance_for_cpu()
    {
        var facts = new SupportFacts(GpuVendor.Nvidia, GpuControlMethod.NvApi, new GpuLimits(-500, 300, -2000, 1500, 70, 115, 65, 90, true), false, "Intel", CpuControlMethod.None, XmpEvaluation.Disabled, true);
        var rows = SupportMatrixBuilder.Build(facts);
        Assert.Contains(rows, r => r.Feature.StartsWith("GPU core") && r.Level == SupportLevel.Full && r.Vendor == "NVIDIA");
        Assert.Contains(rows, r => r.Feature == "GPU power limit" && r.Level == SupportLevel.Full);
        Assert.Contains(rows, r => r.Feature == "GPU fan speed" && r.Level == SupportLevel.Full && r.Note.Contains("Fans page"));
        Assert.Contains(rows, r => r.Feature.StartsWith("CPU all-core") && r.Level == SupportLevel.Unsupported && r.Note.Contains("XTU"));
        Assert.Contains(rows, r => r.Feature.StartsWith("Memory XMP") && r.Level == SupportLevel.MonitorOnly && r.Note.Contains("off"));
    }

    [Fact]
    public void Amd_and_intel_paths_are_marked_experimental_and_integrated_is_explained()
    {
        var amd = SupportMatrixBuilder.Build(new SupportFacts(GpuVendor.Amd, GpuControlMethod.AmdAdl, null, false, "AMD", CpuControlMethod.RyzenAdj, XmpEvaluation.Enabled, false));
        Assert.All(amd.Where(r => r.Feature.StartsWith("GPU") && r.Feature != "GPU fan speed"), r => Assert.Equal(SupportLevel.Experimental, r.Level));
        Assert.Contains(amd, r => r.Feature == "CPU power limit" && r.Level == SupportLevel.Experimental && r.Note.Contains("ryzenadj"));

        var intel = SupportMatrixBuilder.Build(new SupportFacts(GpuVendor.Intel, GpuControlMethod.IntelIgcl, null, false, "Intel", CpuControlMethod.IntelXtuCli, XmpEvaluation.Unknown, false));
        Assert.Contains(intel, r => r.Feature.StartsWith("GPU core") && r.Level == SupportLevel.Experimental && r.Note.Contains("Control Library"));
        Assert.Contains(intel, r => r.Feature.StartsWith("CPU all-core") && r.Level == SupportLevel.Partial);

        var igpu = SupportMatrixBuilder.Build(new SupportFacts(GpuVendor.Intel, GpuControlMethod.None, null, true, "Intel", CpuControlMethod.None, XmpEvaluation.Unknown, false));
        Assert.Contains(igpu, r => r.Feature.StartsWith("GPU core") && r.Level == SupportLevel.Unsupported && r.Note.Contains("Integrated"));
    }

    [Fact]
    public void Power_limit_without_a_policy_is_unsupported_even_on_nvidia()
    {
        var rows = SupportMatrixBuilder.Build(new SupportFacts(GpuVendor.Nvidia, GpuControlMethod.NvApi, new GpuLimits(-500, 300, -2000, 1500, 100, 100, 0, 0, false), false, "Intel", CpuControlMethod.None, XmpEvaluation.Enabled, false));
        Assert.Contains(rows, r => r.Feature == "GPU power limit" && r.Level == SupportLevel.Unsupported);
        Assert.Contains(rows, r => r.Feature == "GPU temperature limit" && r.Level == SupportLevel.Unsupported);
        Assert.Contains(rows, r => r.Feature == "GPU fan speed" && r.Level == SupportLevel.Partial);
    }
}

public class AdlValueRulesTests
{
    private static readonly Od8Range[] Ranges =
    [
        new(Od8Feature.GfxClkFmax, 500, 2800, 2500),
        new(Od8Feature.UclkFmax, 1000, 2300, 2000),
        new(Od8Feature.PowerPercentage, -50, 15, 0),
        new(Od8Feature.OperatingTempMax, 60, 110, 110),
        new(Od8Feature.FanMinSpeed, 0, 0, 0),
    ];

    [Fact]
    public void Offsets_map_to_absolute_values_within_the_driver_range()
    {
        var core = AdlValueRules.Find(Ranges, Od8Feature.GfxClkFmax)!;
        Assert.Equal((-2000, 300), AdlValueRules.OffsetRange(core));
        Assert.Equal(2600, AdlValueRules.OffsetToAbsolute(core, 100));
        Assert.Equal(2800, AdlValueRules.OffsetToAbsolute(core, 900));
        Assert.True(AdlValueRules.IsWithin(core, 2800));
        Assert.False(AdlValueRules.IsWithin(core, 2801));
        Assert.False(AdlValueRules.Find(Ranges, Od8Feature.FanMinSpeed)!.IsSupported);
    }

    [Fact]
    public void Power_percent_is_a_delta_and_limits_are_built_from_ranges()
    {
        Assert.Equal(10, AdlValueRules.PowerPercentToDelta(110));
        Assert.Equal(85, AdlValueRules.PowerDeltaToPercent(-15));
        var limits = AdlValueRules.BuildLimits(Ranges);
        Assert.Equal(-2000, limits.CoreOffsetMinMhz);
        Assert.Equal(300, limits.CoreOffsetMaxMhz);
        Assert.Equal(-1000, limits.MemoryOffsetMinMhz);
        Assert.Equal(50, limits.PowerLimitMinPercent);
        Assert.Equal(115, limits.PowerLimitMaxPercent);
        Assert.Equal(110, limits.TempLimitMaxC);
        Assert.False(limits.FanControl);
        Assert.Equal("not supported by this card", AdlValueRules.DescribeError(-8));
    }
}

public class IgclValueRulesTests
{
    [Fact]
    public void Snap_rounds_to_step_and_clamps()
    {
        var control = new IgclControl(true, true, -300, 300, 5, 0);
        Assert.Equal(55, IgclValueRules.Snap(control, 53));
        Assert.Equal(300, IgclValueRules.Snap(control, 900));
        Assert.Equal(-300, IgclValueRules.Snap(control, -900));
        Assert.True(IgclValueRules.IsWithin(control, 300));
        Assert.False(IgclValueRules.IsWithin(IgclControl.Unsupported, 0));
    }

    [Fact]
    public void Power_percent_and_milliwatts_convert_both_ways_and_limits_are_built()
    {
        var power = new IgclControl(true, false, 150000, 228000, 1000, 190000);
        Assert.Equal(209000, IgclValueRules.PercentToMilliwatts(power, 110));
        Assert.Equal(100, IgclValueRules.MilliwattsToPercent(power, 190000));
        var limits = IgclValueRules.BuildLimits(new IgclControl(true, true, -300, 300, 5, 0), IgclControl.Unsupported, power, new IgclControl(true, false, 60, 100, 1, 95));
        Assert.Equal(-300, limits.CoreOffsetMinMhz);
        Assert.Equal(0, limits.MemoryOffsetMaxMhz);
        Assert.Equal(79, limits.PowerLimitMinPercent);
        Assert.Equal(120, limits.PowerLimitMaxPercent);
        Assert.Equal(100, limits.TempLimitMaxC);
        Assert.Contains("waiver", IgclValueRules.DescribeResult(0x44000008));
        Assert.Contains("voltage-locked", IgclValueRules.DescribeResult(0x44000006));
    }
}

public class CpuToolLocatorTests
{
    [Fact]
    public void Intel_prefers_xtu_and_reports_none_when_missing()
    {
        var found = CpuToolLocator.Detect("Intel", p => p.Contains("XtuCli", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(CpuControlMethod.IntelXtuCli, found.Method);
        Assert.NotNull(found.ToolPath);
        var missing = CpuToolLocator.Detect("Intel", _ => false);
        Assert.Equal(CpuControlMethod.None, missing.Method);
        Assert.Contains("BIOS", missing.Note);
    }

    [Fact]
    public void Amd_prefers_ryzenadj_then_ryzen_master()
    {
        // Directory names without drive letters so the PATH string splits the same way on every OS.
        var appDir = Path.Combine("apps", "pcchecker");
        var toolsDir = Path.Combine("bin", "tools");
        var otherDir = Path.Combine("bin", "other");
        var pathVariable = toolsDir + Path.PathSeparator + otherDir;

        var adj = CpuToolLocator.Detect("AMD", p => p.EndsWith("ryzenadj.exe", StringComparison.OrdinalIgnoreCase), appDir, pathVariable);
        Assert.Equal(CpuControlMethod.RyzenAdj, adj.Method);
        Assert.Equal(Path.Combine(appDir, "tools", "ryzenadj.exe"), adj.ToolPath);
        var master = CpuToolLocator.Detect("AMD", p => p.Contains("RyzenMaster", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(CpuControlMethod.RyzenMaster, master.Method);
        var onPath = CpuToolLocator.LocateRyzenAdj(p => p.StartsWith(otherDir, StringComparison.OrdinalIgnoreCase), null, pathVariable);
        Assert.Equal(Path.Combine(otherDir, "ryzenadj.exe"), onPath);
        Assert.Equal("--stapm-limit=45000 --fast-limit=45000 --slow-limit=45000 --tctl-temp=90", CpuToolLocator.RyzenAdjArguments(45, 90));
    }
}

public class SensorPickerAndPresetTests
{
    private static SensorSnapshot Snapshot() => new(DateTimeOffset.Now,
    [
        new("gpu.clock", "RTX", HardwareKind.Gpu, SensorKind.Clock, "GPU Core", 2505, null, null, "MHz"),
        new("gpu.memclock", "RTX", HardwareKind.Gpu, SensorKind.Clock, "GPU Memory", 10501, null, null, "MHz"),
        new("gpu.temp", "RTX", HardwareKind.Gpu, SensorKind.Temperature, "GPU Core", 46, null, null, "°C"),
        new("gpu.hot", "RTX", HardwareKind.Gpu, SensorKind.Temperature, "GPU Hot Spot", 58, null, null, "°C"),
        new("gpu.power", "RTX", HardwareKind.Gpu, SensorKind.Power, "GPU Package", 42, null, null, "W"),
        new("cpu.core1", "i7", HardwareKind.Cpu, SensorKind.Temperature, "Core #1", 55, null, null, "°C"),
        new("cpu.pkg", "i7", HardwareKind.Cpu, SensorKind.Temperature, "CPU Package", 58, null, null, "°C"),
        new("cpu.clock", "i7", HardwareKind.Cpu, SensorKind.Clock, "CPU Core #1", 5300, null, null, "MHz"),
    ]);

    [Fact]
    public void Picks_the_right_readings_by_name_and_kind()
    {
        var live = SensorPicker.Pick(Snapshot());
        Assert.Equal(2505, live.GpuCoreClockMhz);
        Assert.Equal(10501, live.GpuMemoryClockMhz);
        Assert.Equal(46, live.GpuTempC);
        Assert.Equal(58, live.GpuHotSpotC);
        Assert.Equal(42, live.GpuPowerW);
        Assert.Equal(58, live.CpuTempC);
        Assert.Equal(5300, live.CpuClockMhz);
        Assert.Equal(58, SensorPicker.MaxGpuTemp(Snapshot()));
        Assert.Equal(58, SensorPicker.MaxCpuTemp(Snapshot()));
        Assert.Equal(LiveReadout.Empty, SensorPicker.Pick(null));
    }

    [Fact]
    public void Presets_are_copies_and_describe_changes_in_plain_english()
    {
        var mild = OverclockPresets.Get(OverclockPresets.MildName)!;
        mild.GpuCoreOffsetMhz = 999;
        Assert.Equal(50, OverclockPresets.Get(OverclockPresets.MildName)!.GpuCoreOffsetMhz);
        Assert.True(OverclockPresets.IsPreset("mild (+50 / +200)"));
        Assert.Null(OverclockPresets.Get("nope"));
        var lines = OverclockPresets.Describe(new OverclockProfile { GpuCoreOffsetMhz = 50, GpuPowerLimitPercent = 110, CpuVoltageOffsetMv = -50 });
        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, l => l.Contains("+50 MHz"));
        Assert.Contains(lines, l => l.Contains("-50 mV"));
        Assert.Single(OverclockPresets.Describe(OverclockProfile.Stock()));
    }

    [Fact]
    public void Vendor_catalog_maps_vendors_and_locates_installed_tools()
    {
        Assert.Equal("AMD Software: Adrenalin Edition", VendorSoftwareCatalog.ForGpu(GpuVendor.Amd).Name);
        Assert.Equal(VendorSoftwareCatalog.IntelXtu, VendorSoftwareCatalog.ForCpu("Intel Core i7-13700K"));
        Assert.Equal(VendorSoftwareCatalog.AmdRyzenMaster, VendorSoftwareCatalog.ForCpu("AMD Ryzen 7 7800X3D"));
        Assert.Null(VendorSoftwareCatalog.ForCpu("Qualcomm"));
        Assert.Equal("Intel", VendorSoftwareCatalog.CpuVendorName("GenuineIntel"));
        Assert.Equal("AMD", VendorSoftwareCatalog.CpuVendorName("AuthenticAMD"));
        Assert.Null(VendorSoftwareCatalog.NvidiaApp.Locate(_ => false));
        Assert.NotNull(VendorSoftwareCatalog.NvidiaApp.Locate(p => p.EndsWith("NVIDIA app.exe")));
        Assert.Contains(VendorSoftwareCatalog.BiosSteps("AMD"), s => s.Contains("Curve Optimizer"));
    }
}

public class DemoOverclockServiceTests
{
    [Fact]
    public async Task Demo_detects_apply_stress_and_reset_without_windows()
    {
        var demo = new DemoOverclockService();
        var caps = await demo.DetectAsync(CancellationToken.None);
        Assert.Equal(GpuControlMethod.NvApi, caps.GpuMethod);
        Assert.Equal(XmpEvaluation.Disabled, caps.XmpState);
        Assert.NotEmpty(caps.SupportMatrix);

        var apply = await demo.ApplyAsync(new OverclockProfile { Name = "Mild", GpuCoreOffsetMhz = 50, GpuMemoryOffsetMhz = 200 }, CancellationToken.None);
        Assert.True(apply.Success);
        var state = await demo.GetStateAsync(CancellationToken.None);
        Assert.Equal(50, state.Current.GpuCoreOffsetMhz);

        var updates = new List<ProgressUpdate>();
        var progress = new Progress<ProgressUpdate>(updates.Add);
        var result = await demo.RunStressTestAsync(StressTarget.Cpu, 5, 95, 95, progress, CancellationToken.None);
        Assert.True(result.Passed);
        Assert.False(result.AbortedByWatchdog);

        var aborted = await demo.RunStressTestAsync(StressTarget.Cpu, 20, 60, 95, null, CancellationToken.None);
        Assert.True(aborted.AbortedByWatchdog);
        Assert.True((await demo.GetStateAsync(CancellationToken.None)).Current.IsStock);

        Assert.True((await demo.ResetAsync(CancellationToken.None)).Success);
    }
}

public class OverclockTemperatureGateTests
{
    [Fact]
    public void Rejects_when_hot_and_allows_cool_or_unknown_readings()
    {
        var settings = new OverclockSettings { MaxCpuTempC = 90, MaxGpuTempLimitC = 87 };

        var cpuHot = OverclockSafetyValidator.CheckTemperatures(91, 60, settings);
        Assert.NotNull(cpuHot);
        Assert.False(cpuHot!.Success);
        Assert.Contains("CPU", cpuHot.Message);

        var cpuAtLimit = OverclockSafetyValidator.CheckTemperatures(90, null, settings);
        Assert.NotNull(cpuAtLimit);

        var gpuHot = OverclockSafetyValidator.CheckTemperatures(50, 87, settings);
        Assert.NotNull(gpuHot);
        Assert.Contains("GPU", gpuHot!.Message);

        Assert.Null(OverclockSafetyValidator.CheckTemperatures(70, 60, settings));
        Assert.Null(OverclockSafetyValidator.CheckTemperatures(null, null, settings));
        Assert.Null(OverclockSafetyValidator.CheckTemperatures(89.9, 86.9, settings));
    }
}

public class IgclResultCodeTests
{
    [Theory]
    [InlineData(0x00000000u, "OK")]
    [InlineData(0x40000009u, "unsupported version")]
    [InlineData(0x4000000Cu, "invalid API handle")]
    [InlineData(0x4000000Du, "invalid null handle")]
    [InlineData(0x4000000Fu, "invalid size")]
    [InlineData(0x40000010u, "unsupported size")]
    [InlineData(0x44000001u, "not supported")]
    [InlineData(0x44000003u, "frequency")]
    [InlineData(0x44000008u, "waiver")]
    [InlineData(0x4400FFFFu, "0x4400FFFF")]
    public void Result_codes_follow_the_public_igcl_header(uint code, string expected) =>
        Assert.Contains(expected, IgclValueRules.DescribeResult(code));
}
