namespace PcChecker.Core.Gaming;

/// <summary>
/// What the game profile switcher should do now. Steps run in order: first <see cref="Exit"/> (put things back when
/// <see cref="RevertExit"/> is true), then <see cref="Enter"/> (remember the current state when
/// <see cref="CaptureBaseline"/> is true, then apply the profile).
/// </summary>
public sealed record ProfileSwitchDecision(
    GameProfile? Exit,
    bool RevertExit,
    GameProfile? Enter,
    int? EnterProcessId,
    string? EnterExeName,
    bool CaptureBaseline,
    string Reason)
{
    public static ProfileSwitchDecision None { get; } = new(null, false, null, null, null, false, string.Empty);

    public bool IsNone => Exit is null && Enter is null;
}

/// <summary>
/// The game profile state machine, free of any Windows calls so it can be tested:
/// <list type="bullet">
/// <item>A game with an enabled profile must stay in front for <see cref="Debounce"/> (2 s) before its profile is
/// applied, so alt-tabbing past a game does not thrash fans and power plans.</item>
/// <item>Losing focus never switches back; only the game's process exiting does (with "revert on exit").</item>
/// <item>A second profiled game coming to the front while the first still runs takes over: the first profile is
/// undone (when it reverts on exit) and the second applied. Going back to the first game later re-applies it.</item>
/// <item>Turning auto-switch off, or disabling/deleting the active profile, undoes it at once. Editing or renaming it
/// re-applies it.</item>
/// </list>
/// Not thread-safe: the caller serialises calls.
/// </summary>
public sealed class ProfileSwitchPlanner
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

    private Candidate? _pending;

    public ProfileSwitchPlanner(TimeSpan? debounce = null)
    {
        Debounce = debounce ?? DefaultDebounce;
    }

    public TimeSpan Debounce { get; }

    /// <summary>The applied profile, or null when none is active.</summary>
    public ActiveSession? Active { get; private set; }

    /// <summary>The game waiting out the debounce, if any.</summary>
    public ForegroundApp? PendingApp => _pending?.App;

    /// <summary>A new foreground app was reported (or the current one re-checked).</summary>
    public ProfileSwitchDecision OnForeground(ForegroundApp? app, GamingSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.AutoSwitch || app is null)
        {
            _pending = null;
            return ProfileSwitchDecision.None;
        }

        var profile = GameProfileMatcher.Match(app, settings.Profiles);
        if (profile is null || (settings.OnlyWhenFullscreen && !app.IsFullscreen))
        {
            _pending = null;
            return ProfileSwitchDecision.None;
        }

        if (Active is { } active && active.ProcessId == app.Pid)
        {
            // Back to the game that is already active (profile edits are handled by OnSettingsChanged).
            _pending = null;
            return ProfileSwitchDecision.None;
        }

        if (_pending is null || _pending.App.Pid != app.Pid)
        {
            _pending = new Candidate(app, profile, now);
        }
        else
        {
            _pending = _pending with { App = app, Profile = profile };
        }

        return OnTick(now);
    }

    /// <summary>Time passed: applies a pending game once it has been in front for the debounce time.</summary>
    public ProfileSwitchDecision OnTick(DateTimeOffset now)
    {
        if (_pending is not { } pending || now - pending.FirstSeen < Debounce)
        {
            return ProfileSwitchDecision.None;
        }

        _pending = null;
        var previous = Active;
        string exe = GameProfileRules.CanonicalExeName(string.IsNullOrWhiteSpace(pending.App.ExePath) ? pending.App.ProcessName : pending.App.ExePath);
        Active = new ActiveSession(pending.Profile, pending.App.Pid, exe, now);
        if (previous is null)
        {
            return new ProfileSwitchDecision(null, false, pending.Profile, pending.App.Pid, exe, true,
                $"{exe} has been in front for {Debounce.TotalSeconds:0} s");
        }

        // Hand over from one running game to another. When the first profile is not undone, its state becomes the new baseline.
        bool revert = previous.Profile.RevertOnExit;
        return new ProfileSwitchDecision(previous.Profile, revert, pending.Profile, pending.App.Pid, exe, !revert,
            $"{exe} took over from {previous.ExeName}");
    }

    /// <summary>A watched process ended.</summary>
    public ProfileSwitchDecision OnProcessExited(int processId, DateTimeOffset now)
    {
        if (_pending is { } pending && pending.App.Pid == processId)
        {
            _pending = null;
        }

        if (Active is not { } active || active.ProcessId != processId)
        {
            return ProfileSwitchDecision.None;
        }

        Active = null;
        return new ProfileSwitchDecision(active.Profile, active.Profile.RevertOnExit, null, null, null, false, $"{active.ExeName} closed");
    }

    /// <summary>The profile list or options changed: undo a profile that was switched off, re-apply one that was edited.</summary>
    public ProfileSwitchDecision OnSettingsChanged(GamingSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_pending is { } pending)
        {
            var stillMatches = settings.AutoSwitch ? GameProfileMatcher.Match(pending.App, settings.Profiles) : null;
            _pending = stillMatches is null ? null : pending with { Profile = stillMatches };
        }

        if (Active is not { } active)
        {
            return ProfileSwitchDecision.None;
        }

        if (!settings.AutoSwitch)
        {
            Active = null;
            return new ProfileSwitchDecision(active.Profile, true, null, null, null, false, "automatic switching was turned off");
        }

        // Look the game up again rather than the profile name: a renamed profile still matches its game and is
        // re-applied under its new name, and when the active profile is switched off another enabled profile for the
        // same game takes over (the one the foreground check would pick).
        var winner = GameProfileMatcher.Match(active.ExeName, settings.Profiles);
        if (winner is null)
        {
            Active = null;
            return new ProfileSwitchDecision(active.Profile, true, null, null, null, false, $"the \"{active.Profile.Name}\" profile was turned off or removed");
        }

        if (!SameProfile(active.Profile, winner))
        {
            Active = active with { Profile = winner };
            string reason = string.Equals(active.Profile.Name, winner.Name, StringComparison.OrdinalIgnoreCase)
                ? $"the \"{winner.Name}\" profile was changed"
                : $"the \"{active.Profile.Name}\" profile was renamed or switched off, so \"{winner.Name}\" applies now";
            return new ProfileSwitchDecision(active.Profile, true, winner, active.ProcessId, active.ExeName, false, reason);
        }

        return ProfileSwitchDecision.None;
    }

    /// <summary>PC Checker is closing or the user asked to stop: undo the active profile when it reverts on exit.</summary>
    public ProfileSwitchDecision Deactivate(bool forceRevert = false)
    {
        _pending = null;
        if (Active is not { } active)
        {
            return ProfileSwitchDecision.None;
        }

        Active = null;
        return new ProfileSwitchDecision(active.Profile, forceRevert || active.Profile.RevertOnExit, null, null, null, false, "switching stopped");
    }

    /// <summary>True when two profiles would change exactly the same things for the same game.</summary>
    public static bool SameProfile(GameProfile a, GameProfile b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
        && GameProfileMatcher.NormalizeExeName(a.ExeName) == GameProfileMatcher.NormalizeExeName(b.ExeName)
        && a.Enabled == b.Enabled
        && string.Equals(a.OverlayPresetName, b.OverlayPresetName, StringComparison.OrdinalIgnoreCase)
        && a.ShowOverlay == b.ShowOverlay
        && string.Equals(a.FanProfileName, b.FanProfileName, StringComparison.OrdinalIgnoreCase)
        && a.PowerPlanId == b.PowerPlanId
        && a.RevertOnExit == b.RevertOnExit;

    /// <summary>The profile in effect, the game process it is for and since when.</summary>
    public sealed record ActiveSession(GameProfile Profile, int ProcessId, string ExeName, DateTimeOffset Since);

    private sealed record Candidate(ForegroundApp App, GameProfile Profile, DateTimeOffset FirstSeen);
}
