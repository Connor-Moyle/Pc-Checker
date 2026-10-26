namespace PcChecker.Core.Startup;

/// <summary>How careful the app must be with a service's start mode.</summary>
public enum ServiceProtection
{
    /// <summary>Any start mode may be set.</summary>
    None,

    /// <summary>May be changed, but the UI warns about what stops working.</summary>
    Warn,

    /// <summary>Never allowed to be set to Manual or Disabled: Windows breaks without it.</summary>
    Protected,
}

/// <summary>Services that Windows needs to boot, log on, stay secure or run this app. Pure data, testable anywhere.</summary>
public static class ProtectedServices
{
    private static readonly Dictionary<string, string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RpcSs"] = "Remote Procedure Call: almost everything in Windows depends on it.",
        ["RpcEptMapper"] = "RPC Endpoint Mapper: required by RpcSs.",
        ["DcomLaunch"] = "DCOM Server Process Launcher: starts system components.",
        ["Winmgmt"] = "Windows Management Instrumentation: PC Checker and many tools read hardware through it.",
        ["EventLog"] = "Windows Event Log: needed for health checks and troubleshooting.",
        ["Dhcp"] = "DHCP Client: your PC gets its network address from it.",
        ["Dnscache"] = "DNS Client: resolves website names.",
        ["nsi"] = "Network Store Interface: networking stops without it.",
        ["PlugPlay"] = "Plug and Play: detects hardware.",
        ["Power"] = "Power: power plans and sleep.",
        ["BFE"] = "Base Filtering Engine: the firewall and VPNs depend on it.",
        ["mpssvc"] = "Windows Defender Firewall.",
        ["WinDefend"] = "Microsoft Defender Antivirus: your real-time protection.",
        ["Schedule"] = "Task Scheduler: Windows maintenance and many apps rely on it.",
        ["LSM"] = "Local Session Manager: user sessions.",
        ["SamSs"] = "Security Accounts Manager: sign-in.",
        ["ProfSvc"] = "User Profile Service: loads your profile at sign-in.",
        ["UserManager"] = "User Manager: sign-in and app accounts.",
        ["CryptSvc"] = "Cryptographic Services: Windows Update and driver signing.",
        ["gpsvc"] = "Group Policy Client: policies and some sign-in steps.",
        ["BrokerInfrastructure"] = "Background Tasks Infrastructure: Store apps and notifications.",
        ["SystemEventsBroker"] = "System Events Broker: required by many Windows services.",
        ["TimeBrokerSvc"] = "Time Broker: background tasks.",
        ["CoreMessagingRegistrar"] = "CoreMessaging: the Windows shell and apps.",
        ["StateRepository"] = "State Repository Service: Store apps and the Start menu.",
        ["Wcmsvc"] = "Windows Connection Manager: Wi-Fi and network connections.",
        ["EventSystem"] = "COM+ Event System: used by many services.",
    };

    private static readonly Dictionary<string, string> WarnOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Audiosrv"] = "Windows Audio: you will have no sound until it runs again.",
        ["AudioEndpointBuilder"] = "Windows Audio Endpoint Builder: sound devices disappear without it.",
        ["Themes"] = "Themes: the desktop falls back to the classic look.",
        ["Spooler"] = "Print Spooler: printing stops working.",
        ["wuauserv"] = "Windows Update: security updates will not install.",
        ["WSearch"] = "Windows Search: Start menu and File Explorer search get slower.",
        ["SysMain"] = "SysMain (Superfetch): app pre-loading stops; usually fine on SSDs.",
        ["DiagTrack"] = "Connected User Experiences and Telemetry: diagnostics data stops; safe for most people.",
        ["TrustedInstaller"] = "Windows Modules Installer: updates and optional features need it.",
        ["LanmanWorkstation"] = "Workstation: network shares stop working.",
        ["LanmanServer"] = "Server: sharing your folders stops working.",
        ["Netman"] = "Network Connections: network adapter management.",
        ["WlanSvc"] = "WLAN AutoConfig: Wi-Fi stops working.",
        ["bthserv"] = "Bluetooth Support: Bluetooth devices disconnect.",
    };

    public static IReadOnlyCollection<string> CriticalNames => Critical.Keys;

    public static ServiceProtection Classify(string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return ServiceProtection.None;
        }

        if (Critical.ContainsKey(serviceName))
        {
            return ServiceProtection.Protected;
        }

        return WarnOnly.ContainsKey(serviceName) ? ServiceProtection.Warn : ServiceProtection.None;
    }

    public static bool IsProtected(string? serviceName) => Classify(serviceName) == ServiceProtection.Protected;

    /// <summary>Why the service is protected or worth a warning; null for ordinary services.</summary>
    public static string? Reason(string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return null;
        }

        if (Critical.TryGetValue(serviceName, out var reason))
        {
            return reason;
        }

        return WarnOnly.TryGetValue(serviceName, out var warn) ? warn : null;
    }

    /// <summary>
    /// Whether a start mode change is allowed. Protected services may only be set to an automatic mode.
    /// Returns a message explaining a refusal.
    /// </summary>
    public static (bool Allowed, string? Message) CanSetStartMode(string? serviceName, string startMode)
    {
        var canonical = ServiceStartModes.Normalize(startMode);
        if (canonical is null)
        {
            return (false, $"'{startMode}' is not a start mode PC Checker knows (use Automatic, Automatic (Delayed), Manual or Disabled).");
        }

        bool stopsAutoStart = canonical is ServiceStartModes.Manual or ServiceStartModes.Disabled;
        if (stopsAutoStart && IsProtected(serviceName))
        {
            return (false, $"{serviceName} is a core Windows service and PC Checker will not stop it from starting. {Reason(serviceName)}");
        }

        return (true, Classify(serviceName) == ServiceProtection.Warn && stopsAutoStart ? Reason(serviceName) : null);
    }
}
