using PcChecker.Core.Fans;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Overclocking.GpuLoad;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class AmdOverdrive8Tests
{
    private const Od8Capability Rdna3 = Od8Capability.GfxClkLimits | Od8Capability.UclkMax | Od8Capability.PowerLimit | Od8Capability.TemperatureSystem
        | Od8Capability.FanCurve | Od8Capability.FanZeroRpmControl | Od8Capability.FanSpeedMin | Od8Capability.OdVoltageLimit;

    /// <summary>An RX 7900 XTX-like init table, including entries a reset must never touch.</summary>
    private static List<Od8Range> Ranges()
    {
        var list = new List<Od8Range>
        {
            new(Od8Feature.GfxClkFmax, 500, 3000, 2500),
            new(Od8Feature.GfxClkFmin, 500, 3000, 500),
            new(Od8Feature.UclkFmax, 1250, 1500, 1250),
            new(Od8Feature.PowerPercentage, -10, 15, 0),
            new(Od8Feature.FanMinSpeed, 15, 100, 15),
            new(Od8Feature.OperatingTempMax, 90, 110, 110),
            new(14, 0, 1, 0), // AC timing
            new(Od8Feature.FanZeroRpmControl, 0, 1, 1),
            new(16, 0, 1, 0), // auto undervolt engine
            new(Od8Feature.OdVoltage, -200, 0, 0),
            new(Od8Feature.OverdriveInterfaceId, 7, 7, 7),
        };
        for (int p = 1; p <= 5; p++)
        {
            list.Add(new Od8Range(Od8Feature.FanCurveTemperature(p), 25, 100, 25 + (p * 10)));
            list.Add(new Od8Range(Od8Feature.FanCurveSpeed(p), 15, 100, 20 + (p * 10)));
        }

        return list;
    }

    [Fact]
    public void Capability_mask_decides_support_and_limits()
    {
        var ranges = Ranges();
        Assert.Equal(Od8Capability.OdVoltageLimit, AdlValueRules.CapabilityFor(Od8Feature.OdVoltage));
        Assert.Equal(Od8Capability.FanCurve, AdlValueRules.CapabilityFor(Od8Feature.FanCurveSpeed(3)));
        Assert.Null(AdlValueRules.CapabilityFor(Od8Feature.OverdriveInterfaceId));
        Assert.Null(AdlValueRules.CapabilityFor(14));

        Assert.True(AdlValueRules.IsSupported(AdlValueRules.Find(ranges, Od8Feature.OdVoltage), Rdna3));
        Assert.False(AdlValueRules.IsSupported(AdlValueRules.Find(ranges, Od8Feature.OdVoltage), Rdna3 & ~Od8Capability.OdVoltageLimit));
        Assert.False(AdlValueRules.IsSupported(AdlValueRules.Find(ranges, Od8Feature.OverdriveInterfaceId), Rdna3));

        var limits = AdlValueRules.BuildLimits(ranges, Rdna3);
        Assert.True(limits.VoltageOffsetSupported);
        Assert.Equal(-200, limits.VoltageOffsetMinMv);
        Assert.True(limits.FanCurve);
        Assert.True(limits.FanControl);
        Assert.False(AdlValueRules.BuildLimits(ranges, Rdna3 & ~Od8Capability.FanCurve).FanCurve);
    }

    [Fact]
    public void Reset_touches_only_managed_supported_features()
    {
        var ranges = Ranges();
        var ids = AdlValueRules.ResetFeatureIds(ranges, includeFans: false, Rdna3);
        Assert.Equal([Od8Feature.GfxClkFmax, Od8Feature.GfxClkFmin, Od8Feature.UclkFmax, Od8Feature.PowerPercentage, Od8Feature.OperatingTempMax, Od8Feature.OdVoltage], ids);
        Assert.DoesNotContain(14, ids);
        Assert.DoesNotContain(16, ids);
        Assert.DoesNotContain(Od8Feature.OverdriveInterfaceId, ids);

        var withFans = AdlValueRules.ResetFeatureIds(ranges, includeFans: true, Rdna3);
        Assert.Contains(Od8Feature.FanCurveSpeed(5), withFans);
        Assert.Contains(Od8Feature.FanZeroRpmControl, withFans);

        var entries = AdlValueRules.BuildResetEntries(77, ranges, includeFans: false, Rdna3);
        Assert.Equal(new Od8SetEntry(0, true, true), entries[Od8Feature.OdVoltage]);
        Assert.Equal(default, entries[Od8Feature.OverdriveInterfaceId]);
        Assert.Equal(default, entries[14]);

        var single = AdlValueRules.BuildSingleResetEntry(77, AdlValueRules.Find(ranges, Od8Feature.PowerPercentage)!);
        Assert.Single(single, e => e.Requested);
    }

    [Fact]
    public void Lower_max_frequency_keeps_the_minimum_100_mhz_below()
    {
        var fmin = new Od8Range(Od8Feature.GfxClkFmin, 500, 3000, 500);
        Assert.Null(AdlValueRules.FminFor(2400, 500, fmin));
        Assert.Equal(2200, AdlValueRules.FminFor(2300, 2250, fmin));
        Assert.Equal(500, AdlValueRules.FminFor(550, 500, fmin));
    }

    [Fact]
    public void Fixed_fan_speed_becomes_a_flat_curve_and_read_back_finds_ignored_values()
    {
        var curve = AdlValueRules.FlatFanCurve(Ranges(), Rdna3, 60);
        for (int p = 1; p <= 5; p++)
        {
            Assert.Equal(60, curve[Od8Feature.FanCurveSpeed(p)]);
            Assert.Equal(25 + (p * 10), curve[Od8Feature.FanCurveTemperature(p)]);
        }

        Assert.Equal(0, curve[Od8Feature.FanZeroRpmControl]);
        Assert.Equal(15, AdlValueRules.FlatFanCurve(Ranges(), Rdna3, 5)[Od8Feature.FanCurveSpeed(1)]);

        var requested = new Dictionary<int, int> { [Od8Feature.OdVoltage] = -50, [Od8Feature.PowerPercentage] = -10 };
        var current = new int[40];
        current[Od8Feature.PowerPercentage] = -10;
        Assert.Equal([Od8Feature.OdVoltage], AdlValueRules.NotTaken(requested, current));
        Assert.Equal("voltage offset", AdlValueRules.DescribeFeature(Od8Feature.OdVoltage));
    }

    [Fact]
    public void Amd_presets_undervolt_and_the_validator_checks_the_voltage_range()
    {
        Assert.Same(OverclockPresets.Amd, OverclockPresets.ForVendor(GpuVendor.Amd));
        Assert.Same(OverclockPresets.All, OverclockPresets.ForVendor(GpuVendor.Nvidia));
        var uv = OverclockPresets.Get(OverclockPresets.AmdUndervoltPowerName)!;
        Assert.Equal(-80, uv.GpuVoltageOffsetMv);
        Assert.Equal(90, uv.GpuPowerLimitPercent);
        Assert.False(uv.IsStock);
        Assert.Contains(OverclockPresets.Describe(uv), l => l.Contains("-80 mV"));
        Assert.All(OverclockPresets.Amd, p => Assert.Equal(0, p.GpuMemoryOffsetMhz));

        var settings = new OverclockSettings { ConsentAccepted = true };
        var limits = new GpuLimits(-500, 200, 0, 200, 90, 115, 90, 110, true) { VoltageOffsetMinMv = -200, VoltageOffsetMaxMv = 0 };
        var caps = new OverclockCapabilities(GpuVendor.Amd, "AMD Radeon RX 7900 XTX", GpuControlMethod.AmdAdl, limits, null, CpuControlMethod.None, null, "Enabled", null, null, []);
        Assert.True(OverclockSafetyValidator.Validate(uv, caps, settings).Success);
        Assert.False(OverclockSafetyValidator.Validate(new OverclockProfile { GpuVoltageOffsetMv = -300 }, caps, settings).Success);
        var noVolt = caps with { GpuLimits = limits with { VoltageOffsetMinMv = 0, VoltageOffsetMaxMv = 0 } };
        Assert.Contains("does not offer", OverclockSafetyValidator.Validate(uv, noVolt, settings).Message);
        Assert.Equal(-200, OverclockSafetyValidator.Clamp(new OverclockProfile { GpuVoltageOffsetMv = -500 }, settings, limits).GpuVoltageOffsetMv);
        Assert.Equal(0, OverclockSafetyValidator.Clamp(new OverclockProfile { GpuVoltageOffsetMv = -50 }, settings, noVolt.GpuLimits).GpuVoltageOffsetMv);
    }

    [Fact]
    public void Radeon_fan_rules_and_the_support_matrix()
    {
        Assert.True(AmdGpuFanRules.IsOverdrive8Card("AMD Radeon RX 7900 XTX"));
        Assert.True(AmdGpuFanRules.IsOverdrive8Card("AMD Radeon RX 6800M"));
        Assert.True(AmdGpuFanRules.IsOverdrive8Card("AMD Radeon RX 9070 XT"));
        Assert.True(AmdGpuFanRules.IsOverdrive8Card("AMD Radeon VII"));
        Assert.False(AmdGpuFanRules.IsOverdrive8Card("Radeon RX 580 Series"));
        Assert.False(AmdGpuFanRules.IsOverdrive8Card("AMD Radeon RX Vega 64"));
        Assert.False(AmdGpuFanRules.HostControlIgnored("NVIDIA GeForce RTX 4080", isAmdGpu: false));

        var limits = new GpuLimits(-500, 200, 0, 200, 90, 115, 90, 110, true) { VoltageOffsetMinMv = -200, VoltageOffsetMaxMv = 0, FanCurve = true };
        var rows = SupportMatrixBuilder.Build(new SupportFacts(GpuVendor.Amd, GpuControlMethod.AmdAdl, limits, false, "AMD", CpuControlMethod.None, "Enabled", FanHostAvailable: true)
        {
            GpuName = "AMD Radeon RX 7900 XTX",
        });
        Assert.Contains(rows, r => r.Feature == "GPU fan speed" && r.Level == SupportLevel.Experimental && r.Note.Contains("flat curve"));
        Assert.Contains(rows, r => r.Feature.StartsWith("GPU voltage offset") && r.Level == SupportLevel.Experimental);
        Assert.Contains(rows, r => r.Feature == "GPU minimum clock");

        var noTuner = SupportMatrixBuilder.Build(new SupportFacts(GpuVendor.Amd, GpuControlMethod.None, null, false, "AMD", CpuControlMethod.None, "Enabled", FanHostAvailable: true)
        {
            GpuName = "AMD Radeon RX 6700 XT",
        });
        Assert.Contains(noTuner, r => r.Feature == "GPU fan speed" && r.Level == SupportLevel.Unsupported);

        var off = SupportMatrixBuilder.Build(new SupportFacts(GpuVendor.Amd, GpuControlMethod.AmdAdl, limits, false, "AMD", CpuControlMethod.None, "Enabled", false)
        {
            GpuTuningSwitchedOff = true,
        });
        Assert.Contains(off, r => r.Note.Contains("Overdrive is disabled in AMD Software"));
    }
}

public class AmdCpuToolTests
{
    private static readonly string Pf = Path.Combine(Path.GetTempPath(), "pf");

    [Fact]
    public void Ryzenadj_only_for_laptops_and_apus()
    {
        var roots = new[] { Pf };
        string adj = Path.Combine(Pf, "RyzenAdj", "ryzenadj.exe");
        bool Exists(string p) => p == adj || p.Contains("RyzenMaster", StringComparison.OrdinalIgnoreCase);

        Assert.Equal(CpuControlMethod.RyzenAdj, CpuToolLocator.Detect("AMD", Exists, null, roots, "AMD Ryzen 7 7840U w/ Radeon 780M Graphics").Method);
        Assert.Equal(CpuControlMethod.RyzenMaster, CpuToolLocator.Detect("AMD", Exists, null, roots, "AMD Ryzen 9 7950X 16-Core Processor").Method);
        Assert.Equal(CpuControlMethod.RyzenMaster, CpuToolLocator.Detect("AMD", Exists, null, roots, "AMD Ryzen 9 7945HX with Radeon Graphics").Method);
        Assert.Contains("WinRing0x64.sys is missing", CpuToolLocator.Detect("AMD", Exists, null, roots, "AMD Ryzen Z1 Extreme").Note);
    }

    [Fact]
    public void Ryzenadj_output_is_read_not_the_exit_code()
    {
        Assert.True(CpuToolLocator.ParseRyzenAdjOutput("Sucessfully set stapm_limit to 15000\nSucessfully set fast_limit to 19000\nSucessfully set slow_limit to 15000", "", 0).Success);
        Assert.False(CpuToolLocator.ParseRyzenAdjOutput("", "", 0).Success);
        Assert.Contains("blocks that driver", CpuToolLocator.ParseRyzenAdjOutput("unable to init ryzenadj", "", -1).Message);
        Assert.Contains("does not support this CPU", CpuToolLocator.ParseRyzenAdjOutput("CPU family not supported", "", 1).Message);
        Assert.False(CpuToolLocator.ParseRyzenAdjOutput("Sucessfully set stapm_limit to 15000\nFailed to set fast_limit", "", 0).Success);
        Assert.Equal(19, CpuToolLocator.FastLimitW(15));
        Assert.Equal(400, CpuToolLocator.FastLimitW(400));
        Assert.Contains("--fast-limit=19000", CpuToolLocator.RyzenAdjArguments(15));
    }

    [Fact]
    public void Ryzen_master_is_found_from_the_uninstall_entry_and_caveats_are_explained()
    {
        Assert.Equal(@"C:\Program Files\AMD\RyzenMaster", CpuToolLocator.InstallFolderFromUninstall(@"C:\Program Files\AMD\RyzenMaster\", null)?.TrimEnd('\\'));
        Assert.Equal(Path.GetDirectoryName(@"C:\Program Files\AMD\RyzenMaster\bin\AMD Ryzen Master.exe"),
            CpuToolLocator.InstallFolderFromUninstall(null, "\"C:\\Program Files\\AMD\\RyzenMaster\\bin\\AMD Ryzen Master.exe\",0"));
        Assert.Null(CpuToolLocator.InstallFolderFromUninstall(null, null));

        string dir = Path.Combine(Pf, "AMD", "RyzenMaster");
        string exe = Path.Combine(dir, "AMD Ryzen Master.exe");
        Assert.Equal(exe, CpuToolLocator.LocateRyzenMaster(p => p == exe, dir));

        Assert.Contains("Virtualization-Based Security", CpuToolLocator.RyzenMasterCaveat("AMD Ryzen 7 7700X", vbsRunning: true));
        Assert.Contains("laptops", CpuToolLocator.RyzenMasterCaveat("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", false));
        Assert.Null(CpuToolLocator.RyzenMasterCaveat("AMD Ryzen 7 7700X", false));
        Assert.DoesNotContain(VendorSoftwareCatalog.AmdRyzenMaster.CandidatePaths, p => p.Contains("CLI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Power_floor_and_wording_follow_the_tool()
    {
        Assert.Equal(5, OverclockSafetyValidator.MinCpuPowerFor(CpuControlMethod.RyzenAdj));
        Assert.Equal(15, OverclockSafetyValidator.MinCpuPowerFor(CpuControlMethod.IntelXtuCli));
        Assert.Contains("ryzenadj (laptops and APUs) or the BIOS", OverclockSafetyValidator.NoCpuToolMessage("AMD Ryzen 9 7950X"));
        Assert.DoesNotContain("Ryzen Master", OverclockSafetyValidator.NoCpuToolMessage("Intel Core i7"));

        var settings = new OverclockSettings { ConsentAccepted = true };
        var handheld = new OverclockCapabilities(GpuVendor.Amd, null, GpuControlMethod.None, null, "AMD Ryzen Z1 Extreme", CpuControlMethod.RyzenAdj, "x", "Unknown", null, null, []);
        Assert.True(OverclockSafetyValidator.Validate(new OverclockProfile { CpuPowerLimitW = 8 }, handheld, settings).Success);
        Assert.False(OverclockSafetyValidator.Validate(new OverclockProfile { CpuPowerLimitW = 8 }, handheld with { CpuMethod = CpuControlMethod.IntelXtuCli }, settings).Success);
    }

    [Fact]
    public void Bios_steps_follow_the_ryzen_model()
    {
        Assert.Contains(VendorSoftwareCatalog.BiosSteps("AMD", "AMD Ryzen 7 5800X3D 8-Core Processor"), s => s.Contains("locked"));
        Assert.Contains(VendorSoftwareCatalog.BiosSteps("AMD", "AMD Ryzen 7 7800X3D 8-Core Processor"), s => s.Contains("not multiplier"));
        Assert.Contains(VendorSoftwareCatalog.BiosSteps("AMD", "AMD Ryzen 9 9950X 16-Core Processor"), s => s.Contains("95 °C"));
        Assert.Contains(VendorSoftwareCatalog.BiosSteps("AMD", "AMD Ryzen 7 7840U w/ Radeon 780M Graphics"), s => s.Contains("no CPU tuning in the BIOS"));
        Assert.Contains(VendorSoftwareCatalog.BiosSteps("AMD", "AMD Ryzen 9 9950X"), s => s.Contains("AMD Overclocking (ASRock, MSI)"));
        Assert.DoesNotContain(VendorSoftwareCatalog.BiosSteps("Intel", "Intel Core i9"), s => s.Contains("Curve Optimizer"));
    }

    [Fact]
    public void Stress_result_mentions_single_core_testing_on_ryzen()
    {
        Assert.NotNull(StressAdvice.AmdSingleCoreNote("AMD Ryzen 9 7950X 16-Core Processor"));
        Assert.Null(StressAdvice.AmdSingleCoreNote("Intel(R) Core(TM) i9-14900K"));
        var verdict = StressVerdictBuilder.Build(new StressFacts(StressTarget.Cpu, GpuStressMode.BuiltIn, 600) { CpuChecks = 1000, MaxCpuTempC = 80, CpuName = "AMD Ryzen 9 7950X" });
        Assert.Contains(verdict.Reasons, r => r.Contains("CoreCycler"));
    }
}
