namespace PcChecker.Core.Users;

/// <summary>The state of a Windows sign-in session, as the OS reports it (WTS_CONNECTSTATE_CLASS).</summary>
public enum SessionState
{
    /// <summary>Signed in and using the PC right now.</summary>
    Active,

    /// <summary>Connected but not the session on screen.</summary>
    Connected,

    /// <summary>Connecting.</summary>
    ConnectQuery,

    /// <summary>Shadowing another session.</summary>
    Shadow,

    /// <summary>Signed in but disconnected (the programs keep running).</summary>
    Disconnected,

    /// <summary>Waiting for someone to sign in.</summary>
    Idle,

    /// <summary>Listening for connections.</summary>
    Listen,

    /// <summary>Being reset.</summary>
    Reset,

    /// <summary>Going down.</summary>
    Down,

    /// <summary>Starting up.</summary>
    Init,

    /// <summary>The state could not be read.</summary>
    Unknown,
}

/// <summary>One signed-in session with the totals of the programs that user is running.</summary>
public sealed record UserSession(
    int SessionId,
    string UserName,
    string? Domain,
    SessionState State,
    string? ClientName,
    DateTimeOffset? LogonTime,
    bool IsCurrent)
{
    /// <summary>Summed processor use of the user's processes (0–100).</summary>
    public double CpuPercent { get; init; }

    /// <summary>Summed memory of the user's processes, in bytes.</summary>
    public long MemoryBytes { get; init; }

    /// <summary>How many processes the user is running.</summary>
    public int ProcessCount { get; init; }

    /// <summary>User shown with the domain when there is one (<c>DOMAIN\\user</c>).</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Domain) ? UserName : $"{Domain}\\{UserName}";

    /// <summary>True when the session has a real signed-in user (not a listening or idle service session).</summary>
    public bool IsSignedIn => !string.IsNullOrWhiteSpace(UserName) && State is not (SessionState.Listen or SessionState.Idle);
}
