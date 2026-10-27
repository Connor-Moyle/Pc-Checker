using PcChecker.Core.Common;
using PcChecker.Core.Fans;
using PcChecker.Core.Hardware;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Power;
using PcChecker.Core.Reports;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class CpuTraitsTests
{
    [Theory]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", ZenGeneration.Zen5, CpuFormFactor.Desktop, "AM5", true, true)]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", ZenGeneration.Zen4, CpuFormFactor.Desktop, "AM5", true, true)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", ZenGeneration.Zen4, CpuFormFactor.Desktop, "AM5", true, false)]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", ZenGeneration.Zen3, CpuFormFactor.Desktop, "AM4", true, false)]
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", ZenGeneration.Zen4, CpuFormFactor.Desktop, "AM5", false, false)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", ZenGeneration.Zen2, CpuFormFactor.Desktop, "AM4", false, false)]
    [InlineData("AMD Ryzen 7 1700X Eight-Core Processor", ZenGeneration.Zen1, CpuFormFactor.Desktop, "AM4", false, false)]
    [InlineData("AMD Ryzen 5 8600G w/ Radeon 760M Graphics", ZenGeneration.Zen4, CpuFormFactor.DesktopApu, "AM5", false, false)]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", ZenGeneration.Zen4, CpuFormFactor.Laptop, "FP7/FP8", false, false)]
    [InlineData("AMD Ryzen 9 7945HX3D with Radeon Graphics", ZenGeneration.Zen4, CpuFormFactor.Laptop, "FL1", true, true)]
    [InlineData("AMD Ryzen Threadripper 7980X 64-Cores", ZenGeneration.Zen4, CpuFormFactor.Workstation, "sTR5", false, false)]
    public void Parses_ryzen_names(string name, ZenGeneration gen, CpuFormFactor form, string socket, bool x3d, bool dual)
    {
        var t = CpuTraits.Parse(name);
        Assert.True(t.IsAmd);
        Assert.Equal(gen, t.Generation);
        Assert.Equal(form, t.FormFactor);
        Assert.Equal(socket, t.Socket);
        Assert.Equal(x3d, t.IsX3D);
        Assert.Equal(dual, t.IsDualCcdX3D);
        Assert.Equal(dual, CpuTraits.IsDualCcdX3D(name));
    }

    [Fact]
    public void Ryzen_ai_and_handhelds_are_mobile_and_ryzenadj_capable()
    {
        Assert.True(CpuTraits.IsRyzenMobileOrApu("AMD Ryzen AI 9 HX 370 w/ Radeon 890M"));
        Assert.Equal(ZenGeneration.Zen5, CpuTraits.Parse("AMD Ryzen AI 9 HX 370 w/ Radeon 890M").Generation);
        Assert.Equal(CpuFormFactor.Handheld, CpuTraits.Parse("AMD Ryzen Z1 Extreme").FormFactor);
        Assert.True(CpuTraits.IsRyzenMobileOrApu("AMD Ryzen Z1 Extreme"));
        Assert.True(CpuTraits.IsRyzenMobileOrApu("AMD Ryzen 5 5600G with Radeon Graphics"));
        Assert.True(CpuTraits.IsRyzenMobileOrApu("AMD Ryzen 7 7840U w/ Radeon 780M Graphics"));
        // Dragon Range HX is desktop silicon; ryzenadj does not support it. Desktop chips never.
        Assert.False(CpuTraits.IsRyzenMobileOrApu("AMD Ryzen 9 7945HX with Radeon Graphics"));
        Assert.False(CpuTraits.IsRyzenMobileOrApu("AMD Ryzen 9 7950X 16-Core Processor"));
        Assert.False(CpuTraits.IsRyzenMobileOrApu("Intel(R) Core(TM) i7-14700K"));
    }

    [Fact]
    public void Tjmax_offsets_and_x3d_tuning()
    {
        Assert.Equal(95, CpuTraits.Parse("AMD Ryzen 9 9950X 16-Core Processor").TjMaxC);
        Assert.Equal(89, CpuTraits.Parse("AMD Ryzen 7 7800X3D 8-Core Processor").TjMaxC);
        Assert.True(CpuTraits.Parse("AMD Ryzen 9 7950X 16-Core Processor").BoostsToTjMax);
        Assert.False(CpuTraits.Parse("AMD Ryzen 7 7800X3D 8-Core Processor").BoostsToTjMax);
        Assert.Equal(20, CpuTraits.Parse("AMD Ryzen 7 1800X Eight-Core Processor").TctlOffsetC);
        Assert.Equal(10, CpuTraits.Parse("AMD Ryzen 7 2700X Eight-Core Processor").TctlOffsetC);
        Assert.Equal(27, CpuTraits.Parse("AMD Ryzen Threadripper 1950X 16-Core Processor").TctlOffsetC);
        Assert.Equal(X3dTuning.Locked, CpuTraits.Parse("AMD Ryzen 7 5800X3D").X3dTuning);
        Assert.Equal(X3dTuning.PboAndCurveOptimizerOnly, CpuTraits.Parse("AMD Ryzen 7 7800X3D").X3dTuning);
        Assert.Equal(X3dTuning.Unlocked, CpuTraits.Parse("AMD Ryzen 7 9800X3D").X3dTuning);
    }

    [Fact]
    public void Identifier_fills_the_generation_and_describe_reads_well()
    {
        Assert.Equal(ZenGeneration.Zen4, CpuTraits.FromIdentifier("AMD64 Family 25 Model 97 Stepping 2"));
        Assert.Equal(ZenGeneration.Zen3, CpuTraits.FromIdentifier("AMD64 Family 25 Model 33 Stepping 0"));
        Assert.Equal(ZenGeneration.Zen5, CpuTraits.FromIdentifier("AMD64 Family 26 Model 68 Stepping 0"));
        Assert.Equal(ZenGeneration.Zen2, CpuTraits.FromIdentifier("AMD64 Family 23 Model 113 Stepping 0"));
        Assert.Null(CpuTraits.FromIdentifier("Intel64 Family 6 Model 183"));
        Assert.Equal("Zen 4 · AM5 · 3D V-Cache (two CCDs)", CpuTraits.Describe(CpuTraits.Parse("AMD Ryzen 9 7950X3D 16-Core Processor")));
        Assert.Null(CpuTraits.Describe(CpuTraits.Parse("Intel(R) Core(TM) i9-14900K")));
        Assert.Equal("DDR5", CpuTraits.Parse("AMD Ryzen 5 7600").MemoryType);
        Assert.Equal("DDR4", CpuTraits.Parse("AMD Ryzen 5 5600X").MemoryType);
    }
}

public class IntegratedGpuRuleTests
{
    [Theory]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD Radeon 780M Graphics", true)]
    [InlineData("AMD Radeon(TM) 760M", true)]
    [InlineData("AMD Radeon 610M", true)]
    [InlineData("AMD Radeon 8060S Graphics", true)]
    [InlineData("AMD Radeon 890M Graphics", true)]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics", true)]
    [InlineData("Radeon RX Vega 11 Graphics", true)]
    [InlineData("AMD Radeon RX Vega 64", false)]
    [InlineData("AMD Radeon RX 7600M XT", false)]
    [InlineData("AMD Radeon RX 6800S", false)]
    [InlineData("AMD Radeon RX 9070 XT", false)]
    public void One_rule_for_amd_integrated_graphics(string name, bool integrated)
    {
        Assert.Equal(integrated, HardwareMappings.IsIntegratedGpu(name, "AMD"));
        var gpu = new GpuInfo(name, "Advanced Micro Devices, Inc.", null, null, 8L * 1024 * 1024 * 1024, null, null, null, null, null, null, null,
            @"PCI\VEN_1002&DEV_15BF", "AMD", false, new Dictionary<string, string>());
        Assert.Equal(integrated, GpuVendorDetector.LooksIntegrated(gpu));
    }

    [Fact]
    public void Adapter_ranking_puts_a_big_uma_apu_after_a_discrete_card()
    {
        var ranked = Overclocking.GpuLoad.GpuAdapterRanking.Rank(
        [
            new Overclocking.GpuLoad.GpuAdapterInfo(0, "AMD Radeon 8060S Graphics", 0x1002, 0x1586, 16L * 1024 * 1024 * 1024, 0, false),
            new Overclocking.GpuLoad.GpuAdapterInfo(1, "NVIDIA GeForce RTX 4070", 0x10DE, 0x2786, 12L * 1024 * 1024 * 1024, 0, false),
        ]);
        Assert.Equal(1, ranked[0].Index);
    }
}

public class GpuSelectionTests
{
    internal static SensorReading R(string id, string hw, HardwareKind kind, SensorKind sk, string name, double? value, string unit = "") =>
        new(id, hw, kind, sk, name, value, value, value, unit);

    internal static SensorSnapshot Ryzen7000WithRtx() => new(DateTimeOffset.UtcNow,
    [
        R("/amdcpu/0/temperature/2", "AMD Ryzen 9 7950X 16-Core Processor", HardwareKind.Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)", 95),
        R("/amdcpu/0/temperature/3", "AMD Ryzen 9 7950X 16-Core Processor", HardwareKind.Cpu, SensorKind.Temperature, "CCD1 (Tdie)", 93),
        R("/amdcpu/0/power/0", "AMD Ryzen 9 7950X 16-Core Processor", HardwareKind.Cpu, SensorKind.Power, "Package", 200),
        R("/gpu-amd/0/temperature/0", "AMD Radeon(TM) Graphics", HardwareKind.Gpu, SensorKind.Temperature, "GPU Core", 90),
        R("/gpu-amd/0/load/0", "AMD Radeon(TM) Graphics", HardwareKind.Gpu, SensorKind.Load, "GPU Core", 1),
        R("/gpu-amd/0/clock/0", "AMD Radeon(TM) Graphics", HardwareKind.Gpu, SensorKind.Clock, "GPU Core", 2200),
        R("/gpu-amd/0/power/0", "AMD Radeon(TM) Graphics", HardwareKind.Gpu, SensorKind.Power, "GPU Package", 8),
        R("/gpu-nvidia/0/temperature/0", "NVIDIA GeForce RTX 4080", HardwareKind.Gpu, SensorKind.Temperature, "GPU Core", 66),
        R("/gpu-nvidia/0/temperature/2", "NVIDIA GeForce RTX 4080", HardwareKind.Gpu, SensorKind.Temperature, "GPU Hot Spot", 78),
        R("/gpu-nvidia/0/load/0", "NVIDIA GeForce RTX 4080", HardwareKind.Gpu, SensorKind.Load, "GPU Core", 98),
        R("/gpu-nvidia/0/clock/0", "NVIDIA GeForce RTX 4080", HardwareKind.Gpu, SensorKind.Clock, "GPU Core", 2700),
        R("/gpu-nvidia/0/power/0", "NVIDIA GeForce RTX 4080", HardwareKind.Gpu, SensorKind.Power, "GPU Package", 300),
        R("/gpu-nvidia/0/smalldata/1", "NVIDIA GeForce RTX 4080", HardwareKind.Gpu, SensorKind.Data, "GPU Memory Total", 16376, "MB"),
    ]);

    [Fact]
    public void The_discrete_card_wins_over_the_integrated_radeon_listed_first()
    {
        var snapshot = Ryzen7000WithRtx();
        Assert.Equal("/gpu-nvidia/0", GpuSelection.PrimaryGpuKey(snapshot.Readings));
        var picked = SensorPicker.Pick(snapshot);
        Assert.Equal(98, picked.GpuLoadPercent);
        Assert.Equal(2700, picked.GpuCoreClockMhz);
        Assert.Equal(66, picked.GpuTempC);
        Assert.Equal(66, SensorPicker.MaxGpuTemp(snapshot));
        var head = SnapshotSummary.Summarize(snapshot);
        Assert.Equal(98, head.GpuLoadPercent);
        Assert.Equal("NVIDIA GeForce RTX 4080", head.GpuName);
        Assert.Equal(98, OverlaySensorMatcher.Resolve(snapshot, OverlayMetricIds.GpuLoad).Value);
        Assert.Equal(66, TemperatureRoles.PickGpuCoreTemperature(snapshot.Readings)!.Value);
    }

    [Fact]
    public void Dedicated_memory_breaks_a_tie_between_unknown_names()
    {
        var readings = new List<SensorReading>
        {
            R("/gpu-x/0/temperature/0", "Card A", HardwareKind.Gpu, SensorKind.Temperature, "GPU Core", 40),
            R("/gpu-x/1/temperature/0", "Card B", HardwareKind.Gpu, SensorKind.Temperature, "GPU Core", 50),
            R("/gpu-x/1/smalldata/0", "Card B", HardwareKind.Gpu, SensorKind.Data, "D3D Dedicated Memory Total", 8192, "MB"),
        };
        Assert.Equal("/gpu-x/1", GpuSelection.PrimaryGpuKey(readings));
        Assert.Equal(2, GpuSelection.PrimaryGpuOnly(readings).Count);
    }

    [Fact]
    public void Apu_power_is_not_counted_twice()
    {
        var readings = new List<SensorReading>
        {
            R("/amdcpu/0/power/0", "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", HardwareKind.Cpu, SensorKind.Power, "Package", 45),
            R("/gpu-amd/0/power/0", "AMD Radeon 780M Graphics", HardwareKind.Gpu, SensorKind.Power, "GPU PPT", 45),
        };
        var breakdown = PowerMath.Aggregate(readings);
        Assert.DoesNotContain(breakdown.Components, c => c.Source == PowerSource.Gpu);
        Assert.Equal(PowerSource.Gpu, PowerMath.Classify(R("/gpu-amd/1/power/0", "AMD Radeon RX 7900 XTX", HardwareKind.Gpu, SensorKind.Power, "GPU PPT", 300)));
    }
}

public class TemperatureRoleTests
{
    private static SensorReading R(string name, HardwareKind kind, double value, string hw = "AMD Radeon RX 7900 XTX") =>
        GpuSelectionTests.R("id." + name, hw, kind, SensorKind.Temperature, name, value);

    [Theory]
    [InlineData("GPU Core", TemperatureRole.GpuCore)]
    [InlineData("GPU Hot Spot", TemperatureRole.GpuHotSpot)]
    [InlineData("GPU Memory", TemperatureRole.GpuMemory)]
    [InlineData("GPU Memory Junction", TemperatureRole.GpuMemory)]
    [InlineData("GPU VR SoC", TemperatureRole.GpuVr)]
    [InlineData("GPU Liquid", TemperatureRole.GpuLiquid)]
    public void Gpu_roles(string name, TemperatureRole role) => Assert.Equal(role, TemperatureRoles.Classify(R(name, HardwareKind.Gpu, 50)));

    [Fact]
    public void Tdie_wins_over_raw_tctl_on_old_ryzen()
    {
        const string cpu = "AMD Ryzen 7 1800X Eight-Core Processor";
        var readings = new List<SensorReading>
        {
            R("Core (Tctl)", HardwareKind.Cpu, 80, cpu),
            R("Core (Tdie)", HardwareKind.Cpu, 60, cpu),
            R("CCD1 (Tdie)", HardwareKind.Cpu, 61, cpu),
        };
        Assert.Equal("Core (Tdie)", TemperatureRoles.PickCpuTemperature(readings)!.Name);
        Assert.Equal(TemperatureRole.CpuTctlRaw, TemperatureRoles.Classify(readings[0]));
        Assert.Equal(TemperatureRole.CpuCore, TemperatureRoles.Classify(readings[2]));
        Assert.Equal(60, SensorPicker.MaxCpuTemp(new SensorSnapshot(DateTimeOffset.UtcNow, readings)));
        Assert.Null(TemperatureRoles.ThresholdsFor(readings[0], new MonitoringSettings()));
        Assert.Equal(60, OverlaySensorMatcher.Resolve(new SensorSnapshot(DateTimeOffset.UtcNow, readings), OverlayMetricIds.CpuTemp).Value);
    }

    [Fact]
    public void Ryzen_7000_at_95_is_not_critical_with_stock_settings()
    {
        var settings = new MonitoringSettings();
        var tctl = R("Core (Tctl/Tdie)", HardwareKind.Cpu, 95, "AMD Ryzen 9 7950X 16-Core Processor");
        Assert.Equal((90d, 98d), TemperatureRoles.ThresholdsFor(tctl, settings));
        var tracker = new AlertTracker();
        var alert = tracker.Evaluate(tctl, settings, DateTimeOffset.UtcNow);
        Assert.NotNull(alert);
        Assert.NotEqual(Severity.Critical, alert!.Severity);
        Assert.Null(FanSafetyRules.FindCriticalReading([tctl], settings));

        // A user's own thresholds always win, and Intel keeps the stock values.
        Assert.Equal((80d, 90d), TemperatureRoles.CpuThresholds(new MonitoringSettings { CpuWarnC = 80, CpuCritC = 90 }, "AMD Ryzen 9 7950X"));
        Assert.Equal((85d, 95d), TemperatureRoles.CpuThresholds(settings, "Intel(R) Core(TM) i9-14900K"));
        Assert.Equal(98, TemperatureRoles.EffectiveCpuStopC(90, "AMD Ryzen 9 9950X 16-Core Processor"));
        Assert.Equal(90, TemperatureRoles.EffectiveCpuStopC(90, "AMD Ryzen 7 7800X3D 8-Core Processor"));
        Assert.Equal(85, TemperatureRoles.EffectiveCpuStopC(85, "AMD Ryzen 9 9950X 16-Core Processor"));
    }

    [Fact]
    public void Radeon_junction_at_100_is_normal_but_core_limits_still_apply()
    {
        var settings = new MonitoringSettings();
        var hot = R("GPU Hot Spot", HardwareKind.Gpu, 100);
        var core = R("GPU Core", HardwareKind.Gpu, 75);
        Assert.Null(FanSafetyRules.FindCriticalReading([hot, core], settings));
        Assert.NotNull(FanSafetyRules.FindCriticalReading([R("GPU Hot Spot", HardwareKind.Gpu, 111)], settings));
        Assert.NotNull(FanSafetyRules.FindCriticalReading([R("GPU Core", HardwareKind.Gpu, 93)], settings));

        var snapshot = new SensorSnapshot(DateTimeOffset.UtcNow, [hot, core]);
        Assert.Equal(75, SensorPicker.MaxGpuTemp(snapshot));
        Assert.Null(SensorPicker.GpuLimitBreach(snapshot, 87));
        Assert.Contains("hot spot", SensorPicker.GpuLimitBreach(new SensorSnapshot(DateTimeOffset.UtcNow, [R("GPU Hot Spot", HardwareKind.Gpu, 110), core]), 87));
    }

    [Fact]
    public void Integrated_gpu_temperature_follows_the_cpu_limits()
    {
        var igpu = R("GPU Core", HardwareKind.Gpu, 94, "AMD Radeon(TM) Graphics");
        Assert.Equal(TemperatureRole.IntegratedGpu, TemperatureRoles.Classify(igpu));
        Assert.Null(FanSafetyRules.FindCriticalReading([igpu], new MonitoringSettings()));
    }
}
