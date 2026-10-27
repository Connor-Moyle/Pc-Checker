namespace PcChecker.Core.Services;

/// <summary>
/// Pure mapping between the Windows service start types and the friendly names, in both directions, including the
/// delayed auto-start flag that is stored separately from the start type. No Windows calls, so it is unit-tested.
/// </summary>
public static class ServiceStartupMapping
{
    // Win32 SERVICE_*_START values (dwStartType).
    public const int BootStart = 0;
    public const int SystemStart = 1;
    public const int AutoStart = 2;
    public const int DemandStart = 3;
    public const int Disabled = 4;

    /// <summary>The start types a user can choose on the Services page, in the order the console shows them.</summary>
    public static IReadOnlyList<ServiceStartupType> Selectable { get; } =
    [
        ServiceStartupType.Automatic,
        ServiceStartupType.AutomaticDelayed,
        ServiceStartupType.Manual,
        ServiceStartupType.Disabled,
    ];

    /// <summary>Maps a Win32 start type plus the delayed-auto-start flag to the friendly value.</summary>
    public static ServiceStartupType FromWin32(int dwStartType, bool delayedAutoStart) => dwStartType switch
    {
        BootStart => ServiceStartupType.Boot,
        SystemStart => ServiceStartupType.System,
        AutoStart => delayedAutoStart ? ServiceStartupType.AutomaticDelayed : ServiceStartupType.Automatic,
        DemandStart => ServiceStartupType.Manual,
        Disabled => ServiceStartupType.Disabled,
        _ => ServiceStartupType.Unknown,
    };

    /// <summary>
    /// The Win32 start type and delayed flag for a chosen value, or null when the value cannot be set (Boot, System,
    /// Unknown). ChangeServiceConfig takes the start type; the delayed flag goes through ChangeServiceConfig2.
    /// </summary>
    public static (int StartType, bool Delayed)? ToWin32(ServiceStartupType type) => type switch
    {
        ServiceStartupType.Automatic => (AutoStart, false),
        ServiceStartupType.AutomaticDelayed => (AutoStart, true),
        ServiceStartupType.Manual => (DemandStart, false),
        ServiceStartupType.Disabled => (Disabled, false),
        _ => null,
    };

    /// <summary>True when the start type is one the app is allowed to set.</summary>
    public static bool IsSettable(ServiceStartupType type) => ToWin32(type) is not null;

    /// <summary>True for a start type that starts the service with Windows (with or without a delay).</summary>
    public static bool IsAutomatic(ServiceStartupType type) =>
        type is ServiceStartupType.Automatic or ServiceStartupType.AutomaticDelayed;

    /// <summary>The plain label shown in the list and the drop-down.</summary>
    public static string Label(ServiceStartupType type) => type switch
    {
        ServiceStartupType.Automatic => "Automatic",
        ServiceStartupType.AutomaticDelayed => "Automatic (Delayed)",
        ServiceStartupType.Manual => "Manual",
        ServiceStartupType.Disabled => "Disabled",
        ServiceStartupType.Boot => "Boot",
        ServiceStartupType.System => "System",
        _ => "Unknown",
    };

    /// <summary>Parses a friendly label back to the value; null when it is not one we recognise.</summary>
    public static ServiceStartupType? Parse(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        return label.Trim().ToLowerInvariant() switch
        {
            "automatic" or "auto" => ServiceStartupType.Automatic,
            "automatic (delayed)" or "automatic (delayed start)" or "auto (delayed)" or "delayed-auto" or "delayed" => ServiceStartupType.AutomaticDelayed,
            "manual" or "demand" => ServiceStartupType.Manual,
            "disabled" => ServiceStartupType.Disabled,
            "boot" => ServiceStartupType.Boot,
            "system" => ServiceStartupType.System,
            _ => null,
        };
    }

    // Win32 SERVICE_STATUS dwCurrentState values.
    public const int Stopped = 1;
    public const int StartPending = 2;
    public const int StopPending = 3;
    public const int Running = 4;
    public const int ContinuePending = 5;
    public const int PausePending = 6;
    public const int Paused = 7;

    /// <summary>Maps a Win32 current-state value to the friendly run state.</summary>
    public static ServiceRunState StateFromWin32(int dwCurrentState) => dwCurrentState switch
    {
        Stopped => ServiceRunState.Stopped,
        StartPending => ServiceRunState.StartPending,
        StopPending => ServiceRunState.StopPending,
        Running => ServiceRunState.Running,
        ContinuePending => ServiceRunState.ContinuePending,
        PausePending => ServiceRunState.PausePending,
        Paused => ServiceRunState.Paused,
        _ => ServiceRunState.Unknown,
    };

    /// <summary>The plain label for a run state.</summary>
    public static string StateLabel(ServiceRunState state) => state switch
    {
        ServiceRunState.Stopped => "Stopped",
        ServiceRunState.StartPending => "Starting…",
        ServiceRunState.StopPending => "Stopping…",
        ServiceRunState.Running => "Running",
        ServiceRunState.ContinuePending => "Resuming…",
        ServiceRunState.PausePending => "Pausing…",
        ServiceRunState.Paused => "Paused",
        _ => "Unknown",
    };
}
