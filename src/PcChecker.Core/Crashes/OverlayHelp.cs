using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>How to turn one overlay off: the steps in its own settings and, when it has one, a link that opens the app.</summary>
public sealed record OverlayHelpEntry(string Owner, string Steps, string? OpenUri);

/// <summary>
/// Where each overlay is turned off. PC Checker never closes or changes another app: it explains the setting and, when
/// the app has a link for it, opens the app so the user can switch it off. Pure.
/// </summary>
public static class OverlayHelp
{
    public static IReadOnlyList<OverlayHelpEntry> All { get; } =
    [
        new(T("Discord overlay"), T("In Discord: User Settings → Game Overlay → turn off \"Enable in-game overlay\" (or only for this game under Activity Settings → Registered Games)."), "discord://-/settings/game-overlay"),
        new(T("Steam overlay"), T("In Steam: Settings → In Game → turn off \"Enable the Steam Overlay while in-game\" (or per game: right-click the game → Properties → General)."), "steam://open/settings"),
        new(T("RivaTuner Statistics Server (MSI Afterburner overlay)"), T("Open RivaTuner Statistics Server from the taskbar corner and switch \"Show On-Screen Display\" off, or close MSI Afterburner and RTSS while testing."), null),
        new(T("OBS Studio game capture"), T("In OBS, remove or disable the Game Capture source (use Display Capture instead), or close OBS while testing."), null),
        new(T("NVIDIA overlay / ShadowPlay capture"), T("In the NVIDIA app: Settings → Features → turn off the NVIDIA Overlay (Alt+Z)."), null),
        new(T("Epic Games overlay"), T("The Epic overlay comes with the game's online services; update the game, or start it without the overlay where the game offers that."), null),
        new(T("EA app in-game overlay"), T("In the EA app: Settings → Application → turn off \"In-game overlay\"."), null),
        new(T("Ubisoft Connect overlay"), T("In Ubisoft Connect: Settings → General → turn off \"Enable in-game overlay for supported games\"."), null),
        new(T("Overwolf overlay"), T("In Overwolf: Settings → Overlay & Hotkeys → turn the overlay off for this game, or close Overwolf while testing."), null),
        new(T("Xbox Game Bar"), T("In Windows Settings → Gaming → Xbox Game Bar, turn it off as a test."), "ms-settings:gaming-gamebar"),
    ];

    /// <summary>General steps when the overlay is not in the list.</summary>
    public static string General => T("Turn off in-game overlays one at a time (Discord, Steam, NVIDIA, MSI Afterburner/RTSS, OBS, Xbox Game Bar) and play again to find the one that crashes.");

    /// <summary>The help for an overlay by its owner name (from <see cref="FaultingModuleCatalog"/>), or null.</summary>
    public static OverlayHelpEntry? Find(string? owner) =>
        string.IsNullOrWhiteSpace(owner) ? null : All.FirstOrDefault(o => string.Equals(o.Owner, owner, StringComparison.OrdinalIgnoreCase));
}
