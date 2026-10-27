using static PcChecker.Core.Debloat.DebloatSources;

namespace PcChecker.Core.Debloat;

/// <summary>Built-in presets, the deliberately unsafe list and the report-only notes. Pure data; covered by tests.</summary>
public static class DebloatPresets
{
    public const string RecommendedId = "recommended";
    public const string GamingId = "gaming";
    public const string PrivacyMaxId = "privacy-max";
    public const string MinimalUiId = "minimal-ui";
    public const string ConsumerAppsId = "consumer-apps";
    public const string CustomId = "custom";

    private static readonly string[] Recommended =
    [
        "privacy.telemetry", "privacy.advertisingid", "privacy.tailored", "privacy.feedback", "privacy.activity", "privacy.apptracking",
        "privacy.inking", "privacy.speech", "privacy.appdiagnostics", "privacy.langlist", "privacy.handwriting",
        "ads.start", "ads.lockscreen", "ads.settings", "ads.tips", "ads.silentinstall", "ads.explorersync", "ads.startrecommended",
        "ads.notificationsuggest", "ads.scoobe",
        "ai.copilot", "ai.recall", "ai.clicktodo", "ai.paint", "ai.notepad", "ai.bing", "ai.searchhighlights", "ai.searchcloud", "ai.cortana",
        "explorer.extensions",
    ];

    public static IReadOnlyList<DebloatPreset> All { get; } =
    [
        new(RecommendedId, "Recommended",
            "Safe privacy settings, no ads or suggestions, AI features and Bing web search off, file extensions shown. A good start for everyone.",
            Recommended, IsRecommended: true),
        new(GamingId, "Gaming",
            "Everything in Recommended plus Game Mode on, no background game recording and no background Store apps.",
            [.. Recommended, "gaming.gamedvr", "gaming.gamemode", "privacy.backgroundapps"]),
        new(PrivacyMaxId, "Privacy max",
            "Recommended plus the stricter privacy settings: location, error reports, telemetry tasks, clipboard sync and recent-file history.",
            [
                .. Recommended, "privacy.backgroundapps", "privacy.recentdocs", "privacy.location", "privacy.wer", "privacy.ceiptasks",
                "privacy.devicemetadata", "privacy.clipboardcloud", "onedrive.nags", "explorer.recent", "explorer.officecloud",
            ]),
        new(MinimalUiId, "Minimal UI",
            "A calmer desktop: fewer taskbar buttons, no Explorer promotions, classic right-click menu and Explorer opening on This PC.",
            [
                "taskbar.widgets", "taskbar.chat", "taskbar.taskview", "taskbar.searchicon", "taskbar.endtask", "start.morepins",
                "taskbar.news", "taskbar.meetnow", "explorer.extensions", "explorer.thispc", "explorer.classicmenu", "explorer.compact",
                "explorer.recent", "explorer.officecloud", "ads.explorersync", "ads.startrecommended",
            ]),
        new(ConsumerAppsId, "Remove consumer apps",
            "Removes pre-installed consumer and promotional apps (news, games, social and streaming stubs). Useful apps such as Photos, Calculator and the Store are never included.",
            [.. DebloatCatalog.PreselectedAppIds, "ai.copilotapp"]),
        new(CustomId, "Custom", "Clear all ticks and choose tweaks yourself.", []),
    ];

    public static DebloatPreset? Find(string? id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Popular debloat steps PC Checker deliberately does not offer, with the reason shown in the UI and docs.</summary>
    public static IReadOnlyList<DebloatUnsafeItem> NotOffered { get; } =
    [
        new("Turn off Microsoft Defender",
            "Leaves the PC without real-time malware protection, and Windows turns it back on anyway. Installing another antivirus makes Defender step aside safely.",
            [Optimizer, AtlasOS, DebloatWindows10]),
        new("Disable Windows Update",
            "Security fixes stop arriving. Use the Windows Update options here (delay feature updates, no forced restarts) or pause updates in Settings instead.",
            [WinUtil, Optimizer, DebloatWindows10]),
        new("Remove Microsoft Edge",
            "Edge provides the WebView2 runtime many apps and Windows features need; outside the EEA it cannot be removed cleanly and comes back with updates.",
            [WinUtil, AtlasOS, ReviOS]),
        new("Remove the Microsoft Store",
            "Without it you cannot install or update Store apps, App Installer (winget) or the photo and video codecs.",
            [DebloatWindows10, ReviOS]),
        new("Delete services or strip Windows components",
            "Removed components break Windows Update, feature updates and repair tools, and can only be restored by reinstalling Windows.",
            [AtlasOS, ReviOS]),
        new("Turn off User Account Control",
            "Every program would run with full administrator rights, so malware could change anything without asking.",
            [Optimizer, ReviOS]),
        new("Block Microsoft servers in the hosts file",
            "Breaks Windows Update, activation, the Store and Defender signature updates in ways that are hard to diagnose.",
            [DebloatWindows10, Privatezilla]),
        new("Turn off CPU security mitigations (Spectre/Meltdown)",
            "A small speed gain in exchange for real, exploitable security holes.",
            [AtlasOS, ReviOS]),
        new("Turn off SmartScreen",
            "Removes the warning before you run a known-malicious download or visit a phishing site.",
            [Optimizer, ShutUp10]),
    ];

    /// <summary>Settings that PC Checker only points at (no documented policy, or a personal choice).</summary>
    public static IReadOnlyList<DebloatNote> Notes { get; } =
    [
        new("Find my device",
            "Lets you locate a lost laptop through your Microsoft account using its location. Decide for yourself whether you want it on.",
            "ms-settings:findmydevice"),
        new("Pause Windows Update",
            "Need a quiet week? Pause updates for up to five weeks in Settings instead of disabling Windows Update.",
            "ms-settings:windowsupdate"),
        new("AI actions in File Explorer and other apps",
            "Some AI features (such as AI actions in File Explorer's right-click menu) have no documented policy yet. Review them in Settings.",
            "ms-settings:privacy"),
        new("Personalised ads in your Microsoft account",
            "Ads personalisation tied to your Microsoft account is controlled online, not on this PC.",
            "https://account.microsoft.com/privacy/ad-settings"),
    ];
}
