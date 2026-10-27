using PcChecker.Core.Common;
using PcChecker.Core.Optimization;
using PcChecker.Core.Performance;
using PcChecker.Core.Power;
using Xunit;

namespace PcChecker.Core.Tests;

public class SlowdownRulesTests
{
    private static PerformanceFacts Healthy() => new()
    {
        MemoryUsedPercent = 40,
        MemoryTotalBytes = 32L * 1024 * 1024 * 1024,
        ActivePowerPlan = "Balanced",
        Drives = [("C:", 200L * 1024 * 1024 * 1024, 500L * 1024 * 1024 * 1024, "SSD")],
        LastBootSeconds = 20,
        SystemDriveIsSsd = true,
        TrimEnabled = true,
    };

    [Fact]
    public void Healthy_machine_has_no_findings_and_full_score()
    {
        var findings = SlowdownRules.Evaluate(Healthy());
        Assert.Empty(findings);
        Assert.Equal(100, SlowdownRules.Score(findings));
    }

    [Fact]
    public void Many_startup_items_are_flagged()
    {
        var items = Enumerable.Range(0, 16).Select(i => new StartupItem($"App{i}", "cmd", "HKCU", true, null, "Registry")).ToList();
        var findings = SlowdownRules.Evaluate(Healthy() with { StartupItems = items });
        var f = Assert.Single(findings, f => f.Id == "startup.many");
        Assert.Equal(Severity.High, f.Severity);
        Assert.Equal(FindingCategory.Startup, f.Category);
    }

    [Fact]
    public void Memory_pressure_escalates()
    {
        Assert.Contains(SlowdownRules.Evaluate(Healthy() with { MemoryUsedPercent = 88 }), f => f.Id == "memory.high");
        var critical = SlowdownRules.Evaluate(Healthy() with { MemoryUsedPercent = 97 });
        Assert.Contains(critical, f => f.Id == "memory.critical" && f.Severity == Severity.Critical);
        Assert.DoesNotContain(critical, f => f.Id == "memory.high");
    }

    [Fact]
    public void Low_disk_space_and_hdd_system_drive()
    {
        var facts = Healthy() with
        {
            Drives = [("C:", 10L * 1024 * 1024 * 1024, 500L * 1024 * 1024 * 1024, "HDD"), ("D:", 1L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024, "SSD")],
        };
        var findings = SlowdownRules.Evaluate(facts);
        Assert.Contains(findings, f => f.Id == "disk.full.D:" && f.Severity == Severity.Critical);
        Assert.DoesNotContain(findings, f => f.Id.StartsWith("disk.low.C"));
        Assert.Contains(findings, f => f.Id == "disk.hdd.system");
        Assert.Contains(findings, f => f.ActionIds.Contains("tempfiles.clean"));
    }

    [Fact]
    public void Power_saver_while_plugged_in_is_flagged_but_not_on_battery()
    {
        Assert.Contains(SlowdownRules.Evaluate(Healthy() with { ActivePowerPlan = "Power saver" }), f => f.Id == "power.saver");
        Assert.DoesNotContain(SlowdownRules.Evaluate(Healthy() with { ActivePowerPlan = "Power saver", OnBattery = true }), f => f.Id == "power.saver");
    }

    [Fact]
    public void Translated_power_plan_names_are_recognised_by_their_guid()
    {
        var saver = Healthy() with { ActivePowerPlan = "Économie d'énergie", ActivePowerPlanId = PowerPlanParser.PowerSaver };
        Assert.Contains(SlowdownRules.Evaluate(saver), f => f.Id == "power.saver");
        Assert.DoesNotContain(SlowdownRules.Evaluate(saver with { OnBattery = true }), f => f.Id == "power.saver");

        var balanced = Healthy() with { ActivePowerPlan = "Ausbalanciert", ActivePowerPlanId = PowerPlanParser.Balanced, HasBattery = false };
        Assert.Contains(SlowdownRules.Evaluate(balanced), f => f.Id == "power.balanced.desktop");

        var high = Healthy() with { ActivePowerPlan = "Höchstleistung", ActivePowerPlanId = PowerPlanParser.HighPerformance, HasBattery = false };
        Assert.DoesNotContain(SlowdownRules.Evaluate(high), f => f.Id is "power.saver" or "power.balanced.desktop");
    }

    [Fact]
    public void Thermal_and_boot_findings()
    {
        var findings = SlowdownRules.Evaluate(Healthy() with { MaxCpuTempC = 95, MaxGpuTempC = 90, LastBootSeconds = 130 });
        Assert.Contains(findings, f => f.Id == "thermal.cpu");
        Assert.Contains(findings, f => f.Id == "thermal.gpu");
        Assert.Contains(findings, f => f.Id == "boot.veryslow");
    }

    [Fact]
    public void Findings_are_sorted_by_severity()
    {
        var findings = SlowdownRules.Evaluate(Healthy() with { MemoryUsedPercent = 97, StorageSenseEnabled = false, TrimEnabled = false });
        Assert.Equal(Severity.Critical, findings[0].Severity);
        Assert.Equal(Severity.Info, findings[^1].Severity);
    }

    [Fact]
    public void Score_subtracts_by_severity_and_clamps()
    {
        var findings = new List<SlowdownFinding>
        {
            new("a", FindingCategory.Memory, Severity.Critical, "", "", "", [], false),
            new("b", FindingCategory.Memory, Severity.Low, "", "", "", [], false),
        };
        Assert.Equal(72, SlowdownRules.Score(findings));
        Assert.Equal(0, SlowdownRules.Score(Enumerable.Repeat(findings[0], 10)));
    }

    [Fact]
    public void Action_ids_reference_catalog_entries()
    {
        var facts = Healthy() with
        {
            MemoryUsedPercent = 97, StorageSenseEnabled = false, TrimEnabled = false, ActivePowerPlan = "Power saver",
            MemoryTotalBytes = 4L * 1024 * 1024 * 1024, VisualEffectsBestAppearance = true, BackgroundAppsEnabled = true,
        };
        var known = new HashSet<string>(OptimizationCatalog.All.Select(a => a.Id)) { "startup.review", "processes.review", "services.review" };
        foreach (var f in SlowdownRules.Evaluate(facts))
        {
            Assert.All(f.ActionIds, id => Assert.Contains(id, known));
        }
    }
}

public class OptimizationCatalogTests
{
    [Fact]
    public void Ids_are_unique_and_lowercase()
    {
        var ids = OptimizationCatalog.All.Select(a => a.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ids, id => Assert.Equal(id.ToLowerInvariant(), id));
    }

    [Fact]
    public void Advanced_actions_are_marked_advanced_only()
    {
        Assert.All(OptimizationCatalog.All.Where(a => a.Risk == RiskLevel.Advanced), a => Assert.True(a.AdvancedOnly));
        Assert.All(OptimizationCatalog.All.Where(a => a.AdvancedOnly), a => Assert.Equal(RiskLevel.Advanced, a.Risk));
        Assert.DoesNotContain(OptimizationCatalog.ForMode(false), a => a.AdvancedOnly);
        Assert.Contains(OptimizationCatalog.ForMode(true), a => a.AdvancedOnly);
    }

    [Fact]
    public void Expected_ids_exist()
    {
        foreach (var id in new[] { "power.highperformance", "power.balanced", "power.ultimate", "visuals.performance", "faststartup.disable",
                     "hibernation.disable", "gamemode.enable", "storagesense.enable", "trim.enable", "dism.restorehealth", "sfc.scannow", "network.reset", "tempfiles.clean" })
        {
            Assert.NotNull(OptimizationCatalog.Find(id));
        }

        Assert.Null(OptimizationCatalog.Find("nope"));
    }

    [Fact]
    public void Every_action_has_text()
    {
        Assert.All(OptimizationCatalog.All, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Title));
            Assert.False(string.IsNullOrWhiteSpace(a.Description));
            Assert.False(string.IsNullOrWhiteSpace(a.Category));
        });
    }
}
