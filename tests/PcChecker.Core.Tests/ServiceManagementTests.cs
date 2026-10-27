using PcChecker.Core.Services;
using Xunit;

namespace PcChecker.Core.Tests;

public class ServiceManagementTests
{
    private static ServiceDetails Service(string name, ServiceRunState state = ServiceRunState.Running,
        ServiceStartupType startup = ServiceStartupType.Automatic, bool microsoft = true, bool canStop = true,
        string? display = null) =>
        new(name, display ?? name, "A service.", state, startup, "LocalSystem", 100, null, @"C:\Windows\System32\svchost.exe",
            microsoft, canStop, CanPauseContinue: false);

    [Theory]
    [InlineData(ServiceStartupMapping.AutoStart, false, ServiceStartupType.Automatic)]
    [InlineData(ServiceStartupMapping.AutoStart, true, ServiceStartupType.AutomaticDelayed)]
    [InlineData(ServiceStartupMapping.DemandStart, false, ServiceStartupType.Manual)]
    [InlineData(ServiceStartupMapping.Disabled, false, ServiceStartupType.Disabled)]
    [InlineData(ServiceStartupMapping.BootStart, false, ServiceStartupType.Boot)]
    [InlineData(ServiceStartupMapping.SystemStart, false, ServiceStartupType.System)]
    public void Win32_start_type_maps_including_delayed(int dwStartType, bool delayed, ServiceStartupType expected) =>
        Assert.Equal(expected, ServiceStartupMapping.FromWin32(dwStartType, delayed));

    [Fact]
    public void Automatic_round_trips_to_win32_with_delayed_flag()
    {
        Assert.Equal((ServiceStartupMapping.AutoStart, false), ServiceStartupMapping.ToWin32(ServiceStartupType.Automatic));
        Assert.Equal((ServiceStartupMapping.AutoStart, true), ServiceStartupMapping.ToWin32(ServiceStartupType.AutomaticDelayed));
        Assert.Equal((ServiceStartupMapping.DemandStart, false), ServiceStartupMapping.ToWin32(ServiceStartupType.Manual));
        Assert.Equal((ServiceStartupMapping.Disabled, false), ServiceStartupMapping.ToWin32(ServiceStartupType.Disabled));
    }

    [Fact]
    public void Boot_and_system_and_unknown_cannot_be_set()
    {
        Assert.Null(ServiceStartupMapping.ToWin32(ServiceStartupType.Boot));
        Assert.Null(ServiceStartupMapping.ToWin32(ServiceStartupType.System));
        Assert.Null(ServiceStartupMapping.ToWin32(ServiceStartupType.Unknown));
        Assert.False(ServiceStartupMapping.IsSettable(ServiceStartupType.Boot));
    }

    [Theory]
    [InlineData("Automatic (Delayed Start)", ServiceStartupType.AutomaticDelayed)]
    [InlineData("Auto", ServiceStartupType.Automatic)]
    [InlineData("demand", ServiceStartupType.Manual)]
    [InlineData("Disabled", ServiceStartupType.Disabled)]
    public void Labels_parse_back_to_the_value(string label, ServiceStartupType expected) =>
        Assert.Equal(expected, ServiceStartupMapping.Parse(label));

    [Theory]
    [InlineData("RpcSs")]
    [InlineData("DcomLaunch")]
    [InlineData("LSM")]
    [InlineData("Winmgmt")]
    [InlineData("EventLog")]
    [InlineData("PlugPlay")]
    [InlineData("Power")]
    [InlineData("BFE")]
    [InlineData("MpsSvc")]
    [InlineData("WinDefend")]
    public void Critical_services_cannot_be_stopped_with_a_reason(string name)
    {
        Assert.True(ServiceSafetyRules.IsCritical(name));
        var (allowed, reason) = ServiceSafetyRules.CanStop(Service(name));
        Assert.False(allowed);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Critical_services_cannot_be_set_to_manual_or_disabled()
    {
        Assert.False(ServiceSafetyRules.CanSetStartup("RpcSs", ServiceStartupType.Disabled).Allowed);
        Assert.False(ServiceSafetyRules.CanSetStartup("RpcSs", ServiceStartupType.Manual).Allowed);
        // A critical service may still be left on Automatic.
        Assert.True(ServiceSafetyRules.CanSetStartup("RpcSs", ServiceStartupType.Automatic).Allowed);
    }

    [Fact]
    public void Ordinary_third_party_service_can_be_stopped_and_disabled()
    {
        var service = Service("GoogleUpdaterService", microsoft: false);
        Assert.True(ServiceSafetyRules.CanStop(service).Allowed);
        Assert.True(ServiceSafetyRules.CanSetStartup(service.Name, ServiceStartupType.Disabled).Allowed);
    }

    [Fact]
    public void Non_stoppable_service_is_refused_even_when_not_critical()
    {
        var service = Service("SomeOrdinary", canStop: false, microsoft: false);
        Assert.False(ServiceSafetyRules.CanStop(service).Allowed);
    }

    [Fact]
    public void Windows_update_warns_but_is_allowed_to_be_disabled()
    {
        var (allowed, reason) = ServiceSafetyRules.CanSetStartup("wuauserv", ServiceStartupType.Disabled);
        Assert.True(allowed);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Safe_to_set_manual_note_only_for_known_third_party_hog()
    {
        var hog = Service("GoogleUpdaterService", microsoft: false, display: "Google Update Service");
        var core = Service("Winmgmt");
        Assert.NotNull(ServiceSafetyRules.SafeToSetManualNote(hog));
        Assert.Null(ServiceSafetyRules.SafeToSetManualNote(core));
    }

    [Fact]
    public void Filter_matches_running_stopped_third_party_and_disabled()
    {
        var running = Service("A", ServiceRunState.Running, microsoft: true);
        var stopped = Service("B", ServiceRunState.Stopped, microsoft: true);
        var thirdParty = Service("C", ServiceRunState.Running, microsoft: false);
        var disabled = Service("D", ServiceRunState.Stopped, ServiceStartupType.Disabled, microsoft: true);

        Assert.True(ServiceFilter.MatchesFilter(running, ServiceFilterKind.Running));
        Assert.False(ServiceFilter.MatchesFilter(stopped, ServiceFilterKind.Running));
        Assert.True(ServiceFilter.MatchesFilter(stopped, ServiceFilterKind.Stopped));
        Assert.True(ServiceFilter.MatchesFilter(thirdParty, ServiceFilterKind.ThirdPartyOnly));
        Assert.False(ServiceFilter.MatchesFilter(running, ServiceFilterKind.ThirdPartyOnly));
        Assert.True(ServiceFilter.MatchesFilter(disabled, ServiceFilterKind.Disabled));
    }

    [Fact]
    public void Search_matches_name_display_and_pid()
    {
        var service = Service("W32Time", display: "Windows Time");
        Assert.True(ServiceFilter.MatchesSearch(service, "time"));
        Assert.True(ServiceFilter.MatchesSearch(service, "W32"));
        Assert.True(ServiceFilter.MatchesSearch(service, "100"));
        Assert.False(ServiceFilter.MatchesSearch(service, "nomatch"));
        Assert.True(ServiceFilter.MatchesSearch(service, null));
    }

    [Fact]
    public void Dependency_stop_order_puts_dependents_first_and_start_order_reverses_it()
    {
        // leaf depends on middle depends on root; required-by is the reverse.
        var dependsOn = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["leaf"] = ["middle"],
            ["middle"] = ["root"],
        };
        var requiredBy = ServiceDependencyOrder.BuildRequiredBy(dependsOn);

        var stop = ServiceDependencyOrder.StopOrder("root", requiredBy).ToList();
        Assert.Equal("root", stop[^1]); // root stops last
        Assert.True(stop.IndexOf("leaf") < stop.IndexOf("middle"));
        Assert.True(stop.IndexOf("middle") < stop.IndexOf("root"));

        var start = ServiceDependencyOrder.StartOrder(stop);
        Assert.Equal("root", start[0]); // root starts first
        Assert.Equal(Enumerable.Reverse(stop), start);
    }

    [Fact]
    public void Dependency_order_does_not_loop_on_a_cycle()
    {
        var requiredBy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = ["b"],
            ["b"] = ["a"],
        };
        var stop = ServiceDependencyOrder.StopOrder("a", requiredBy);
        Assert.Contains("a", stop);
        Assert.Contains("b", stop);
        Assert.Equal(2, stop.Count);
    }
}
