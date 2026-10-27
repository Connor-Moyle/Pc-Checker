using PcChecker.Core.Performance;
using PcChecker.Core.Startup;

namespace PcChecker.Core.Services;

/// <summary>
/// Which services PC Checker refuses to stop or disable, and why, in plain words. Builds on the shared
/// <see cref="ProtectedServices"/> list (used by the Startup and Speed-check pages too) and adds a few that matter
/// specifically when stopping, not only when changing the start type. Pure data, so it is unit-tested.
/// </summary>
public static class ServiceSafetyRules
{
    /// <summary>
    /// Services that must keep running while Windows is on: stopping them signs you out, drops the network or crashes
    /// the PC. These are refused in addition to everything <see cref="ProtectedServices"/> marks as protected.
    /// </summary>
    private static readonly Dictionary<string, string> MustKeepRunning = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RpcSs"] = "Remote Procedure Call is the backbone of Windows; stopping it crashes the PC.",
        ["RpcEptMapper"] = "The RPC Endpoint Mapper is required by Remote Procedure Call.",
        ["DcomLaunch"] = "The DCOM Server Process Launcher starts core system components.",
        ["LSM"] = "Local Session Manager runs your sign-in session; stopping it signs you out.",
        ["Winmgmt"] = "Windows Management Instrumentation; PC Checker and many tools read the PC through it.",
        ["EventLog"] = "The Windows Event Log records what happens; health checks and troubleshooting need it.",
        ["PlugPlay"] = "Plug and Play detects your hardware.",
        ["Power"] = "The Power service runs power plans and sleep.",
        ["BFE"] = "The Base Filtering Engine is what the firewall and VPNs run on.",
        ["MpsSvc"] = "Windows Defender Firewall.",
        ["WinDefend"] = "Microsoft Defender Antivirus; stopping it turns off your real-time protection.",
        ["SamSs"] = "The Security Accounts Manager handles sign-in.",
        ["ProfSvc"] = "The User Profile Service loads your profile; stopping it can lock you out.",
        ["gpsvc"] = "The Group Policy Client; some sign-in steps depend on it.",
        ["Schedule"] = "Task Scheduler runs Windows maintenance and many apps.",
        ["Dnscache"] = "The DNS Client resolves website names.",
        ["Dhcp"] = "The DHCP Client gets your PC its network address.",
        ["nsi"] = "The Network Store Interface; networking stops without it.",
    };

    /// <summary>
    /// Services whose start type is only safe to leave alone unless a policy already changed it; stopping or disabling
    /// them is a security choice, so the UI warns rather than silently allows it.
    /// </summary>
    private static readonly Dictionary<string, string> SecuritySensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wuauserv"] = "Windows Update installs security fixes; disabling it leaves the PC unpatched.",
        ["WdNisSvc"] = "Microsoft Defender network inspection; part of your protection.",
        ["SecurityHealthService"] = "Windows Security tracks your protection status.",
        ["wscsvc"] = "The Security Center warns you when protection is off.",
    };

    /// <summary>True for a service PC Checker will never stop or set to Manual/Disabled from the Services page.</summary>
    public static bool IsCritical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return MustKeepRunning.ContainsKey(name) || ProtectedServices.IsProtected(name);
    }

    /// <summary>The plain reason a service is critical, or null for an ordinary service.</summary>
    public static string? CriticalReason(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (MustKeepRunning.TryGetValue(name, out var reason))
        {
            return reason;
        }

        return ProtectedServices.IsProtected(name) ? ProtectedServices.Reason(name) : null;
    }

    /// <summary>A security warning shown before stopping or disabling a protection-related service, or null.</summary>
    public static string? SecurityWarning(string? name) =>
        name is not null && SecuritySensitive.TryGetValue(name, out var reason) ? reason : null;

    /// <summary>The reason shown for a service an organization manages by policy.</summary>
    public const string PolicyManagedReason =
        "Windows Update is managed by your organization's policy on this PC, so PC Checker leaves it alone.";

    /// <summary>True for a service that may not be stopped or set to Manual/Disabled here (critical or policy-managed).</summary>
    public static bool IsLocked(ServiceDetails service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.ManagedByPolicy || IsCritical(service.Name);
    }

    /// <summary>The plain reason a service is locked, or null.</summary>
    public static string? LockedReason(ServiceDetails service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return CriticalReason(service.Name) ?? (service.ManagedByPolicy ? PolicyManagedReason : null);
    }

    /// <summary>
    /// True when the Windows Update policy key holds settings (a WSUS server, automatic-update rules), meaning an
    /// organization manages updates. <paramref name="policyValueNames"/> are the value names under
    /// <c>HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate</c> and its <c>AU</c> subkey.
    /// </summary>
    public static bool IsUpdatePolicyManaged(IEnumerable<string>? policyValueNames) =>
        policyValueNames?.Any(n => !string.IsNullOrWhiteSpace(n)) == true;

    /// <summary>The service names that only become protected when a policy manages them.</summary>
    public static bool IsPolicyProtectable(string? name) => string.Equals(name, "wuauserv", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the service may be started, with the reason when it may not.</summary>
    public static (bool Allowed, string? Reason) CanStart(ServiceDetails service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (service.State != ServiceRunState.Stopped)
        {
            return (false, service.IsRunning ? "This service is already running." : "This service is busy starting or stopping.");
        }

        if (service.StartupType == ServiceStartupType.Disabled)
        {
            return (false, "This service is disabled. Set its start type to Manual or Automatic first.");
        }

        return (true, null);
    }

    /// <summary>Whether the service may be stopped, with the reason when it may not and a warning when it may.</summary>
    public static (bool Allowed, string? Reason) CanStop(ServiceDetails service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (IsLocked(service))
        {
            return (false, LockedReason(service));
        }

        if (service.State == ServiceRunState.Stopped)
        {
            return (false, "This service is already stopped.");
        }

        if (!service.CanStop)
        {
            return (false, service.State is ServiceRunState.Running or ServiceRunState.Paused
                ? "This service does not allow being stopped on its own; it stops with the component that owns it."
                : "This service is busy starting or stopping. Try again in a moment.");
        }

        return (true, SecurityWarning(service.Name));
    }

    /// <summary>Whether the service may be paused: it must be running and accept pause and continue.</summary>
    public static (bool Allowed, string? Reason) CanPause(ServiceDetails service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (IsLocked(service))
        {
            return (false, LockedReason(service));
        }

        if (service.State != ServiceRunState.Running)
        {
            return (false, "Only a running service can be paused.");
        }

        return service.CanPauseContinue ? (true, null) : (false, "This service does not support pausing.");
    }

    /// <summary>Whether a paused service may be continued.</summary>
    public static (bool Allowed, string? Reason) CanContinue(ServiceDetails service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (service.State != ServiceRunState.Paused)
        {
            return (false, "Only a paused service can be continued.");
        }

        return service.CanPauseContinue ? (true, null) : (false, "This service does not support pausing.");
    }

    /// <summary>Whether the start type may be changed to <paramref name="target"/>, with the reason when it may not.</summary>
    public static (bool Allowed, string? Reason) CanSetStartup(ServiceDetails service, ServiceStartupType target)
    {
        ArgumentNullException.ThrowIfNull(service);
        return CanSetStartup(service.Name, target, service.ManagedByPolicy);
    }

    /// <summary>Whether the start type may be changed to <paramref name="target"/>, with the reason when it may not.</summary>
    public static (bool Allowed, string? Reason) CanSetStartup(string? name, ServiceStartupType target, bool managedByPolicy = false)
    {
        if (!ServiceStartupMapping.IsSettable(target))
        {
            return (false, $"{ServiceStartupMapping.Label(target)} is not a start type PC Checker can set.");
        }

        bool stopsStartup = target is ServiceStartupType.Manual or ServiceStartupType.Disabled;
        if (stopsStartup && IsCritical(name))
        {
            return (false, CriticalReason(name));
        }

        if (stopsStartup && managedByPolicy)
        {
            return (false, PolicyManagedReason);
        }

        string? warning = stopsStartup ? SecurityWarning(name) : null;
        return (true, warning);
    }

    /// <summary>
    /// A one-line "recommended: safe to set to Manual" note for a third-party service that runs at start-up without
    /// being needed, using the shared known-hog list. Null for everything else, so it never nudges core services.
    /// </summary>
    public static string? SafeToSetManualNote(ServiceDetails service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (service.IsMicrosoft || IsCritical(service.Name) || !ServiceStartupMapping.IsAutomatic(service.StartupType))
        {
            return null;
        }

        var info = new ServiceInfo(service.Name, service.DisplayName, ServiceStartupMapping.Label(service.StartupType),
            ServiceStartupMapping.StateLabel(service.State), service.BinaryPath, service.IsMicrosoft, service.Description);
        var reason = ServiceClassifier.KnownHogReason(info);
        return reason is null ? null : $"Safe to set to Manual. {reason}";
    }

    /// <summary>The critical service names, exposed for tests and for the "about" text.</summary>
    public static IReadOnlyCollection<string> CriticalNames => MustKeepRunning.Keys;
}
