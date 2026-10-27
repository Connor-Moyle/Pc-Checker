using PcChecker.Core.Common;
using PcChecker.Core.Debloat;
using PcChecker.Core.Gaming;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Latency;
using PcChecker.Core.Performance;
using PcChecker.Core.Power;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class X3dAndPlatformTests
{
    private const string X3d = "AMD Ryzen 9 7950X3D 16-Core Processor";

    private static LatencyReport Balanced() => LatencyReport.Empty() with
    {
        PowerPlan = "Balanced",
        PowerPlanId = PowerPlanParser.Balanced,
        CoreParkingAllowed = true,
        GameMode = true,
    };

    [Fact]
    public void Latency_does_not_push_high_performance_or_no_parking_on_dual_ccd_x3d()
    {
        var generic = LatencyRules.BuildFindings(Balanced(), cpuName: "AMD Ryzen 9 7950X 16-Core Processor");
        Assert.Contains(generic, f => f.Id == "latency.powerplan.balanced");
        Assert.Contains(generic, f => f.Id == "latency.coreparking.on");

        var x3d = LatencyRules.BuildFindings(Balanced(), cpuName: X3d);
        Assert.DoesNotContain(x3d, f => f.Id is "latency.powerplan.balanced" or "latency.coreparking.on");
        var ok = Assert.Single(x3d, f => f.Id == "latency.x3d.scheduling");
        Assert.Equal(Severity.Info, ok.Severity);

        var broken = LatencyRules.BuildFindings(Balanced() with
        {
            PowerPlan = "High performance",
            PowerPlanId = PowerPlanParser.HighPerformance,
            CoreParkingAllowed = false,
            GameMode = false,
        }, cpuName: X3d);
        var bad = Assert.Single(broken, f => f.Id == "latency.x3d.scheduling");
        Assert.Equal(Severity.Medium, bad.Severity);
        Assert.Contains("Balanced power plan", bad.Detail);
        Assert.Contains("core parking", bad.Detail);
        Assert.Equal(LatencyActionIds.GameModeOn, bad.ActionId);
    }

    [Fact]
    public void Action_and_debloat_warnings_appear_only_on_dual_ccd_x3d()
    {
        Assert.NotNull(AmdPlatformRules.ActionWarning("power.highperformance", X3d));
        Assert.NotNull(AmdPlatformRules.ActionWarning("latency.coreparking.off", X3d));
        Assert.NotNull(AmdPlatformRules.ActionWarning("gamebar.disable", X3d));
        Assert.Null(AmdPlatformRules.ActionWarning("gamemode.enable", X3d));
        Assert.Null(AmdPlatformRules.ActionWarning("power.highperformance", "AMD Ryzen 7 7800X3D 8-Core Processor"));
        Assert.Null(AmdPlatformRules.ActionWarning("power.highperformance", "Intel(R) Core(TM) i9-14900K"));

        var gamebar = DebloatCatalog.All.Single(t => t.Id == "apps.gamebar");
        Assert.Contains("X3D", gamebar.Warning);
        Assert.True(DebloatConfirmationText.X3dGameBarConflict([gamebar], X3d));
        Assert.StartsWith("Warning:", DebloatConfirmationText.Build([gamebar], revert: false, cpuName: X3d));
        Assert.False(DebloatConfirmationText.Build([gamebar], revert: false, cpuName: "AMD Ryzen 9 7950X").StartsWith("Warning:", StringComparison.Ordinal));
        Assert.DoesNotContain(DebloatPresets.All.SelectMany(p => p.TweakIds), id => id == "apps.gamebar");

        Assert.NotNull(GameProfileRules.PowerPlanWarning(PowerPlanParser.HighPerformance, X3d));
        Assert.Null(GameProfileRules.PowerPlanWarning(PowerPlanParser.Balanced, X3d));
        Assert.Null(GameProfileRules.PowerPlanWarning(null, X3d));
        Assert.Null(GameProfileRules.PowerPlanWarning(PowerPlanParser.HighPerformance, "AMD Ryzen 9 7950X"));
    }

    [Fact]
    public void Slowdown_rules_keep_balanced_and_game_bar_on_x3d()
    {
        var facts = new PerformanceFacts
        {
            HasBattery = false,
            ActivePowerPlan = "Balanced",
            ActivePowerPlanId = PowerPlanParser.Balanced,
            GameBarEnabled = true,
        };
        Assert.Contains(SlowdownRules.Evaluate(facts), f => f.Id == "power.balanced.desktop");
        var x3d = SlowdownRules.Evaluate(facts with { CpuName = X3d });
        Assert.DoesNotContain(x3d, f => f.Id is "power.balanced.desktop" or "gamebar.on");
    }

    [Fact]
    public void Chipset_checks_report_missing_drivers_and_the_vcache_service()
    {
        Assert.Empty(AmdPlatformRules.ChipsetItems(AmdPlatformFacts.None with { CpuName = "Intel(R) Core(TM) i7-13700K" }));

        var missing = AmdPlatformRules.ChipsetItems(new AmdPlatformFacts("AMD Ryzen 7 7700X 8-Core Processor", null, [], null, null));
        var item = Assert.Single(missing);
        Assert.Equal(HealthStatus.Warning, item.Status);
        Assert.Contains("AM5", item.Recommendation);

        var devices = new List<PlatformDevice>
        {
            new("AMD PPM Provisioning File Driver", "Advanced Micro Devices, Inc."),
            new("AMD PSP 11.0 Device", "Advanced Micro Devices, Inc."),
            new("AMD GPIO Controller", "Microsoft"),
        };
        var generic = AmdPlatformRules.ChipsetItems(new AmdPlatformFacts("AMD Ryzen 7 7700X", "6.10.17.152", devices, null, null));
        Assert.Contains(generic, i => i.Title.Contains("generic drivers", StringComparison.Ordinal));

        var withOptimizer = devices.Take(2).Append(new PlatformDevice(AmdPlatformRules.VCacheDeviceName, "Advanced Micro Devices, Inc.")).ToList();
        var x3dNoService = AmdPlatformRules.ChipsetItems(new AmdPlatformFacts(X3d, "6.10.17.152", withOptimizer, new PlatformServiceState(true, false, true), true));
        Assert.Contains(x3dNoService, i => i.Title.Contains("V-Cache service", StringComparison.Ordinal));

        var x3dMissing = AmdPlatformRules.ChipsetItems(new AmdPlatformFacts(X3d, "6.10.17.152", devices.Take(2).ToList(), new PlatformServiceState(false, false, false), true));
        Assert.Contains(x3dMissing, i => i.Title.Contains("Performance Optimizer is missing", StringComparison.Ordinal));

        var good = AmdPlatformRules.ChipsetItems(new AmdPlatformFacts("AMD Ryzen 7 7700X", "6.10.17.152", devices.Take(2).ToList(), null, null));
        Assert.Equal(HealthStatus.Good, Assert.Single(good).Status);
    }

    [Fact]
    public void Amd_ryzen_balanced_plan_and_bios_advice()
    {
        Assert.Equal("AMD Ryzen Balanced", PowerPlanParser.DescribeKnown(PowerPlanParser.AmdRyzenBalanced));
        var findings = LatencyRules.BuildFindings(Balanced() with { PowerPlan = "AMD Ryzen Balanced", PowerPlanId = PowerPlanParser.AmdRyzenBalanced },
            cpuName: "AMD Ryzen 7 3700X 8-Core Processor");
        Assert.Contains(findings, f => f.Id == "latency.powerplan.amdryzen" && f.ActionId == "power.balanced");
        Assert.Contains("Power Supply Idle Control", LatencyRules.AmdBiosAdvice("AMD Ryzen 7 3700X 8-Core Processor"));
        Assert.DoesNotContain("Power Supply Idle Control", LatencyRules.AmdBiosAdvice("AMD Ryzen 7 7700X"));
        Assert.Null(LatencyRules.AmdBiosAdvice("AMD Ryzen 7 7840U w/ Radeon 780M Graphics"));
        Assert.Null(LatencyRules.AmdBiosAdvice("Intel(R) Core(TM) i9-14900K"));
    }
}

public class HardwareErrorRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Whea_19_on_ryzen_points_at_curve_optimizer_with_the_core()
    {
        var e = HardwareErrorRules.ParseWhea(19, Now.AddDays(-2), new Dictionary<string, string?> { ["ApicId"] = "6", ["ErrorType"] = "Cache Hierarchy" });
        Assert.Equal(6, e.ApicId);
        Assert.Equal(WheaKind.CpuCorrected, HardwareErrorRules.Classify(e));
        var item = HardwareErrorRules.WheaItem([e], 0, "AMD Ryzen 9 5950X 16-Core Processor", Now)!;
        Assert.Equal(HealthStatus.Warning, item.Status);
        Assert.Contains("core 3", item.Detail);
        Assert.Contains("Curve Optimizer", item.Recommendation);

        var bus = e with { ErrorType = "Bus/Interconnect" };
        Assert.Equal(WheaKind.CpuBus, HardwareErrorRules.Classify(bus));
        Assert.Contains("FCLK", HardwareErrorRules.WheaItem([bus], 0, "AMD Ryzen 5 3600", Now)!.Recommendation);
    }

    [Fact]
    public void Pcie_fatal_and_bugcheck_124()
    {
        var pcie = new WheaEvent(Now.AddDays(-1), 17, null, null, null, null);
        Assert.Contains("Gen4", HardwareErrorRules.WheaItem([pcie], 0, null, Now)!.Recommendation);
        Assert.Equal(HealthStatus.Critical, HardwareErrorRules.WheaItem([], 1, null, Now)!.Status);
        Assert.Equal(HealthStatus.Critical, HardwareErrorRules.WheaItem([pcie with { EventId = 18 }], 0, null, Now)!.Status);
        Assert.Null(HardwareErrorRules.WheaItem([pcie with { Time = Now.AddDays(-45) }], 0, null, Now));
        Assert.Equal(0x124, HardwareErrorRules.ParseBugCheckCode("0x00000124"));
        Assert.Equal(0x124, HardwareErrorRules.ParseBugCheckCode("292"));
        Assert.Null(HardwareErrorRules.ParseBugCheckCode("abc"));
    }

    [Fact]
    public void Graphics_driver_timeouts_are_attributed_to_the_vendor()
    {
        Assert.Equal("AMD", HardwareErrorRules.DriverVendor(["amdkmdag", null]));
        Assert.Equal("NVIDIA", HardwareErrorRules.DriverVendor(["nvlddmkm"]));
        Assert.Equal("Intel", HardwareErrorRules.DriverVendor(["igfx"]));
        Assert.Null(HardwareErrorRules.DriverVendor(["something"]));
        Assert.Equal(0x141, HardwareErrorRules.LiveKernelCode(["0", "0", "LiveKernelEvent", "Not available", "0", "141", "0"]));
        Assert.Null(HardwareErrorRules.LiveKernelCode(["0", "0", "APPCRASH", "Not available", "0", "141"]));

        var item = HardwareErrorRules.DriverTimeoutItem(
            [new GpuDriverTimeout(Now.AddDays(-1), "AMD", "Display 4101"), new GpuDriverTimeout(Now.AddDays(-3), "AMD", "Display 4101")], Now)!;
        Assert.Contains("2 times", item.Title);
        Assert.Contains("factory-reset", item.Recommendation);
        Assert.Null(HardwareErrorRules.DriverTimeoutItem([], Now));
    }

    [Fact]
    public void Health_thermal_uses_ryzen_limits_and_ignores_the_hot_spot()
    {
        var readings = new List<SensorReading>
        {
            GpuSelectionTests.R("c", "AMD Ryzen 9 9950X 16-Core Processor", HardwareKind.Cpu, SensorKind.Temperature, "Core (Tctl/Tdie)", 95),
            GpuSelectionTests.R("g", "AMD Radeon RX 7900 XTX", HardwareKind.Gpu, SensorKind.Temperature, "GPU Core", 70),
            GpuSelectionTests.R("h", "AMD Radeon RX 7900 XTX", HardwareKind.Gpu, SensorKind.Temperature, "GPU Hot Spot", 98),
        };
        var items = HealthItemRules.Thermal(readings, new MonitoringSettings());
        Assert.DoesNotContain(items, i => i.Status == HealthStatus.Critical);
        Assert.DoesNotContain(items, i => i.Title.Contains("graphics card", StringComparison.Ordinal));
    }
}
