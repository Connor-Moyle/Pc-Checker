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

    [Theory]
    [InlineData("Ausbalanciert", "381b4222-f694-41f0-9685-ff5bb260df2e", true)]
    [InlineData("Économie d'énergie", "a1841308-3541-4fab-bc81-f71556f20b4a", true)]
    [InlineData("Höchstleistung", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", false)]
    public void Translated_power_plans_are_judged_by_their_guid(string name, string id, bool flagged)
    {
        var findings = LatencyRules.BuildFindings(Healthy() with { PowerPlan = name, PowerPlanId = Guid.Parse(id) });
        Assert.Equal(flagged, findings.Any(f => f.Id == "latency.powerplan.balanced"));
        if (flagged)
        {
            Assert.Contains(findings, f => f.Title.Contains(name, StringComparison.Ordinal));
        }
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
    public void Slow_dns_finding_is_visible_but_rated_advanced_like_its_catalog_action()
    {
        var finding = Assert.Single(LatencyRules.BuildFindings(Healthy() with { DnsMs = 180 }));
        Assert.Equal("latency.dns.slow", finding.Id);
        Assert.Equal(RiskLevel.Advanced, finding.Risk);
        Assert.False(finding.AdvancedOnly);
        var catalog = OptimizationCatalog.All.Single(a => a.Id == "dns.cloudflare");
        Assert.Equal(catalog.Risk, finding.Risk);
        Assert.True(catalog.AdvancedOnly);
    }

    [Fact]
    public void Nic_finding_names_the_adapters_that_save_power()
    {
        var adapters = new List<NicPowerState>
        {
            NicPowerRules.Interpret("Intel I225-V", "24", "1", "0", "1", "1"),
            NicPowerRules.Interpret("Realtek PCIe GbE", "24", "0", "0", "1", "0"),
        };
        var finding = Assert.Single(LatencyRules.BuildFindings(Healthy(), nicPowerSaving: true, adapters: adapters));
        Assert.Equal(LatencyActionIds.NicPowerSavingOff, finding.ActionId);
        Assert.Contains("Intel I225-V: Energy-Efficient Ethernet", finding.Detail);
        Assert.DoesNotContain("Realtek", finding.Detail);
        Assert.Equal("nothing", adapters[1].PowerSavingSummary);
    }

    [Fact]
    public void Power_setting_guids_are_complete()
    {
        foreach (var guid in new[]
                 {
                     LatencyProbes.UsbSubgroup, LatencyProbes.UsbSelectiveSuspendSetting, LatencyProbes.PciSubgroup,
                     LatencyProbes.PciAspmSetting, LatencyProbes.ProcessorSubgroup, LatencyProbes.CoreParkingMinCoresSetting,
                 })
        {
            Assert.True(Guid.TryParseExact(guid, "D", out _), guid + " is not a full GUID");
        }

        Assert.Equal("48e6b7a6-50f5-4782-a5d4-53bb50ff42bd", LatencyProbes.UsbSelectiveSuspendSetting);
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

    [Theory]
    [InlineData("Ja", true)]
    [InlineData("Nein", false)]
    [InlineData("Oui", true)]
    [InlineData("Non", false)]
    [InlineData("Sí", true)]
    [InlineData("はい", true)]
    [InlineData("いいえ", false)]
    [InlineData("Tak", true)]
    [InlineData("Nie", false)]
    [InlineData("YES", true)]
    [InlineData("Wahrscheinlich", null)]
    public void BcdEdit_booleans_accept_localized_yes_and_no(string word, bool? expected)
    {
        var map = BcdEditParser.Parse("Windows Boot Loader\n-------------------\nuseplatformclock        " + word + "\n");
        Assert.Equal(expected, BcdEditParser.GetBool(map, "useplatformclock"));
        Assert.Equal(expected, BcdEditParser.GetFlag(map, "useplatformclock"));
    }

    [Fact]
    public void BcdEdit_flags_are_false_when_absent_and_unknown_when_unreadable()
    {
        var map = BcdEditParser.Parse(BcdSample);
        Assert.False(BcdEditParser.GetFlag(map, "missing"));
        Assert.True(BcdEditParser.GetFlag(map, "useplatformclock"));
        Assert.False(BcdEditParser.GetFlag(map, "disabledynamictick"));
        var odd = BcdEditParser.Parse("disabledynamictick      Igen-ish\n");
        Assert.Null(BcdEditParser.GetFlag(odd, "disabledynamictick"));
        Assert.Null(BcdEditParser.GetBool(odd, "disabledynamictick"));
    }

    [Fact]
    public void PowerCfg_query_parser_reads_ac_and_dc_indexes_in_hex_and_decimal()
    {
        const string sample = """
            Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
              Subgroup GUID: 2a737441-1930-4402-8d77-b2bebba308a3  (USB settings)
                Power Setting GUID: 48e6b7a6-50f5-4782-a5d4-53bb50ff42bd  (USB selective suspend setting)
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

    [Fact]
    public void PowerCfg_query_parser_reads_localized_output_by_shape()
    {
        const string german = """
            Energieschema-GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Ausbalanciert)
              Untergruppen-GUID: 2a737441-1930-4402-8d77-b2bebba308a3  (USB-Einstellungen)
                Energieeinstellungs-GUID: 48e6b7a6-50f5-4782-a5d4-53bb50ff42bd  (Einstellung für selektives USB-Energiesparen)
                  Mögliche Einstellung Index: 000
                  Mögliche Einstellung Anzeigename: Deaktiviert
                  Mögliche Einstellung Index: 001
                  Mögliche Einstellung Anzeigename: Aktiviert
                Aktueller Index der Netzstromeinstellung: 0x00000001
                Aktueller Index der Akkustromeinstellung: 0x00000000
            """;
        Assert.Equal((1, 0), PowerCfgQueryParser.ParseIndexes(german));

        const string french = """
            GUID du mode de gestion de l'alimentation : 381b4222-f694-41f0-9685-ff5bb260df2e  (Utilisation normale)
              GUID du sous-groupe : 54533251-82be-4824-96c1-47b60b740d00  (Gestion de l'alimentation du processeur)
                GUID du paramètre d'alimentation : 0cc5b647-c1df-4637-891a-dec35c318583  (Nombre minimal de cœurs)
                  Index de paramètre possible minimal : 00000000
                  Index de paramètre possible maximal : 00000064
                Index du paramètre d'alimentation sur secteur actuel : 0x00000064
                Index du paramètre d'alimentation sur batterie actuel : 0x0000000a
            """;
        Assert.Equal((100, 10), PowerCfgQueryParser.ParseIndexes(french));

        // Real powercfg output for a range setting prints the possible minimum, maximum and increment as 0x values
        // before the current indexes; those must not be read as AC and DC.
        const string germanRange = """
            Energieschema-GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Ausbalanciert)
              Untergruppen-GUID: 54533251-82be-4824-96c1-47b60b740d00  (Prozessorenergieverwaltung)
                Energieeinstellungs-GUID: 0cc5b647-c1df-4637-891a-dec35c318583  (Minimale Anzahl von Kernen)
                  GUID-Alias: CPMINCORES
                  Minimale mögliche Einstellung: 0x00000000
                  Maximale mögliche Einstellung: 0x00000064
                  Mögliche Einstellungsschritte: 0x00000001
                  Mögliche Einstellungseinheiten: %
                Aktueller Index der Netzstromeinstellung: 0x0000000a
                Aktueller Index der Akkustromeinstellung: 0x00000005
            """;
        Assert.Equal((10, 5), PowerCfgQueryParser.ParseIndexes(germanRange));

        // Only one hex value: not enough to tell AC from DC, so nothing is guessed.
        Assert.Equal((null, null), PowerCfgQueryParser.ParseIndexes("Irgendwas: 0x00000001"));
    }

    [Theory]
    [InlineData("GUID du mode de gestion de l'alimentation : 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Performances élevées)", "Performances élevées")]
    [InlineData("Energieschema-GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Ausbalanciert)", "Ausbalanciert")]
    public void PowerCfg_active_scheme_parser_is_locale_independent(string output, string name)
    {
        var (id, parsedName) = PowerCfgQueryParser.ParseActiveScheme(output);
        Assert.NotNull(id);
        Assert.Equal(name, parsedName);
    }
}

public class LatencyCaptureStoreTests
{
    [Fact]
    public void Captures_round_trip_through_json_and_tolerate_garbage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PcCheckerTests", "latency-" + Guid.NewGuid().ToString("N"));
        var path = LatencyCaptureStore.DefaultPath(dir);
        try
        {
            Assert.Empty(LatencyCaptureStore.TryRead(path));
            var captures = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase)
            {
                [LatencyActionIds.HpetOff] = new Dictionary<string, string?> { ["useplatformclock"] = "true" },
                [LatencyActionIds.MsiGpuOn] = new Dictionary<string, string?> { [@"PCI\VEN_10DE&DEV_2684\4&abc|MSISupported"] = null },
            };
            LatencyCaptureStore.Write(path, captures);
            var read = LatencyCaptureStore.TryRead(path);
            Assert.Equal(2, read.Count);
            Assert.Equal("true", read[LatencyActionIds.HpetOff]["useplatformclock"]);
            Assert.True(read[LatencyActionIds.MsiGpuOn].ContainsKey(@"PCI\VEN_10DE&DEV_2684\4&abc|MSISupported"));
            Assert.Null(read[LatencyActionIds.MsiGpuOn][@"PCI\VEN_10DE&DEV_2684\4&abc|MSISupported"]);

            File.WriteAllText(path, "{ not json");
            Assert.Empty(LatencyCaptureStore.TryRead(path));
            Assert.Empty(LatencyCaptureStore.Deserialize(string.Empty));
            Assert.Empty(LatencyCaptureStore.Deserialize("[1,2,3]"));
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

public class LatencyPowerSchemeTests
{
    [Theory]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e", "381b4222-f694-41f0-9685-ff5bb260df2e")]
    [InlineData("{8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C}", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")]
    [InlineData(null, "SCHEME_CURRENT")]
    [InlineData("", "SCHEME_CURRENT")]
    [InlineData("00000000-0000-0000-0000-000000000000", "SCHEME_CURRENT")]
    [InlineData("SCHEME_MIN & del x", "SCHEME_CURRENT")]
    public void Power_reverts_target_the_captured_plan(string? captured, string expected)
    {
        Assert.Equal(expected, LatencyProbes.SchemeArgument(captured));
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

    [Fact]
    public void Flow_control_is_read_but_does_not_count_as_power_saving()
    {
        var state = NicPowerRules.Interpret("Intel I225-V", "24", "0", "0", "1", "3");
        Assert.True(state.FlowControl);
        Assert.False(state.IsPowerSaving);
        Assert.Null(NicPowerRules.Interpret("Wi-Fi 6E", "24", null, null, null).FlowControl);
    }

    [Theory]
    [InlineData(null, 0x08)]
    [InlineData(0, 0x08)]
    [InlineData(0x08, 0x08)]
    [InlineData(0x18, 0x18)]
    [InlineData(0x20, 0x28)]
    public void Turning_power_saving_off_keeps_the_wake_bits(int? current, int expected)
    {
        int caps = NicPowerRules.PowerOffCapabilities(current);
        Assert.Equal(expected, caps);
        Assert.Equal(current is { } c && (c & NicPowerRules.PnpDisableWake) != 0, (caps & NicPowerRules.PnpDisableWake) != 0);
        Assert.False(NicPowerRules.Interpret("Intel I225-V", caps.ToString(), null, null, null).PowerManagementAllowed);
    }

    [Fact]
    public void Tcp_interface_counts_as_active_only_with_a_real_address()
    {
        Assert.False(NicPowerRules.InterfaceHoldsAddress(null, null));
        Assert.False(NicPowerRules.InterfaceHoldsAddress("0.0.0.0", ["0.0.0.0"]));
        Assert.False(NicPowerRules.InterfaceHoldsAddress(string.Empty, []));
        Assert.True(NicPowerRules.InterfaceHoldsAddress("192.168.1.20", null));
        Assert.True(NicPowerRules.InterfaceHoldsAddress("0.0.0.0", ["0.0.0.0", "10.0.0.5"]));
        Assert.False(NicPowerRules.InterfaceHoldsAddress(null, ["0.0.0.0"]));
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
