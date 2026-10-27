namespace PcChecker.Core.Services;

/// <summary>How a Windows service starts, in the words the Services console uses.</summary>
public enum ServiceStartupType
{
    /// <summary>Starts automatically with Windows.</summary>
    Automatic,

    /// <summary>Starts automatically a short while after sign-in, so start-up is smoother.</summary>
    AutomaticDelayed,

    /// <summary>Starts only when something needs it.</summary>
    Manual,

    /// <summary>Never starts.</summary>
    Disabled,

    /// <summary>A driver loaded by the boot loader (shown, never changed from here).</summary>
    Boot,

    /// <summary>A driver loaded during kernel start-up (shown, never changed from here).</summary>
    System,

    /// <summary>The start type could not be read.</summary>
    Unknown,
}

/// <summary>The running state of a service.</summary>
public enum ServiceRunState
{
    Stopped,
    StartPending,
    StopPending,
    Running,
    ContinuePending,
    PausePending,
    Paused,
    Unknown,
}

/// <summary>The filters the Services page offers above the list.</summary>
public enum ServiceFilterKind
{
    All,
    Running,
    Stopped,
    ThirdPartyOnly,
    Disabled,
}

/// <summary>One Windows service with everything the Services page shows.</summary>
public sealed record ServiceDetails(
    string Name,
    string DisplayName,
    string? Description,
    ServiceRunState State,
    ServiceStartupType StartupType,
    string? LogOnAs,
    int? Pid,
    string? Group,
    string? BinaryPath,
    bool IsMicrosoft,
    bool CanStop,
    bool CanPauseContinue)
{
    /// <summary>True while the service is running (or pausing/continuing).</summary>
    public bool IsRunning => State is ServiceRunState.Running or ServiceRunState.ContinuePending or ServiceRunState.PausePending or ServiceRunState.Paused;

    /// <summary>A short, plain description used when the service has none.</summary>
    public string DescriptionOrFallback => string.IsNullOrWhiteSpace(Description)
        ? "No description provided by this service."
        : Description!;
}

/// <summary>Which services a service depends on, and which services depend on it.</summary>
public sealed record ServiceDependencies(
    string Name,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> RequiredBy)
{
    public static ServiceDependencies Empty(string name) => new(name, [], []);

    public bool HasAny => DependsOn.Count > 0 || RequiredBy.Count > 0;
}
