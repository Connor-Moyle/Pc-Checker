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
        var mode = await service.SetServiceStartModeAsync("Bonjour Service", "Manual", CancellationToken.None);
        Assert.False(mode.Success);
        Assert.Equal(1, guard.Calls);
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
