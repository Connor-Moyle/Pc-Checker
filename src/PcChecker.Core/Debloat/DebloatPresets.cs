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
        "ads.notificationsuggest", "ads.scoobe", "ads.settingsm365", "ads.accountnotifications", "ads.phone", "ads.balloons", "ads.storeopenwith",
        "ads.inkworkspace",
        "privacy.telemetrypolicies", "privacy.appcompat", "privacy.voiceactivation", "privacy.speechmodel", "privacy.msrt",
        "ai.searchhistory",
        "ai.copilot", "ai.recall", "ai.clicktodo", "ai.paint", "ai.notepad", "ai.bing", "ai.searchhighlights", "ai.searchcloud", "ai.cortana",
        "explorer.extensions",
    ];

    public static IReadOnlyList<DebloatPreset> All { get; } =
    [
        new(RecommendedId, "Recommended",
            "Safe privacy settings, no ads or suggestions, AI features and Bing web search off, file extensions shown. A good start for everyone.",
            Recommended, IsRecommended: true),
        new(GamingId, "Gaming",
            "Everything in Recommended plus Game Mode on, no background game recording, no background Store apps, no Sticky Keys prompt and no Game Bar on the controller's Xbox button.",
            [.. Recommended, "gaming.gamedvr", "gaming.gamemode", "privacy.backgroundapps", "prefs.stickykeys", "gaming.gamebarbutton"]),
        new(PrivacyMaxId, "Privacy max",
            "Recommended plus the stricter privacy settings: location, error reports, telemetry tasks, clipboard and settings sync, Find my device, Remote Assistance, Edge AI and the telemetry of Office, browsers and developer tools.",
            [
                .. Recommended, "privacy.backgroundapps", "privacy.recentdocs", "privacy.location", "privacy.wer", "privacy.ceiptasks",
                "privacy.devicemetadata", "privacy.clipboardcloud", "onedrive.nags", "explorer.recent", "explorer.officecloud",
                "privacy.remoteassistance", "privacy.findmydevice", "privacy.settingsync", "privacy.nearbysharing", "privacy.resume",
                "privacy.lockscreencamera", "privacy.passwordreveal", "privacy.arso", "privacy.perm.accountinfo", "edge.ai", "other.office",
                "other.chrome", "other.firefox", "other.devtelemetry", "other.nvidia", "installs.pushtoinstall",
            ]),
        new(MinimalUiId, "Minimal UI",
            "A calmer desktop: fewer taskbar buttons, no Explorer promotions or desktop Spotlight, a shorter classic right-click menu and Explorer opening on This PC.",
            [
                "taskbar.widgets", "taskbar.chat", "taskbar.taskview", "taskbar.searchicon", "taskbar.endtask", "start.morepins",
                "taskbar.news", "taskbar.meetnow", "explorer.extensions", "explorer.thispc", "explorer.classicmenu", "explorer.compact",
                "explorer.recent", "explorer.officecloud", "ads.explorersync", "ads.startrecommended", "start.recentmostused",
                "ads.desktopspotlight", "ads.spotlighticon", "explorer.dupdrives", "explorer.ctx.giveaccess", "explorer.ctx.library",
                "explorer.ctx.cast", "edge.desktopshortcut",
            ]),
        new(ConsumerAppsId, "Remove consumer apps",
            "Removes pre-installed consumer and promotional apps (news, games, social and streaming stubs, retired Microsoft apps). Useful apps such as Photos, Calculator and the Store, and PC-maker apps, are never included.",
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
        new("Set diagnostic data to 0 (Security) or disable DiagTrack",
            "Home and Pro treat 0 as Required anyway, and the PolicyManager copies other tools write are overwritten by Windows. \"Send only required diagnostic data\" does what can be done.",
            [AtlasOS, ReviOS, ShutUp10]),
        new("Turn off the application compatibility engine and Program Compatibility Assistant",
            "Breaks the compatibility fixes older games and programs need to run.",
            [AtlasOS, Optimizer]),
        new("Deny camera, microphone, Bluetooth, USB and other hardware access for all apps",
            "Breaks video calls, peripheral apps and passkey sign-in. Choose these per app in Settings > Privacy & security instead.",
            [ShutUp10, AtlasOS]),
        new("Force S3 sleep instead of Modern Standby",
            "Most current AMD and Intel laptop firmware has no S3, so sleep stops working. Turn off network connectivity in Modern Standby instead.",
            [Optimizer, AtlasOS]),
        new("Stop device encryption or decrypt BitLocker",
            "Leaves your files readable on a lost or stolen PC, and decrypting is a long one-way operation.",
            [AtlasOS, ReviOS]),
        new("Remove GPU and audio control panel apps",
            "These are the AMD, NVIDIA, Intel, Realtek, Dolby and Waves driver control panels; removing them loses display and audio settings. They are on the never-remove list.",
            [Tiny11]),
        new("Turn off all notifications or Windows Update notifications",
            "Hides Windows Security, update and driver warnings. Only the advertising notifications are turned off here.",
            [ShutUp10, AtlasOS]),
        new("Turn off Store app updates and updates for other Microsoft products",
            "Store apps and Office stop receiving security fixes.",
            [ShutUp10, Optimizer]),
        new("Write to MDM (PolicyManager) stores or feature overrides",
            "Undocumented internal stores that Windows and MDM overwrite, so the change cannot be read back or reverted reliably.",
            [WinUtil, AtlasOS, XdAntiSpy]),
    ];

    /// <summary>Settings that PC Checker only points at (no documented policy, or a personal choice).</summary>
    public static IReadOnlyList<DebloatNote> Notes { get; } =
    [
        new("Pause Windows Update",
            "Need a quiet week? Pause updates for up to five weeks in Settings instead of disabling Windows Update.",
            "ms-settings:windowsupdate"),
        new("AI actions in File Explorer and other apps",
            "AI actions in File Explorer's right-click menu still have no documented policy. Turn them off per action in Settings > Apps > Actions.",
            "ms-settings:appsfeatures"),
        new("Personalised ads in your Microsoft account",
            "Ads personalisation tied to your Microsoft account is controlled online, not on this PC.",
            "https://account.microsoft.com/privacy/ad-settings"),
    ];
}
