namespace PcChecker.Core.Users;

/// <summary>
/// Pure mapping of the Windows session state number (WTS_CONNECTSTATE_CLASS) to a friendly value and label, and the
/// rules about which actions a session allows. Unit-tested; no Windows calls.
/// </summary>
public static class SessionStateMapper
{
    /// <summary>Maps the raw WTS_CONNECTSTATE_CLASS value (0–9) to <see cref="SessionState"/>.</summary>
    public static SessionState FromWts(int wtsState) => wtsState switch
    {
        0 => SessionState.Active,
        1 => SessionState.Connected,
        2 => SessionState.ConnectQuery,
        3 => SessionState.Shadow,
        4 => SessionState.Disconnected,
        5 => SessionState.Idle,
        6 => SessionState.Listen,
        7 => SessionState.Reset,
        8 => SessionState.Down,
        9 => SessionState.Init,
        _ => SessionState.Unknown,
    };

    /// <summary>The plain label shown in the list.</summary>
    public static string Label(SessionState state) => state switch
    {
        SessionState.Active => "Active",
        SessionState.Connected => "Connected",
        SessionState.ConnectQuery => "Connecting",
        SessionState.Shadow => "Shadowing",
        SessionState.Disconnected => "Disconnected",
        SessionState.Idle => "Idle",
        SessionState.Listen => "Listening",
        SessionState.Reset => "Resetting",
        SessionState.Down => "Signing out",
        SessionState.Init => "Starting",
        _ => "Unknown",
    };

    /// <summary>True when the session can be disconnected (it has a user and is not already disconnected or ending).</summary>
    public static bool CanDisconnect(UserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.IsSignedIn && session.State is SessionState.Active or SessionState.Connected or SessionState.Shadow;
    }

    /// <summary>True when the session can be signed off (any session with a signed-in user).</summary>
    public static bool CanSignOff(UserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.IsSignedIn && session.State is not (SessionState.Down or SessionState.Reset);
    }

    /// <summary>True when a message can be sent to the session (it must have a connected user to see it).</summary>
    public static bool CanSendMessage(UserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.IsSignedIn && session.State is SessionState.Active or SessionState.Connected or SessionState.Disconnected;
    }
}
