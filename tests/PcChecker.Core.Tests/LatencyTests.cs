using PcChecker.Core.Common;
using PcChecker.Core.Latency;
using PcChecker.Core.Optimization;
using Xunit;

namespace PcChecker.Core.Tests;

public class LatencyRulesTests
{
    private static LatencyReport Healthy() => LatencyReport.Empty() with
    {
        DpcTimePercent = 0.4,
        InterruptTimePercent = 0.2,
        TimerResolutionMs = 0.5,
        HpetForced = false,
        HagsEnabled = true,
        PowerPlan = "High performance",
        CoreParkingAllowed = false,
        UsbSelectiveSuspend = false,
        PcieAspmEnabled = false,
        GameMode = true,
        PointerPrecision = false,
        MousePollingHz = 1000,
        DnsMs = 12,
        Network = [new("1.1.1.1", "Cloudflare DNS", 9.5, 8, 11, 0.8, 0, 10)],
    };

    [Fact]
    public void Healthy_machine_has_no_findings_and_full_score()
    {
        var findings = LatencyRules.BuildFindings(Healthy(), hasNvidiaGpu: true, nicPowerSaving: false);
        Assert.Empty(findings);
        Assert.Equal(100, LatencyRules.Score(findings));
        Assert.Equal("Excellent", LatencyRules.Grade(100));
        Assert.Equal(HealthStatus.Good, LatencyRules.Status(100));
    }

    [Theory]
    [InlineData(3.5, "latency.dpc.high", Severity.High)]
    [InlineData(2.9, null, Severity.Info)]
    public void Dpc_time_above_three_percent_is_high(double dpc, string? expectedId, Severity severity)
    {
        var findings = LatencyRules.BuildFindings(Healthy() with { DpcTimePercent = dpc });
        if (expectedId is null)
        {
            Assert.DoesNotContain(findings, f => f.Id == "latency.dpc.high");
        }
        else
        {
            var f = Assert.Single(findings, f => f.Id == expectedId);
            Assert.Equal(severity, f.Severity);
            Assert.Null(f.ActionId);
        }
    }

    [Fact]
    public void Interrupt_time_above_one_percent_is_medium()
    {
        var f = Assert.Single(LatencyRules.BuildFindings(Healthy() with { InterruptTimePercent = 1.4 }));
        Assert.Equal("latency.isr.high", f.Id);
        Assert.Equal(Severity.Medium, f.Severity);
    }

    [Fact]
    public void Timer_hpet_and_dynamic_rules_map_to_actions()
    {
        var findings = LatencyRules.BuildFindings(Healthy() with { TimerResolutionMs = 15.6, HpetForced = true });
        Assert.Contains(findings, f => f.Id == "latency.timer.coarse" && f.ActionId == LatencyActionIds.TimerResolutionHint && f.Severity == Severity.Info);
        var hpet = Assert.Single(findings, f => f.Id == "latency.hpet.forced");
        Assert.Equal(LatencyActionIds.HpetOff, hpet.ActionId);
        Assert.True(hpet.AdvancedOnly);
        Assert.Equal(RiskLevel.Advanced, hpet.Risk);
    }

    [Fact]
    public void Hags_off_is_only_reported_on_nvidia()
    {
        var facts = Healthy() with { HagsEnabled = false };
        Assert.Contains(LatencyRules.BuildFindings(facts, hasNvidiaGpu: true), f => f.ActionId == LatencyActionIds.HagsOn);
        Assert.DoesNotContain(LatencyRules.BuildFindings(facts, hasNvidiaGpu: false), f => f.ActionId == LatencyActionIds.HagsOn);
    }

    [Fact]
    public void Power_and_input_settings_produce_fix_actions()
    {
        var facts = Healthy() with
        {
            PowerPlan = "Balanced",
            CoreParkingAllowed = true,
            UsbSelectiveSuspend = true,
            PcieAspmEnabled = true,
            GameMode = false,
            PointerPrecision = true,
            MousePollingHz = 125,
        };
        var findings = LatencyRules.BuildFindings(facts, nicPowerSaving: true);
        var byAction = findings.Where(f => f.ActionId is not null).ToDictionary(f => f.ActionId!);
        Assert.Contains("power.highperformance", byAction.Keys);
        Assert.Contains(LatencyActionIds.CoreParkingOff, byAction.Keys);
        Assert.Contains(LatencyActionIds.UsbSelectiveSuspendOff, byAction.Keys);
        Assert.Contains(LatencyActionIds.PcieAspmOff, byAction.Keys);
        Assert.Contains(LatencyActionIds.GameModeOn, byAction.Keys);
        Assert.Contains(LatencyActionIds.PointerPrecisionOff, byAction.Keys);
        Assert.Contains(LatencyActionIds.NicPowerSavingOff, byAction.Keys);
        Assert.Contains(findings, f => f.Id == "latency.polling.low");
        Assert.All(findings, f => Assert.False(f.AdvancedOnly));
    }

    [Fact]
    public void Network_loss_jitter_and_no_reply_are_flagged()
    {
        var facts = Healthy() with
        {
            Network =
            [
                new("gateway", "Your router", 1, 0.5, 2, 0.3, 0, 10),
                new("8.8.8.8", "Google DNS", 20, 10, 80, 14, 10, 10),
                new("9.9.9.9", "Quad9 DNS", null, null, null, null, 100, 10),
            ],
            DnsMs = 250,
        };
        var findings = LatencyRules.BuildFindings(facts);
        Assert.DoesNotContain(findings, f => f.Id == "latency.network.gateway");
        Assert.Contains(findings, f => f.Id == "latency.network.8.8.8.8" && f.Severity == Severity.High);
        Assert.Contains(findings, f => f.Id == "latency.network.9.9.9.9" && f.Severity == Severity.Medium);
        Assert.Contains(findings, f => f.Id == "latency.dns.slow" && f.ActionId == "dns.cloudflare");
    }

    [Fact]
    public void Score_subtracts_weights_and_never_goes_below_zero()
    {
        LatencyFinding Make(Severity s) => new("x", s, "t", "d", null, RiskLevel.Safe, false);
        Assert.Equal(80, LatencyRules.Score([Make(Severity.High)]));
        Assert.Equal(70, LatencyRules.Score([Make(Severity.Critical)]));
        Assert.Equal(100 - 12 - 6 - 2, LatencyRules.Score([Make(Severity.Medium), Make(Severity.Low), Make(Severity.Info)]));
        Assert.Equal(0, LatencyRules.Score(Enumerable.Repeat(Make(Severity.Critical), 5)));
        Assert.Equal("Needs attention", LatencyRules.Grade(40));
        Assert.Equal("Fair", LatencyRules.Grade(60));
        Assert.Equal("Good", LatencyRules.Grade(80));
    }

    [Fact]
    public void Jitter_timer_and_sample_helpers()
    {
        Assert.Null(LatencyRules.Jitter([5]));
        Assert.Equal(2, LatencyRules.Jitter([10, 12, 10, 12]));
        Assert.Equal(0, LatencyRules.Jitter([7, 7, 7]));
        Assert.Equal(0.5, LatencyRules.HundredNanosecondsToMs(5000));
        Assert.Equal(15.625, LatencyRules.HundredNanosecondsToMs(156250));
        Assert.Equal(2, LatencyRules.AverageSamples([0f, 1f, 3f]));
        Assert.Equal(1.5, LatencyRules.AverageSamples([0f, float.NaN, 3f], skipFirst: false));
        Assert.Null(LatencyRules.AverageSamples([]));
        Assert.True(LatencyRules.HagsFromRegistry(2));
        Assert.False(LatencyRules.HagsFromRegistry(1));
        Assert.Null(LatencyRules.HagsFromRegistry(null));
    }
}

public class LatencyParserTests
{
    private const string BcdSample = """
        Windows Boot Loader
        -------------------
        identifier              {current}
        device                  partition=C:
        path                    \WINDOWS\system32\winload.efi
        description             Windows 11
        useplatformclock        Yes
        disabledynamictick      No
        tscsyncpolicy           Enhanced
        nx                      OptIn
        bootmenupolicy          Standard
        timeout                 30
        """;

    [Fact]
    public void BcdEdit_parser_reads_keys_and_flags()
    {
        var map = BcdEditParser.Parse(BcdSample);
        Assert.Equal("{current}", map["identifier"]);
        Assert.Equal("Windows 11", map["description"]);
        Assert.True(BcdEditParser.GetBool(map, "useplatformclock"));
        Assert.False(BcdEditParser.GetBool(map, "DisableDynamicTick"));
        Assert.Null(BcdEditParser.GetBool(map, "missing"));
        Assert.Equal(30, BcdEditParser.GetInt(map, "timeout"));
        Assert.Empty(BcdEditParser.Parse(null));
    }

    [Fact]
    public void PowerCfg_query_parser_reads_ac_and_dc_indexes_in_hex_and_decimal()
    {
        const string sample = """
            Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
              Subgroup GUID: 2a737441-1930-4402-8d77-b2bebba308a3  (USB settings)
                Power Setting GUID: 48e6b7a6-50f5-4782-a5d4-53bb50ff  (USB selective suspend setting)
                  Possible Setting Index: 000
                  Possible Setting Friendly Name: Disabled
                  Possible Setting Index: 001
                  Possible Setting Friendly Name: Enabled
                Current AC Power Setting Index: 0x00000001
                Current DC Power Setting Index: 0x00000000
            """;
        var (ac, dc) = PowerCfgQueryParser.ParseIndexes(sample);
        Assert.Equal(1, ac);
        Assert.Equal(0, dc);
        Assert.Equal((100, 10), PowerCfgQueryParser.ParseIndexes("Current AC Power Setting Index: 100\r\nCurrent DC Power Setting Index: 10"));
        Assert.Equal((null, null), PowerCfgQueryParser.ParseIndexes(string.Empty));
    }

    [Fact]
    public void PowerCfg_active_scheme_parser_reads_guid_and_name()
    {
        var (id, name) = PowerCfgQueryParser.ParseActiveScheme("Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)");
        Assert.Equal(Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), id);
        Assert.Equal("High performance", name);
        Assert.Equal((null, null), PowerCfgQueryParser.ParseActiveScheme("garbage"));
    }
}

public class NicPowerRulesTests
{
    [Fact]
    public void PnpCapabilities_24_means_power_management_off()
    {
        var off = NicPowerRules.Interpret("Intel I225-V", "24", "0", "0", "1");
        Assert.False(off.PowerManagementAllowed);
        Assert.False(off.EnergyEfficientEthernet);
        Assert.False(off.IsPowerSaving);
        Assert.True(off.InterruptModeration);

        var hex = NicPowerRules.Interpret("Realtek", "0x18", null, null, null);
        Assert.False(hex.PowerManagementAllowed);
        Assert.Null(hex.EnergyEfficientEthernet);
    }

    [Fact]
    public void Missing_pnp_value_or_eee_on_counts_as_power_saving()
    {
        Assert.True(NicPowerRules.Interpret("Realtek", null, "0", "0", null).IsPowerSaving);
        Assert.True(NicPowerRules.Interpret("Realtek", "24", "1", "0", null).IsPowerSaving);
        Assert.True(NicPowerRules.Interpret("Realtek", "24", "0", "1", null).IsPowerSaving);
        Assert.True(NicPowerRules.AnyPowerSaving([NicPowerRules.Interpret("a", "24", "0", "0", null), NicPowerRules.Interpret("b", "0", "0", "0", null)]));
        Assert.False(NicPowerRules.AnyPowerSaving([]));
    }

    [Theory]
    [InlineData("Intel(R) Ethernet Controller I225-V", false)]
    [InlineData("Hyper-V Virtual Ethernet Adapter", true)]
    [InlineData("WAN Miniport (IP)", true)]
    [InlineData("Bluetooth Device (Personal Area Network)", true)]
    [InlineData("TAP-Windows Adapter V9", true)]
    [InlineData(null, true)]
    public void Virtual_adapters_are_recognised(string? description, bool isVirtual) =>
        Assert.Equal(isVirtual, NicPowerRules.IsVirtualAdapter(description));

    [Fact]
    public void Flags_parse_text_and_numbers()
    {
        Assert.True(NicPowerRules.ParseFlag("1"));
        Assert.False(NicPowerRules.ParseFlag("Disabled"));
        Assert.Null(NicPowerRules.ParseFlag(""));
        Assert.True(NicPowerRules.ParseFlag("3"));
    }
}

public class NetworkLatencyMathTests
{
    [Fact]
    public void Summarize_computes_statistics_and_loss()
    {
        var r = NetworkLatencyMath.Summarize("8.8.8.8", "Google DNS", [10, 12, 14, 12], 1);
        Assert.Equal(12, r.AvgMs);
        Assert.Equal(10, r.MinMs);
        Assert.Equal(14, r.MaxMs);
        Assert.Equal(2, r.JitterMs);
        Assert.Equal(20, r.LossPercent);
        Assert.Equal(5, r.Sent);
    }

    [Fact]
    public void Summarize_with_no_replies_reports_full_loss()
    {
        var r = NetworkLatencyMath.Summarize("gateway", "Your router", [], 10);
        Assert.Null(r.AvgMs);
        Assert.Equal(100, r.LossPercent);
        Assert.Equal(10, r.Sent);
        Assert.Equal(HealthStatus.Critical, NetworkLatencyMath.Grade(r));
        Assert.Equal("No reply", NetworkLatencyMath.Describe(r));
    }

    [Fact]
    public void Grade_and_describe_follow_thresholds()
    {
        Assert.Equal(HealthStatus.Good, NetworkLatencyMath.Grade(new("1.1.1.1", "x", 9, 8, 10, 0.5, 0, 10)));
        Assert.Equal(HealthStatus.Warning, NetworkLatencyMath.Grade(new("1.1.1.1", "x", 90, 80, 100, 1, 0, 10)));
        Assert.Equal("Some jitter", NetworkLatencyMath.Describe(new("1.1.1.1", "x", 20, 10, 30, 7, 0, 10)));
        Assert.Equal("Packet loss", NetworkLatencyMath.Describe(new("1.1.1.1", "x", 20, 10, 30, 1, 10, 10)));
        Assert.Equal("Unstable", NetworkLatencyMath.Describe(new("1.1.1.1", "x", 20, 10, 60, 25, 0, 10)));
        Assert.Equal("Your router", NetworkLatencyProbe.LabelFor("gateway"));
        Assert.Equal("Cloudflare DNS", NetworkLatencyProbe.LabelFor("1.1.1.1"));
        Assert.Equal("Quad9 DNS", NetworkLatencyProbe.LabelFor("9.9.9.9"));
        Assert.Equal("example.com", NetworkLatencyProbe.LabelFor("example.com"));
    }
}

public class LatencyActionCatalogTests
{
    [Fact]
    public void Catalog_covers_every_action_id_exactly_once()
    {
        Assert.Equal(LatencyActionIds.All.Count, LatencyActionCatalog.All.Count);
        Assert.Equal(LatencyActionIds.All.OrderBy(x => x), LatencyActionCatalog.All.Select(a => a.Id).OrderBy(x => x));
        Assert.All(LatencyActionIds.All, id => Assert.True(LatencyActionCatalog.Handles(id)));
        Assert.False(LatencyActionCatalog.Handles("power.highperformance"));
        Assert.All(LatencyActionCatalog.All, a => Assert.Equal(LatencyActionCatalog.Category, a.Category));
    }

    [Fact]
    public void Advanced_actions_are_marked_advanced_only_and_restart_flags_are_sane()
    {
        string[] advanced = [LatencyActionIds.HpetOff, LatencyActionIds.DynamicTickOff, LatencyActionIds.NagleOff, LatencyActionIds.MsiGpuOn];
        foreach (var a in LatencyActionCatalog.All)
        {
            Assert.Equal(advanced.Contains(a.Id), a.AdvancedOnly);
            Assert.Equal(advanced.Contains(a.Id) ? RiskLevel.Advanced : RiskLevel.Safe, a.Risk);
        }

        Assert.True(LatencyActionCatalog.Find(LatencyActionIds.HagsOn)!.RequiresRestart);
        Assert.False(LatencyActionCatalog.Find(LatencyActionIds.PointerPrecisionOff)!.RequiresRestart);
        Assert.False(LatencyActionCatalog.Find(LatencyActionIds.TimerResolutionHint)!.Reversible);
        Assert.Null(LatencyActionCatalog.Find("nope"));
    }

    [Fact]
    public async Task Demo_provider_handles_every_id_and_remembers_state()
    {
        var provider = new DemoLatencyActionsProvider();
        Assert.All(LatencyActionIds.All, id => Assert.True(provider.Handles(id)));
        Assert.Equal(LatencyActionIds.All.Count, provider.Actions.Count);

        Assert.False((await provider.GetStateAsync(LatencyActionIds.PcieAspmOff, CancellationToken.None)).IsApplied);
        var apply = await provider.ApplyAsync(LatencyActionIds.PcieAspmOff, CancellationToken.None);
        Assert.True(apply.Success);
        Assert.True((await provider.GetStateAsync(LatencyActionIds.PcieAspmOff, CancellationToken.None)).IsApplied);
        Assert.True((await provider.RevertAsync(LatencyActionIds.PcieAspmOff, CancellationToken.None)).Success);
        Assert.False(provider.IsApplied(LatencyActionIds.PcieAspmOff));

        var hint = await provider.ApplyAsync(LatencyActionIds.TimerResolutionHint, CancellationToken.None);
        Assert.True(hint.Success);
        Assert.Contains("Nothing was changed", hint.Message);
        Assert.False((await provider.ApplyAsync("unknown.action", CancellationToken.None)).Success);

        var hags = await provider.ApplyAsync(LatencyActionIds.HagsOn, CancellationToken.None);
        Assert.True(hags.RebootRequired);
        Assert.True(provider.IsApplied(LatencyActionIds.HagsOn));
        Assert.False(provider.IsApplied(LatencyActionIds.HagsOff));
    }

    [Fact]
    public async Task Demo_service_reflects_fixes_applied_through_the_demo_provider()
    {
        var provider = new DemoLatencyActionsProvider();
        var service = new DemoLatencyService([provider]);
        var before = await service.AnalyzeAsync(1, false, null, CancellationToken.None);
        Assert.Contains(before.Findings, f => f.ActionId == LatencyActionIds.HagsOn);
        Assert.Contains(before.Findings, f => f.ActionId == LatencyActionIds.PointerPrecisionOff);
        Assert.Empty(before.Network);

        await provider.ApplyAsync(LatencyActionIds.HagsOn, CancellationToken.None);
        await provider.ApplyAsync(LatencyActionIds.PointerPrecisionOff, CancellationToken.None);
        await provider.ApplyAsync(LatencyActionIds.NicPowerSavingOff, CancellationToken.None);
        var after = await service.AnalyzeAsync(1, false, null, CancellationToken.None);
        Assert.DoesNotContain(after.Findings, f => f.ActionId == LatencyActionIds.HagsOn);
        Assert.DoesNotContain(after.Findings, f => f.ActionId == LatencyActionIds.PointerPrecisionOff);
        Assert.DoesNotContain(after.Findings, f => f.ActionId == LatencyActionIds.NicPowerSavingOff);
        Assert.True(after.Score > before.Score);
        Assert.Same(after, service.Last);
    }

    [Fact]
    public void Windows_provider_handles_the_same_ids_without_touching_the_system()
    {
        // Handles() is pure, so it can be checked on any platform.
        Assert.All(LatencyActionIds.All, id => Assert.True(LatencyActionCatalog.Handles(id.ToUpperInvariant())));
        var empty = LatencyReport.Empty("x");
        Assert.Single(empty.Warnings);
        Assert.Equal(0, empty.Score);
    }
}
