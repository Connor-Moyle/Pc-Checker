using PcChecker.Core.Services;
using PcChecker.Core.TaskManager;
using PcChecker.Core.Users;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Tests for the review fixes in Services, Users and Replace Task Manager.</summary>
public class TaskManagerReviewFixTests
{
    private const string Exe = @"C:\Program Files\PC Checker\PcChecker.exe";
    private const string OldExe = @"C:\Program Files\PC Checker Old\PcChecker.exe";
    private static readonly string[] ProgramFiles = [@"C:\Program Files", @"C:\Program Files (x86)"];

    private static ServiceDetails Service(string name, ServiceRunState state = ServiceRunState.Running,
        ServiceStartupType startup = ServiceStartupType.Manual, bool canStop = true, bool canPause = false) =>
        new(name, name, null, state, startup, "LocalSystem", 100, null, null, false, canStop, canPause);

    [Fact]
    public void Classify_sorts_every_kind_of_debugger_value()
    {
        bool Exists(string path) => path == Exe || path == OldExe || path == @"C:\Tools\procexp64.exe";

        Assert.Equal(DebuggerValueKind.None, TaskManagerReplacement.Classify(null, Exe, Exists));
        Assert.Equal(DebuggerValueKind.Ours, TaskManagerReplacement.Classify($"\"{Exe}\" --taskmgr", Exe, Exists));
        Assert.Equal(DebuggerValueKind.OtherCopy, TaskManagerReplacement.Classify($"\"{OldExe}\" --taskmgr", Exe, Exists));
        Assert.Equal(DebuggerValueKind.Stale, TaskManagerReplacement.Classify("\"D:\\Gone\\PcChecker.exe\" --taskmgr", Exe, Exists));
        Assert.Equal(DebuggerValueKind.ThirdParty, TaskManagerReplacement.Classify("\"C:\\Tools\\procexp64.exe\"", Exe, Exists));
    }

    [Fact]
    public void Classify_treats_this_exe_spelled_with_dot_segments_as_ours()
    {
        var value = "\"C:\\Program Files\\PC Checker\\.\\sub\\..\\PcChecker.exe\" --taskmgr";
        Assert.Equal(DebuggerValueKind.Ours, TaskManagerReplacement.Classify(value, Exe, _ => true));
    }

    [Theory]
    [InlineData(@"C:\Program Files\..\Users\me\PcChecker.exe", false)]
    [InlineData(@"C:\Program Files\PC Checker\..\..\Temp\PcChecker.exe", false)]
    [InlineData(@"C:\Program Files\PC Checker\.\PcChecker.exe", true)]
    [InlineData(@"C:\Program Files (x86)\PC Checker\PcChecker.exe", true)]
    public void Installed_location_resolves_dot_segments(string path, bool expected) =>
        Assert.Equal(expected, TaskManagerReplacement.IsInstalledLocation(path, ProgramFiles));

    [Theory]
    [InlineData(@"C:\a\b\..\c", @"C:\a\c")]
    [InlineData(@"C:\..\..\x", @"C:\x")]
    [InlineData(@"\\server\share\.\x\..\y", @"\\server\share\y")]
    [InlineData("C:/a/./b", @"C:\a\b")]
    public void Dot_segments_collapse_like_windows(string input, string expected) =>
        Assert.Equal(expected, TaskManagerReplacement.CollapseDotSegments(input));

    [Fact]
    public void Status_reports_third_party_and_target()
    {
        var status = new TaskManagerReplacementStatus(false, false, true, null, false, "\"C:\\Tools\\procexp64.exe\" /e")
        {
            Kind = DebuggerValueKind.ThirdParty,
        };
        Assert.True(status.ThirdPartyPresent);
        Assert.Equal(@"C:\Tools\procexp64.exe", status.CurrentTarget);
    }

    [Theory]
    [InlineData(5, "administrator")]
    [InlineData(1051, "depend on this one")]
    [InlineData(1052, "does not accept")]
    [InlineData(1056, "already running")]
    [InlineData(1058, "disabled")]
    public void Service_errors_are_plain_words(int code, string expected)
    {
        var text = ServiceErrorText.Describe(code);
        Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(code.ToString(System.Globalization.CultureInfo.InvariantCulture), text);
        Assert.DoesNotContain("error " + code, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_service_error_still_reads_as_a_sentence() =>
        Assert.StartsWith("Windows refused the request.", ServiceErrorText.Describe(4242));

    [Fact]
    public void Restart_plan_only_cycles_running_dependents_in_windows_order()
    {
        var plan = ServiceRestartPlan.Build("Audiosrv",
            [new("AudioEndpointBuilderChild", true), new("StoppedOne", false), new("OtherChild", true)],
            _ => false);
        Assert.Null(plan.Refusal);
        Assert.Equal(["AudioEndpointBuilderChild", "OtherChild", "Audiosrv"], plan.StopOrder);
        Assert.Equal(["Audiosrv", "OtherChild", "AudioEndpointBuilderChild"], plan.StartOrder);
        Assert.DoesNotContain("StoppedOne", plan.StartOrder);
    }

    [Fact]
    public void Restart_plan_refuses_when_a_running_dependent_is_locked()
    {
        var plan = ServiceRestartPlan.Build("Something", [new("RpcSs", true)], ServiceSafetyRules.IsCritical);
        Assert.NotNull(plan.Refusal);
        Assert.Contains("RpcSs", plan.Refusal);
        Assert.Empty(plan.StopOrder);
    }

    [Fact]
    public void Restart_plan_ignores_a_locked_dependent_that_is_not_running()
    {
        var plan = ServiceRestartPlan.Build("Something", [new("RpcSs", false)], ServiceSafetyRules.IsCritical);
        Assert.Null(plan.Refusal);
        Assert.Equal(["Something"], plan.StopOrder);
    }

    [Fact]
    public void Stopping_a_stopped_service_says_so_instead_of_the_owner_reason()
    {
        var (allowed, reason) = ServiceSafetyRules.CanStop(Service("Fax", ServiceRunState.Stopped, canStop: false));
        Assert.False(allowed);
        Assert.Equal("This service is already stopped.", reason);
    }

    [Fact]
    public void Start_is_only_offered_for_a_stopped_service_that_is_not_disabled()
    {
        Assert.True(ServiceSafetyRules.CanStart(Service("Fax", ServiceRunState.Stopped)).Allowed);
        Assert.False(ServiceSafetyRules.CanStart(Service("Fax", ServiceRunState.Running)).Allowed);
        Assert.False(ServiceSafetyRules.CanStart(Service("Fax", ServiceRunState.Stopped, ServiceStartupType.Disabled)).Allowed);
    }

    [Fact]
    public void Pause_and_continue_need_the_service_to_accept_them()
    {
        Assert.False(ServiceSafetyRules.CanPause(Service("Fax", canPause: false)).Allowed);
        Assert.True(ServiceSafetyRules.CanPause(Service("Fax", canPause: true)).Allowed);
        Assert.False(ServiceSafetyRules.CanContinue(Service("Fax", canPause: true)).Allowed);
        Assert.True(ServiceSafetyRules.CanContinue(Service("Fax", ServiceRunState.Paused, canPause: true)).Allowed);
        Assert.False(ServiceSafetyRules.CanPause(Service("RpcSs", canPause: true)).Allowed);
    }

    [Fact]
    public void Windows_update_is_locked_when_managed_by_policy()
    {
        var managed = Service("wuauserv", startup: ServiceStartupType.Automatic) with { ManagedByPolicy = true };
        Assert.True(ServiceSafetyRules.IsLocked(managed));
        Assert.False(ServiceSafetyRules.CanStop(managed).Allowed);
        Assert.Equal(ServiceSafetyRules.PolicyManagedReason, ServiceSafetyRules.CanSetStartup(managed, ServiceStartupType.Disabled).Reason);
        Assert.False(ServiceSafetyRules.CanSetStartup(managed, ServiceStartupType.Manual).Allowed);

        var unmanaged = Service("wuauserv", startup: ServiceStartupType.Automatic);
        Assert.False(ServiceSafetyRules.IsLocked(unmanaged));
        Assert.True(ServiceSafetyRules.CanStop(unmanaged).Allowed);
        Assert.NotNull(ServiceSafetyRules.CanStop(unmanaged).Reason);
    }

    [Fact]
    public void Update_policy_is_managed_only_when_values_exist()
    {
        Assert.False(ServiceSafetyRules.IsUpdatePolicyManaged(null));
        Assert.False(ServiceSafetyRules.IsUpdatePolicyManaged([]));
        Assert.True(ServiceSafetyRules.IsUpdatePolicyManaged(["WUServer"]));
        Assert.True(ServiceSafetyRules.IsPolicyProtectable("WUAUSERV"));
        Assert.False(ServiceSafetyRules.IsPolicyProtectable("Fax"));
    }

    [Fact]
    public void Logon_time_comes_from_the_filetime_and_falls_back_only_when_missing()
    {
        var signIn = new DateTimeOffset(2026, 10, 27, 8, 15, 0, TimeSpan.Zero);
        var boot = new DateTimeOffset(2026, 10, 27, 7, 0, 0, TimeSpan.Zero);
        var fromWts = SessionStateMapper.LogonTimeFromFileTime(signIn.UtcDateTime.ToFileTimeUtc());

        Assert.Equal(signIn, fromWts);
        Assert.Null(SessionStateMapper.LogonTimeFromFileTime(0));
        Assert.Null(SessionStateMapper.LogonTimeFromFileTime(-5));
        Assert.Equal(signIn, SessionStateMapper.ChooseLogonTime(fromWts, boot));
        Assert.Equal(boot, SessionStateMapper.ChooseLogonTime(null, boot));
    }
}
