namespace PcChecker.Core.Gaming;

/// <summary>
/// What PC Checker changes while one game is running: overlay layout and visibility, fan profile and power plan.
/// Stored in <see cref="GamingSettings.Profiles"/>. Null fields mean "leave as it is".
/// </summary>
public sealed class GameProfile
{
    /// <summary>Friendly name shown in the list, e.g. "Elden Ring".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The game's executable file name, e.g. <c>eldenring.exe</c>. Matching ignores case and folders.</summary>
    public string ExeName { get; set; } = string.Empty;

    /// <summary>Full path the profile was created from (informational only; matching uses <see cref="ExeName"/>).</summary>
    public string? ExePath { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Overlay preset to load while the game runs (built-in or saved), or null to keep the current layout.</summary>
    public string? OverlayPresetName { get; set; }

    /// <summary>Show the overlay while the game runs (false hides it).</summary>
    public bool ShowOverlay { get; set; } = true;

    /// <summary>Fan profile to apply while the game runs, or null to leave the fans alone.</summary>
    public string? FanProfileName { get; set; }

    /// <summary>Windows power plan to switch to while the game runs, or null to leave it.</summary>
    public Guid? PowerPlanId { get; set; }

    /// <summary>Put everything back the way it was when the game closes.</summary>
    public bool RevertOnExit { get; set; } = true;

    /// <summary>Deep copy, so editors can work on a copy without touching the stored profile.</summary>
    public GameProfile Clone() => new()
    {
        Name = Name,
        ExeName = ExeName,
        ExePath = ExePath,
        Enabled = Enabled,
        OverlayPresetName = OverlayPresetName,
        ShowOverlay = ShowOverlay,
        FanProfileName = FanProfileName,
        PowerPlanId = PowerPlanId,
        RevertOnExit = RevertOnExit,
    };
}

/// <summary>Game profile settings stored in <c>AppSettings.Gaming</c>.</summary>
public sealed class GamingSettings
{
    public List<GameProfile> Profiles { get; set; } = [];

    /// <summary>Switch profiles automatically when a game with a profile comes to the front.</summary>
    public bool AutoSwitch { get; set; } = true;

    /// <summary>Only switch when the game window covers its whole monitor (full screen or borderless).</summary>
    public bool OnlyWhenFullscreen { get; set; }
}

/// <summary>The application whose window is in front.</summary>
public sealed record ForegroundApp(int Pid, string ProcessName, string? ExePath, string WindowTitle, bool IsFullscreen);

/// <summary>One frame-rate reading for the tracked process.</summary>
public sealed record FrameRateSample(int ProcessId, string ProcessName, double Fps, double FrameTimeMs, double? OnePercentLowFps, DateTimeOffset Timestamp);

/// <summary>A running program with a visible window, offered by "Add running game".</summary>
public sealed record RunningApp(int Pid, string ProcessName, string? ExePath, string WindowTitle, double CpuPercent, double? GpuPercent, long WorkingSetBytes)
{
    /// <summary>Executable file name, e.g. "eldenring.exe".</summary>
    public string ExeName => GameProfileMatcher.ExeFileName(ExePath) ?? GameProfileMatcher.NormalizeExeName(ProcessName) + ".exe";
}

/// <summary>The profile the switcher currently has applied.</summary>
public sealed record ActiveGameProfile(string ProfileName, string ExeName, int ProcessId, DateTimeOffset Since);
