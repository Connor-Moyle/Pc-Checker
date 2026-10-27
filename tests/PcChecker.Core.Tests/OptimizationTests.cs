using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Optimization;
using PcChecker.Core.Performance;
using PcChecker.Core.Restore;
using PcChecker.Core.Startup;
using Xunit;

namespace PcChecker.Core.Tests;

public class WindowsOptimizationServiceTests
{
    private static WindowsOptimizationService Create(IOptimizationActionProvider? provider = null, PassThroughGuard? guard = null) =>
        new(guard ?? new PassThroughGuard(), new DemoStartupService(), provider is null ? [] : [provider], NullLogger<WindowsOptimizationService>.Instance);

    [Fact]
    public void Every_catalog_action_has_an_implementation()
    {
        var catalog = OptimizationCatalog.All.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supported = WindowsOptimizationService.SupportedActionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Empty(catalog.Except(supported));
        Assert.Empty(supported.Except(catalog));

        var service = Create();
        Assert.Equal(supported, service.HandlerIds.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(OptimizationCatalog.All.Count, service.Actions.Count);
    }

    [Fact]
    public async Task Unknown_actions_and_non_reversible_reverts_fail_cleanly()
    {
        var service = Create();
        var unknown = await service.ApplyAsync("nope.nothing", CancellationToken.None);
        Assert.False(unknown.Success);
        var state = await service.GetStateAsync("nope.nothing", CancellationToken.None);
        Assert.Null(state.IsApplied);
        var revert = await service.RevertAsync("dism.restorehealth", CancellationToken.None);
        Assert.False(revert.Success);
        Assert.Contains("cannot be undone", revert.Message);
    }

    [Fact]
    public async Task Off_windows_everything_degrades_without_throwing()
    {
        if (PlatformGuard.IsWindows)
        {
            return;
        }

        var guard = new PassThroughGuard();
        var service = Create(guard: guard);
        foreach (var id in OptimizationCatalog.All.Select(a => a.Id))
        {
            var state = await service.GetStateAsync(id, CancellationToken.None);
            Assert.Equal(id, state.ActionId);
            var apply = await service.ApplyAsync(id, CancellationToken.None);
            Assert.False(apply.Success);
        }

        Assert.Equal(0, guard.Calls);
        // Start modes are delegated to the Startup module (the demo one here), which guards and journals them itself.
        var mode = await service.SetServiceStartModeAsync("Bonjour Service", "Manual", CancellationToken.None);
        Assert.True(mode.Success);
        Assert.Equal(0, guard.Calls);
    }

    [Fact]
    public async Task Service_start_mode_validates_input_and_protects_core_services()
    {
        var guard = new PassThroughGuard();
        var service = Create(guard: guard);
        var bad = await service.SetServiceStartModeAsync("Spooler", "Sometimes", CancellationToken.None);
        Assert.False(bad.Success);
        var protectedSvc = await service.SetServiceStartModeAsync("RpcSs", "Disabled", CancellationToken.None);
        Assert.False(protectedSvc.Success);
        Assert.Contains("core Windows service", protectedSvc.Message);
        var manual = await service.SetServiceStartModeAsync("Dnscache", "Manual", CancellationToken.None);
        Assert.False(manual.Success);
        Assert.Contains("will not stop it from starting", manual.Message);
        // The firewall services are on the Startup module's list, so the Performance page refuses them too.
        Assert.False((await service.SetServiceStartModeAsync("BFE", "Disabled", CancellationToken.None)).Success);
        Assert.False((await service.SetServiceStartModeAsync("mpssvc", "Manual", CancellationToken.None)).Success);
        Assert.Equal(0, guard.Calls);
    }

    [Fact]
    public async Task Provider_actions_are_listed_and_dispatched()
    {
        var provider = new FakeProvider();
        var service = Create(provider);
        Assert.Contains(service.Actions, a => a.Id == "fake.toggle");
        Assert.True((await service.ApplyAsync("fake.toggle", CancellationToken.None)).Success);
        Assert.True(provider.Applied);
        Assert.True((await service.GetStateAsync("fake.toggle", CancellationToken.None)).IsApplied);
        Assert.True((await service.RevertAsync("fake.toggle", CancellationToken.None)).Success);
        Assert.False(provider.Applied);
    }

    [Fact]
    public async Task Startup_toggle_delegates_to_the_startup_service()
    {
        var service = Create();
        var item = new StartupItem("Steam", "steam.exe", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", true, "Valve Corporation", "Registry");
        var result = await service.SetStartupItemEnabledAsync(item, false, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains("no longer start", result.Message);
    }

    private sealed class PassThroughGuard : IChangeGuard
    {
        public int Calls { get; private set; }

        public async Task<OperationResult<T>> RunGuardedAsync<T>(string description, ChangeOptions options, Func<CancellationToken, Task<OperationResult<T>>> change,
            IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            return await change(cancellationToken);
        }

        public async Task<OperationResult> RunGuardedAsync(string description, ChangeOptions options, Func<CancellationToken, Task<OperationResult>> change,
            IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("Optimization", options.Module);
            return await change(cancellationToken);
        }
    }

    private sealed class FakeProvider : IOptimizationActionProvider
    {
        public bool Applied { get; private set; }
        public IReadOnlyList<OptimizationAction> Actions { get; } = [new("fake.toggle", "Fake toggle", "d", "Latency", RiskLevel.Safe, true, false, false)];
        public bool Handles(string actionId) => actionId == "fake.toggle";
        public Task<ActionState> GetStateAsync(string id, CancellationToken cancellationToken) => Task.FromResult(new ActionState(id, Applied, Applied ? "On" : "Off"));

        public Task<OperationResult> ApplyAsync(string id, CancellationToken cancellationToken)
        {
            Applied = true;
            return Task.FromResult(OperationResult.Ok());
        }

        public Task<OperationResult> RevertAsync(string id, CancellationToken cancellationToken)
        {
            Applied = false;
            return Task.FromResult(OperationResult.Ok());
        }
    }
}

public class PowerPlansTests
{
    [Fact]
    public void Active_scheme_line_is_parsed_with_name_and_star()
    {
        var scheme = PowerPlans.ParseScheme("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *");
        Assert.NotNull(scheme);
        Assert.Equal(PowerPlans.Balanced, scheme!.Id);
        Assert.Equal("Balanced", scheme.Name);
        Assert.True(scheme.IsActive);
        Assert.Null(PowerPlans.ParseScheme("nothing here"));
        Assert.Null(PowerPlans.ParseScheme(null));
    }

    [Theory]
    [InlineData("GUID du mode de gestion de l'alimentation : 381b4222-f694-41f0-9685-ff5bb260df2e  (Utilisation normale) *", "Utilisation normale", true)]
    [InlineData("GUID des Energieschemas: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Höchstleistung)", "Höchstleistung", false)]
    [InlineData("GUID de la combinación de energía: a1841308-3541-4fab-bc81-f71556f20b4a  (Economizador) *", "Economizador", true)]
    public void Scheme_lines_are_parsed_in_any_language(string line, string name, bool active)
    {
        var scheme = PowerPlans.ParseScheme(line);
        Assert.NotNull(scheme);
        Assert.Equal(name, scheme!.Name);
        Assert.Equal(active, scheme.IsActive);
    }

    [Fact]
    public void French_list_output_yields_every_scheme()
    {
        const string output = """
            Modes de gestion de l'alimentation existants (* actif)
            -----------------------------------
            GUID du mode de gestion de l'alimentation : 381b4222-f694-41f0-9685-ff5bb260df2e  (Utilisation normale)
            GUID du mode de gestion de l'alimentation : e9a42b02-d5df-448d-aa00-03f14749eb61  (Performances optimales) *
            """;
        var list = PowerPlans.ParseList(output);
        Assert.Equal(2, list.Count);
        Assert.Single(list, s => s.IsActive && s.Id == PowerPlans.Ultimate);
        Assert.Equal("Utilisation normale", list[0].Name);
    }

    [Fact]
    public void List_output_yields_every_scheme()
    {
        const string output = """
            Existing Power Schemes (* Active)
            -----------------------------------
            Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
            Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance) *
            Power Scheme GUID: a1841308-3541-4fab-bc81-f71556f20b4a  (Power saver)
            """;
        var list = PowerPlans.ParseList(output);
        Assert.Equal(3, list.Count);
        Assert.Single(list, s => s.IsActive && s.Id == PowerPlans.HighPerformance);
        Assert.Equal("High performance", PowerPlans.NameFor(PowerPlans.HighPerformance));
        Assert.Equal("Ultimate Performance", PowerPlans.NameFor(PowerPlans.Ultimate));
        Assert.Equal("Custom plan", PowerPlans.NameFor(Guid.NewGuid()));
        Assert.True(PowerPlans.IsHighPerformanceName("Ultimate Performance"));
        Assert.False(PowerPlans.IsHighPerformanceName("Balanced"));
        Assert.Empty(PowerPlans.ParseList(""));
    }

    [Fact]
    public void Copies_of_built_in_plans_are_found_by_guid_on_translated_windows()
    {
        var copy = new Guid("6f1b1f5e-6a2b-4c4e-9a52-3f0b6e1f7d10");
        const string german =
            "Bestehende Energieschemas (* Aktiv)\r\n" +
            "-----------------------------------\r\n" +
            "GUID des Energieschemas: 381b4222-f694-41f0-9685-ff5bb260df2e  (Ausbalanciert)\r\n" +
            "GUID des Energieschemas: 6f1b1f5e-6a2b-4c4e-9a52-3f0b6e1f7d10  (Ultimative Leistung) *\r\n";
        var list = PowerPlans.ParseList(german);

        // Without the remembered GUID the translated name cannot be matched, which is why it is remembered.
        Assert.Null(PowerPlans.FindCopy(list, PowerPlans.Ultimate, remembered: null, "Ultimate"));
        Assert.Equal(copy, PowerPlans.FindCopy(list, PowerPlans.Ultimate, copy, "Ultimate")!.Id);
        // A remembered copy that was deleted is ignored; the template itself or an English name still count.
        var english = PowerPlans.ParseList("Power Scheme GUID: 11111111-2222-3333-4444-555555555555  (Ultimate Performance)");
        Assert.Equal(new Guid("11111111-2222-3333-4444-555555555555"), PowerPlans.FindCopy(english, PowerPlans.Ultimate, copy, "Ultimate")!.Id);
        var template = PowerPlans.ParseList($"Power Scheme GUID: {PowerPlans.Ultimate}  (Höchstleistung)");
        Assert.Equal(PowerPlans.Ultimate, PowerPlans.FindCopy(template, PowerPlans.Ultimate, null)!.Id);

        var active = list.Single(s => s.IsActive);
        Assert.True(PowerPlans.IsPlanOrCopy(active, PowerPlans.Ultimate, copy, "Ultimate"));
        Assert.False(PowerPlans.IsPlanOrCopy(active, PowerPlans.Ultimate, null, "Ultimate"));
        Assert.True(PowerPlans.IsPlanOrCopy(active, PowerPlans.HighPerformance, copy));
        Assert.False(PowerPlans.IsPlanOrCopy(active, PowerPlans.HighPerformance, null));
    }
}

public class DemoOptimizationServiceTests
{
    [Fact]
    public async Task Power_plans_are_mutually_exclusive_and_revertible()
    {
        var svc = new DemoOptimizationService([]);
        Assert.True((await svc.GetStateAsync("power.balanced", CancellationToken.None)).IsApplied);
        Assert.False((await svc.GetStateAsync("power.highperformance", CancellationToken.None)).IsApplied);

        Assert.True((await svc.ApplyAsync("power.highperformance", CancellationToken.None)).Success);
        var high = await svc.GetStateAsync("power.highperformance", CancellationToken.None);
        Assert.True(high.IsApplied);
        Assert.Equal("Active: High performance", high.CurrentValue);
        Assert.False((await svc.GetStateAsync("power.balanced", CancellationToken.None)).IsApplied);

        Assert.True((await svc.RevertAsync("power.highperformance", CancellationToken.None)).Success);
        Assert.True((await svc.GetStateAsync("power.balanced", CancellationToken.None)).IsApplied);
    }

    [Fact]
    public async Task Opposite_actions_flip_each_other()
    {
        var svc = new DemoOptimizationService([]);
        Assert.True((await svc.ApplyAsync("visuals.performance", CancellationToken.None)).Success);
        Assert.True((await svc.GetStateAsync("visuals.performance", CancellationToken.None)).IsApplied);
        Assert.False((await svc.GetStateAsync("visuals.balanced", CancellationToken.None)).IsApplied);
        Assert.True((await svc.ApplyAsync("faststartup.disable", CancellationToken.None)).Success);
        Assert.Equal("Fast startup off", (await svc.GetStateAsync("faststartup.enable", CancellationToken.None)).CurrentValue);
    }

    [Fact]
    public async Task On_demand_tools_stream_output_and_cleanup_redirects()
    {
        var svc = new DemoOptimizationService([]);
        var lines = new List<string>();
        svc.OutputLine += (_, line) => lines.Add(line);
        var result = await svc.ApplyAsync("sfc.scannow", CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(result.RebootRequired);
        Assert.Contains(lines, l => l.Contains("integrity violations"));
        Assert.Null((await svc.GetStateAsync("sfc.scannow", CancellationToken.None)).IsApplied);

        var cleanup = await svc.ApplyAsync("tempfiles.clean", CancellationToken.None);
        Assert.False(cleanup.Success);
        Assert.Contains("Cleanup page", cleanup.Message);
        Assert.False((await svc.RevertAsync("dism.restorehealth", CancellationToken.None)).Success);
        Assert.False((await svc.SetServiceStartModeAsync("SysMain", "Whenever", CancellationToken.None)).Success);
    }
}

public class OptimizationHelpersTests
{
    [Fact]
    public void Registry_helpers_never_throw_off_windows()
    {
        if (PlatformGuard.IsWindows)
        {
            return;
        }

        Assert.Null(RegistryTweaks.GetDword(RegistryHive.CurrentUser, @"Software\Test", "Value"));
        Assert.Null(RegistryTweaks.GetString(RegistryHive.LocalMachine, @"Software\Test", "Value"));
        Assert.False(RegistryTweaks.KeyExists(RegistryHive.LocalMachine, @"Software\Test"));
        Assert.Empty(RegistryTweaks.GetValueNames(RegistryHive.LocalMachine, @"Software\Test"));
        Assert.False(RegistryTweaks.SetDword(RegistryHive.CurrentUser, @"Software\Test", "Value", 1).Success);
        Assert.False(RegistryTweaks.DeleteValue(RegistryHive.CurrentUser, @"Software\Test", "Value").Success);
        Assert.Equal(@"HKLM\SOFTWARE\Test", RegistryTweaks.Describe(RegistryHive.LocalMachine, @"SOFTWARE\Test"));
    }

    [Fact]
    public async Task External_commands_report_platform_and_split_progress_lines()
    {
        if (!PlatformGuard.IsWindows)
        {
            var result = await ExternalCommand.RunAsync("powercfg.exe", "/list", CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal(-1, result.ExitCode);
        }

        Assert.Equal(["[=== 10% ===]", "[=== 20% ===]"], ExternalCommand.SplitProgressLine("[=== 10% ===]\r[=== 20% ===]\r"));
        var cmd = new CommandResult(0, "line one\nline two\n", string.Empty, false);
        Assert.Equal("line two", cmd.LastLine);
        Assert.True(cmd.Success);
        Assert.False(new CommandResult(0, "", "", true).Success);
    }
}

public class OptimizationLinksTests
{
    [Fact]
    public void Power_plans_refresh_each_other()
    {
        var affected = OptimizationLinks.AffectedBy("power.highperformance");
        Assert.Equal("power.highperformance", affected[0]);
        Assert.Contains("power.balanced", affected);
        Assert.Contains("power.ultimate", affected);
        Assert.Equal(3, affected.Count);
        Assert.True(OptimizationLinks.AreExclusive("power.balanced", "power.ultimate"));
        Assert.False(OptimizationLinks.AreExclusive("power.balanced", "power.balanced"));
    }

    [Fact]
    public void Hibernation_fast_startup_and_visual_presets_are_linked()
    {
        Assert.Contains("faststartup.disable", OptimizationLinks.AffectedBy("hibernation.disable"));
        Assert.Contains("faststartup.enable", OptimizationLinks.AffectedBy("hibernation.disable"));
        Assert.Contains("hibernation.disable", OptimizationLinks.AffectedBy("faststartup.enable"));
        Assert.Contains("transparency.disable", OptimizationLinks.AffectedBy("visuals.performance"));
        Assert.Contains("animations.disable", OptimizationLinks.AffectedBy("visuals.balanced"));
        Assert.Contains("visuals.performance", OptimizationLinks.AffectedBy("transparency.disable"));
        Assert.True(OptimizationLinks.AreExclusive("faststartup.enable", "faststartup.disable"));
        Assert.True(OptimizationLinks.AreExclusive("visuals.performance", "visuals.balanced"));
        Assert.False(OptimizationLinks.AreExclusive("transparency.disable", "gamemode.enable"));
    }

    [Fact]
    public void Unlinked_actions_only_refresh_themselves()
    {
        Assert.Equal(["gamemode.enable"], OptimizationLinks.AffectedBy("gamemode.enable"));
        Assert.Equal(["sfc.scannow"], OptimizationLinks.AffectedBy("sfc.scannow"));
        Assert.Equal(["latency.hags.off"], OptimizationLinks.AffectedBy("latency.hags.off"));
        Assert.Throws<ArgumentException>(() => OptimizationLinks.AffectedBy(" "));
    }

    [Fact]
    public async Task Demo_service_mirrors_the_links()
    {
        var svc = new DemoOptimizationService([]);
        Assert.True((await svc.ApplyAsync("hibernation.disable", CancellationToken.None)).Success);
        Assert.True((await svc.GetStateAsync("faststartup.disable", CancellationToken.None)).IsApplied);
        Assert.False((await svc.GetStateAsync("faststartup.enable", CancellationToken.None)).IsApplied);

        Assert.True((await svc.ApplyAsync("visuals.performance", CancellationToken.None)).Success);
        Assert.True((await svc.GetStateAsync("transparency.disable", CancellationToken.None)).IsApplied);
        Assert.True((await svc.GetStateAsync("animations.disable", CancellationToken.None)).IsApplied);
        Assert.True((await svc.ApplyAsync("visuals.balanced", CancellationToken.None)).Success);
        Assert.False((await svc.GetStateAsync("transparency.disable", CancellationToken.None)).IsApplied);
    }
}

public class SfcResultParserTests
{
    [Fact]
    public void English_console_verdicts_are_read_directly()
    {
        Assert.Equal(SfcOutcome.NoViolations, SfcResultParser.FromConsole("Windows Resource Protection did not find any integrity violations."));
        Assert.Equal(SfcOutcome.Repaired, SfcResultParser.FromConsole("Windows Resource Protection found corrupt files and successfully repaired them."));
        Assert.Equal(SfcOutcome.CouldNotRepair, SfcResultParser.FromConsole("Windows Resource Protection found corrupt files but was unable to fix some of them."));
        Assert.Equal(SfcOutcome.Unknown, SfcResultParser.FromConsole("Der Windows-Ressourcenschutz hat keine Integritätsverletzungen gefunden."));
        Assert.Equal(SfcOutcome.Unknown, SfcResultParser.FromConsole(null));
    }

    [Fact]
    public void Localized_console_falls_back_to_the_english_cbs_log_entries()
    {
        string[] clean =
        [
            "2026-10-26 10:00:01, Info                  CSI    00000a1b [SR] Verifying 100 components",
            "2026-10-26 10:00:02, Info                  CSI    00000a1c [SR] Beginning Verify and Repair transaction",
            "2026-10-26 10:00:03, Info                  CSI    00000a1d [SR] Verify complete",
            "2026-10-26 10:00:04, Info                  CSI    00000a1e [SR] Repairing 0 components",
        ];
        Assert.Equal(SfcOutcome.NoViolations, SfcResultParser.Interpret("Der Windows-Ressourcenschutz hat keine Integritätsverletzungen gefunden.", clean));

        string[] repaired =
        [
            "... [SR] Verify complete",
            "... [SR] Cannot repair member file [l:34{17}]\"Accessibility.dll\" of Accessibility, Version = 6.0.6000.16386",
            "... [SR] Repaired file \\??\\C:\\Windows\\System32\\Accessibility.dll by copying from backup",
            "... [SR] Repairing 1 components",
            "... [SR] Repair complete",
        ];
        Assert.Equal(SfcOutcome.Repaired, SfcResultParser.Interpret("Le programme de protection des ressources Windows a trouvé des fichiers endommagés", repaired));

        string[] partial =
        [
            "... [SR] Verify complete",
            "... [SR] Cannot repair member file [l:34{17}]\"Accessibility.dll\" of Accessibility",
            "... [SR] Cannot repair member file [l:20{10}]\"other.dll\" of Other",
            "... [SR] Repaired file \\??\\C:\\Windows\\System32\\Accessibility.dll by copying from backup",
        ];
        Assert.Equal(SfcOutcome.ViolationsFound, SfcResultParser.Interpret("localized", partial));

        Assert.Equal(SfcOutcome.Unknown, SfcResultParser.Interpret("localized", ["unrelated line", ""]));
        Assert.Equal(SfcOutcome.Unknown, SfcResultParser.Interpret("localized", null));
        Assert.Equal(SfcOutcome.NoViolations, SfcResultParser.Interpret("did not find any integrity violations", partial));
    }
}

public class DnsBackupTests
{
    private const string Ethernet = "{4D36E972-E325-11CE-BFC1-08002BE10318}";
    private const string WiFi = "{0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9}";

    [Fact]
    public void Static_servers_and_dhcp_round_trip_through_the_registry_text()
    {
        var entries = new[]
        {
            new DnsBackupEntry(Ethernet, DnsBackup.ParseServers("192.168.1.10,10.0.0.53")),
            new DnsBackupEntry(WiFi, DnsBackup.ParseServers("")),
        };
        var parsed = DnsBackup.Parse(DnsBackup.Format(entries));
        Assert.Equal(2, parsed.Count);
        Assert.Equal(["192.168.1.10", "10.0.0.53"], parsed[0].Servers);
        Assert.False(parsed[0].WasAutomatic);
        Assert.True(parsed[1].WasAutomatic);
        Assert.Equal(["8.8.8.8", "fd00::1"], DnsBackup.ParseServers("8.8.8.8 fd00::1;not-an-ip"));
        Assert.Empty(DnsBackup.Parse("garbage;{not-a-guid}=1.2.3.4"));
    }

    [Fact]
    public void Applying_twice_keeps_the_original_servers()
    {
        var first = new[] { new DnsBackupEntry(Ethernet, ["192.168.1.10"]) };
        var second = new[] { new DnsBackupEntry(Ethernet, ["1.1.1.1", "1.0.0.1"]), new DnsBackupEntry(WiFi, []) };
        var merged = DnsBackup.Merge(first, second);
        Assert.Equal(2, merged.Count);
        Assert.Equal(["192.168.1.10"], merged.Single(e => e.InterfaceGuid == Ethernet).Servers);
        Assert.True(merged.Single(e => e.InterfaceGuid == WiFi).WasAutomatic);
        Assert.True(DnsBackup.IsCloudflare(second[0].Servers));
        Assert.False(DnsBackup.IsCloudflare(first[0].Servers));
    }

    [Fact]
    public void Restore_script_resets_only_adapters_that_used_dhcp()
    {
        var script = DnsBackup.BuildRestoreScript([new DnsBackupEntry(Ethernet, ["192.168.1.10", "10.0.0.53"]), new DnsBackupEntry(WiFi, [])]);
        Assert.Contains($"$_.InterfaceGuid -eq '{Ethernet}'", script);
        Assert.Contains("-ServerAddresses @('192.168.1.10','10.0.0.53')", script);
        Assert.Contains($"$_.InterfaceGuid -eq '{WiFi}'", script);
        Assert.Equal(1, CountOf(script, "-ResetServerAddresses"));
        Assert.EndsWith("Clear-DnsClientCache", script);
        Assert.DoesNotContain("-ResetServerAddresses", DnsBackup.BuildRestoreScript([new DnsBackupEntry(Ethernet, ["192.168.1.10"])]));

        // Without a backup only DHCP adapters that use Cloudflare are reset.
        Assert.Contains("$ip.Dhcp -eq 'Enabled'", DnsBackup.LegacyRevertScript);
    }

    private static int CountOf(string text, string value) => (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
}
