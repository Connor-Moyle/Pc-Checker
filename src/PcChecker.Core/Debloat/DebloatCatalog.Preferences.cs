using PcChecker.Core.Optimization;
using static PcChecker.Core.Debloat.DebloatSources;

namespace PcChecker.Core.Debloat;

/// <summary>
/// Windows preferences, other apps (browsers, Office, developer tools, GPU control panels), gaming, Windows Update,
/// service and automatic-install tweaks. Tweaks for other software only apply when that software is installed.
/// </summary>
public static partial class DebloatCatalog
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string WindowsSearchPolicy = @"SOFTWARE\Policies\Microsoft\Windows\Windows Search";
    private const string PhotoViewerAssociations = @"SOFTWARE\Microsoft\Windows Photo Viewer\Capabilities\FileAssociations";
    private const string ModernStandbyNetworkPolicy = @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\f15576e8-98b7-4186-b944-eafa664402d9";
    private const string EdgeUpdatePolicy = @"SOFTWARE\Policies\Microsoft\EdgeUpdate";
    private const string BravePolicy = @"SOFTWARE\Policies\BraveSoftware\Brave";
    private const string ChromePolicy = @"SOFTWARE\Policies\Google\Chrome";
    private const string FirefoxPolicy = @"SOFTWARE\Policies\Mozilla\Firefox";
    private const string MachineEnvironment = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string UpdateUxSettings = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    /// <summary>Turns Reserved Storage off, but only with at least 30 GB free so feature updates still have room.</summary>
    internal const string ReservedStorageDisableScript =
        "$ErrorActionPreference = 'Stop'; " +
        "$disk = Get-CimInstance -ClassName Win32_LogicalDisk -Filter \"DeviceID='$env:SystemDrive'\"; " +
        "if ($disk -and $disk.FreeSpace -lt 30GB) { [Console]::Error.WriteLine('Less than 30 GB is free on the system drive, so Reserved Storage stays on to give updates room.'); exit 2 }; " +
        "Set-WindowsReservedStorageState -State Disabled";

    internal const string ReservedStorageEnableScript =
        "$ErrorActionPreference = 'Stop'; Set-WindowsReservedStorageState -State Enabled";

    /// <summary>Prints True when Reserved Storage is off (the state is an enum name, the same in every language).</summary>
    internal const string ReservedStorageDisabledDetectScript =
        "$ErrorActionPreference = 'Stop'; [string](Get-WindowsReservedStorageState).ReservedStorageState -eq 'Disabled'";

    private static IEnumerable<DebloatTweak> PreferenceTweaks()
    {
        yield return Tweak("prefs.autoplay", PreferencesCategory, "Turn off AutoPlay for all media and devices",
            "Inserting a USB stick, memory card or phone no longer opens a prompt or starts a program by itself.",
            RiskLevel.Safe, From(Sophia, AtlasOS),
            [User(ExplorerKey + @"\AutoplayHandlers", "DisableAutoplay", 1, 0)]);

        yield return Tweak("prefs.stickykeys", PreferencesCategory, "Turn off the Sticky Keys shortcut (press Shift five times)",
            "Pressing Shift five times (or holding it) no longer opens the Sticky Keys and Filter Keys prompts, which interrupt games.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia, Optimizer, AtlasOS),
            [
                UserText(@"Control Panel\Accessibility\StickyKeys", "Flags", "506", "510"),
                UserText(@"Control Panel\Accessibility\Keyboard Response", "Flags", "122", "126"),
                UserText(@"Control Panel\Accessibility\ToggleKeys", "Flags", "58", "62"),
            ],
            warning: "Uses the default options without the shortcuts. If you use Sticky, Filter or Toggle Keys, turn them on again in Settings > Accessibility > Keyboard.");

        yield return Tweak("prefs.snapassist", PreferencesCategory, "Turn off Snap Assist suggestions and the Snap layouts flyout",
            "Snapping a window no longer suggests other windows, and hovering the maximise button no longer shows layouts. Win+arrow snapping still works.",
            RiskLevel.Safe, From(Win11Debloat, Sophia, XdAntiSpy),
            [ExplorerFlag("SnapAssist", 0, null), ExplorerFlag("EnableSnapBar", 0, null), ExplorerFlag("EnableSnapAssistFlyout", 0, null)],
            minBuild: Windows11, explorer: true);

        yield return Tweak("prefs.snapoff", PreferencesCategory, "Turn off window snapping",
            "Dragging a window to a screen edge no longer resizes it. Takes effect after you sign out and back in.",
            RiskLevel.Safe, From(Win11Debloat, WinUtil),
            [UserText(@"Control Panel\Desktop", "WindowArrangementActive", "0", "1")]);

        foreach (var (id, filter, title) in new[]
                 {
                     ("hide", 3, "Don't show browser tabs in Alt+Tab"),
                     ("three", 2, "Show the 3 latest browser tabs in Alt+Tab"),
                     ("five", 1, "Show the 5 latest browser tabs in Alt+Tab"),
                     ("twenty", 0, "Show the 20 latest browser tabs in Alt+Tab"),
                 })
        {
            yield return Tweak("prefs.alttab." + id, PreferencesCategory, title,
                "Sets whether Microsoft Edge tabs appear as separate entries when you press Alt+Tab.",
                RiskLevel.Safe, From(Win11Debloat),
                [ExplorerFlag("MultiTaskingAltTabFilter", filter, null)]);
        }

        yield return Tweak("prefs.dark", PreferencesCategory, "Use dark mode for Windows and apps",
            "Switches Windows and apps that follow it to the dark theme. Some apps switch after they are reopened.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia, Optimizer),
            [User(PersonalizeKey, "AppsUseLightTheme", 0, null), User(PersonalizeKey, "SystemUsesLightTheme", 0, null)],
            explorer: true);

        yield return Tweak("prefs.light", PreferencesCategory, "Use light mode for Windows and apps",
            "Switches Windows and apps that follow it to the light theme (the Windows default).",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia),
            [User(PersonalizeKey, "AppsUseLightTheme", 1, null), User(PersonalizeKey, "SystemUsesLightTheme", 1, null)],
            explorer: true);

        yield return Tweak("prefs.bsod", PreferencesCategory, "Show details on the blue screen",
            "A stop error shows its technical parameters, which helps when looking up the cause (for example a GPU or chipset driver).",
            RiskLevel.Safe, From(WinUtil, Sophia),
            [Machine(@"SYSTEM\CurrentControlSet\Control\CrashControl", "DisplayParameters", 1, null)], restart: true);

        yield return Tweak("prefs.verbosestatus", PreferencesCategory, "Show detailed status messages at sign-in and shutdown",
            "Instead of \"Please wait\", Windows shows what it is doing while starting, signing in and shutting down.",
            RiskLevel.Safe, From(WinUtil, XdAntiSpy, AtlasOS),
            [Machine(SystemPoliciesMachine, "VerboseStatus", 1, null)]);

        yield return Tweak("prefs.batterypercent", PreferencesCategory, "Show the battery percentage on the taskbar",
            "Laptops show the remaining charge as a number next to the battery icon.",
            RiskLevel.Safe, From(WinUtil),
            [ExplorerFlag("IsBatteryPercentageEnabled", 1, null)], minBuild: Windows11_24H2, explorer: true);

        yield return Tweak("prefs.numlock", PreferencesCategory, "Turn Num Lock on at startup",
            "Num Lock is on at the sign-in screen and after you sign in.",
            RiskLevel.Safe, From(WinUtil, Sophia),
            [
                DefaultUserText(@"Control Panel\Keyboard", "InitialKeyboardIndicators", "2147483650", "2147483648"),
                UserText(@"Control Panel\Keyboard", "InitialKeyboardIndicators", "2147483650", null),
            ]);

        yield return Tweak("prefs.scrollbars", PreferencesCategory, "Always show scrollbars",
            "Scrollbars in Settings and Store apps stay visible instead of shrinking to a thin line.",
            RiskLevel.Safe, From(WinUtil),
            [User(@"Control Panel\Accessibility", "DynamicScrollbars", 0, null)]);

        yield return Tweak("prefs.logonblur", PreferencesCategory, "No blur on the sign-in screen background",
            "The sign-in screen shows your lock screen picture sharp instead of blurred.",
            RiskLevel.Safe, From(WinUtil, AtlasOS),
            [Machine(@"SOFTWARE\Policies\Microsoft\Windows\System", "DisableAcrylicBackgroundOnLogon", 1, null)]);

        yield return Tweak("prefs.defaultprinter", PreferencesCategory, "Don't let Windows manage the default printer",
            "Your default printer stays the one you chose instead of the one you used last.",
            RiskLevel.Safe, From(Sophia),
            [User(@"Software\Microsoft\Windows NT\CurrentVersion\Windows", "LegacyDefaultPrinterMode", 1, null)]);

        yield return Tweak("prefs.jpegquality", PreferencesCategory, "Use full quality for JPEG wallpapers",
            "Windows stops recompressing JPEG pictures you set as the desktop background.",
            RiskLevel.Safe, From(Sophia, AtlasOS),
            [User(@"Control Panel\Desktop", "JPEGImportQuality", 100, null)]);

        yield return Tweak("prefs.dynamiclighting", PreferencesCategory, "Turn off Windows Dynamic Lighting",
            "Windows stops controlling RGB lighting, so vendor software (Armoury Crate, iCUE, AMD and ASUS lighting tools) keeps control without conflicts.",
            RiskLevel.Safe, From(AtlasOS),
            [User(@"Software\Microsoft\Lighting", "AmbientLightingEnabled", 0, null)], minBuild: Windows11_23H2);

        yield return Tweak("prefs.indexbattery", PreferencesCategory, "Don't index on battery",
            "Search indexing pauses on battery and follows the power mode, a gentler alternative to turning indexing off.",
            RiskLevel.Safe, From(ReviOS, AtlasOS),
            [
                Machine(WindowsSearchPolicy, "PreventIndexOnBattery", 1, null),
                Machine(@"SOFTWARE\Microsoft\Windows Search\Gather\Windows\SystemIndex", "RespectPowerModes", 1, null),
            ]);

        yield return Tweak("prefs.modernstandbynet", PreferencesCategory, "Turn off network connectivity in Modern Standby",
            "A sleeping laptop no longer stays online for mail and updates, which stops it draining the battery in a bag. Only affects PCs with Modern Standby, which includes most current AMD Ryzen and Intel laptops.",
            RiskLevel.Safe, From(Win11Debloat, WinUtil),
            [Machine(ModernStandbyNetworkPolicy, "ACSettingIndex", 0, null), Machine(ModernStandbyNetworkPolicy, "DCSettingIndex", 0, null)]);

        yield return Tweak("prefs.photoviewer", PreferencesCategory, "Offer Windows Photo Viewer for pictures",
            "Adds JPEG, PNG, BMP and GIF to the classic Windows Photo Viewer, so pictures still open after removing the Photos app.",
            RiskLevel.Safe, From(Optimizer, ReviOS),
            [
                .. new[] { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif" }
                    .Select(ext => MachineText(PhotoViewerAssociations, ext, "PhotoViewer.FileAssoc.Tiff", null)),
            ]);
    }

    private static IEnumerable<DebloatTweak> MoreEdgeTweaks() =>
    [
        Tweak("edge.ai", EdgeCategory, "Turn off Copilot and AI features in Microsoft Edge",
            "Edge stops letting Copilot read the page, hides the Microsoft 365 Copilot button, and turns off AI history search, AI writing help, Copilot on the new tab page and the on-device AI model.",
            RiskLevel.Safe, From(Win11Debloat, ShutUp10, Optimizer),
            [
                Machine(EdgePolicy, "CopilotPageContext", 0, null),
                Machine(EdgePolicy, "CopilotCDPPageContext", 0, null),
                Machine(EdgePolicy, "EdgeEntraCopilotPageContext", 0, null),
                Machine(EdgePolicy, "Microsoft365CopilotChatIconEnabled", 0, null),
                Machine(EdgePolicy, "EdgeHistoryAISearchEnabled", 0, null),
                Machine(EdgePolicy, "ComposeInlineEnabled", 0, null),
                Machine(EdgePolicy, "NewTabPageBingChatEnabled", 0, null),
                Machine(EdgePolicy, "GenAILocalFoundationalModelSettings", 1, null),
            ],
            warning: EdgeManagedNote),

        Tweak("edge.desktopshortcut", EdgeCategory, "Stop Edge updates putting a shortcut on the desktop",
            "Edge updates no longer recreate the Microsoft Edge desktop shortcut (Stable, Beta, Dev and Canary).",
            RiskLevel.Safe, From(WinUtil, Sophia),
            [
                Machine(EdgeUpdatePolicy, "CreateDesktopShortcutDefault", 0, null),
                Machine(EdgeUpdatePolicy, "CreateDesktopShortcut{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}", 0, null),
                Machine(EdgeUpdatePolicy, "CreateDesktopShortcut{2CD8A007-E189-409D-A2C8-9AF4EF3C72AA}", 0, null),
                Machine(EdgeUpdatePolicy, "CreateDesktopShortcut{0D50BFEC-CD6A-4F9A-964C-C7416E3ACB10}", 0, null),
                Machine(EdgeUpdatePolicy, "CreateDesktopShortcut{65C35B14-6C1D-4122-AC46-7148CC9D6497}", 0, null),
            ]),
    ];

    private static IEnumerable<DebloatTweak> OtherAppTweaks() =>
    [
        Tweak("other.office", OtherAppsCategory, "Turn off Microsoft Office telemetry and feedback",
            "Sets Office's policies so it sends no diagnostic data, joins no customer experience programme and shows no feedback or survey prompts, and disables Office's telemetry agent tasks.",
            RiskLevel.Safe, From(AtlasOS, Optimizer, ShutUp10),
            [
                User(@"Software\Policies\Microsoft\office\16.0\common", "sendcustomerdata", 0, null),
                User(@"Software\Policies\Microsoft\office\16.0\common", "qmenable", 0, null),
                User(@"Software\Policies\Microsoft\office\common\clienttelemetry", "sendtelemetry", 3, null),
                User(@"Software\Policies\Microsoft\office\16.0\common\feedback", "enabled", 0, null),
                User(@"Software\Policies\Microsoft\office\16.0\common\feedback", "surveyenabled", 0, null),
                DisableTask(@"\Microsoft\Office\OfficeTelemetryAgentLogOn2016"),
                DisableTask(@"\Microsoft\Office\OfficeTelemetryAgentFallBack2016"),
            ])
        with
        {
            Requires = Needs("Microsoft Office is not installed",
                MachineKey(@"SOFTWARE\Microsoft\Office\ClickToRun"), MachineKey(@"SOFTWARE\Microsoft\Office\16.0"), MachineKey(@"SOFTWARE\WOW6432Node\Microsoft\Office\16.0")),
        },

        Tweak("other.newoutlook", OtherAppsCategory, "Hide \"Try the new Outlook\" in classic Outlook",
            "Classic Outlook stops showing the new Outlook switch and Microsoft stops migrating you to it automatically.",
            RiskLevel.Safe, From(WinUtil, ShutUp10),
            [
                User(@"Software\Microsoft\Office\16.0\Outlook\Options\General", "HideNewOutlookToggle", 1, null),
                User(@"Software\Policies\Microsoft\Office\16.0\Outlook\Options\General", "DoNewOutlookAutoMigration", 0, null),
                User(@"Software\Policies\Microsoft\Office\16.0\Outlook\Preferences", "NewOutlookMigrationUserSetting", 0, null),
            ])
        with { Requires = Needs("Classic Outlook is not installed", AppPath("OUTLOOK.EXE")) },

        Tweak("other.brave", OtherAppsCategory, "Turn off Brave Rewards, Wallet, VPN, Leo AI, News, Talk and usage pings",
            "Sets Brave's documented policies. Brave's Tor windows are left alone.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat),
            [
                Machine(BravePolicy, "BraveRewardsDisabled", 1, null),
                Machine(BravePolicy, "BraveWalletDisabled", 1, null),
                Machine(BravePolicy, "BraveVPNDisabled", 1, null),
                Machine(BravePolicy, "BraveAIChatEnabled", 0, null),
                Machine(BravePolicy, "BraveNewsDisabled", 1, null),
                Machine(BravePolicy, "BraveTalkDisabled", 1, null),
                Machine(BravePolicy, "BraveStatsPingEnabled", 0, null),
                Machine(BravePolicy, "BraveP3AEnabled", 0, null),
                Machine(BravePolicy, "MetricsReportingEnabled", 0, null),
            ],
            warning: "Brave shows \"Managed by your organization\" while any policy is set. Revert removes them again.")
        with { Requires = Needs("Brave is not installed", AppPath("brave.exe")) },

        Tweak("other.chrome", OtherAppsCategory, "Turn off AI features and usage reporting in Google Chrome",
            "Sets Chrome's documented policies to turn off Gemini, AI Mode, Help me write, tab compare, AI history search, AI themes, DevTools AI and the on-device AI model, plus usage statistics and feedback.",
            RiskLevel.Safe, From(Win11Debloat, Optimizer),
            [
                Machine(ChromePolicy, "GeminiSettings", 1, null),
                Machine(ChromePolicy, "AIModeSettings", 1, null),
                Machine(ChromePolicy, "HelpMeWriteSettings", 2, null),
                Machine(ChromePolicy, "TabCompareSettings", 2, null),
                Machine(ChromePolicy, "HistorySearchSettings", 2, null),
                Machine(ChromePolicy, "CreateThemesSettings", 2, null),
                Machine(ChromePolicy, "DevToolsGenAiSettings", 2, null),
                Machine(ChromePolicy, "GenAILocalFoundationalModelSettings", 1, null),
                Machine(ChromePolicy, "MetricsReportingEnabled", 0, null),
                Machine(ChromePolicy, "UserFeedbackAllowed", 0, null),
            ],
            warning: "Chrome shows \"Managed by your organization\" while any policy is set. Revert removes them again.")
        with { Requires = Needs("Google Chrome is not installed", AppPath("chrome.exe")) },

        Tweak("other.firefox", OtherAppsCategory, "Turn off Firefox telemetry and the Default Browser Agent",
            "Sets Firefox's enterprise policies so it sends no telemetry and its background agent stops reporting your default browser.",
            RiskLevel.Safe, From(Optimizer),
            [Machine(FirefoxPolicy, "DisableTelemetry", 1, null), Machine(FirefoxPolicy, "DisableDefaultBrowserAgent", 1, null)])
        with { Requires = Needs("Firefox is not installed", AppPath("firefox.exe")) },

        Tweak("other.devtelemetry", OtherAppsCategory, "Opt out of developer tool telemetry",
            "Sets the PowerShell 7 and .NET SDK opt-out variables for every account and turns off Visual Studio telemetry and its feedback dialog. Programs see the variables after you sign in again.",
            RiskLevel.Safe, From(WinUtil, AtlasOS, Optimizer),
            [
                MachineText(MachineEnvironment, "POWERSHELL_TELEMETRY_OPTOUT", "1", null),
                MachineText(MachineEnvironment, "DOTNET_CLI_TELEMETRY_OPTOUT", "1", null),
                User(@"Software\Microsoft\VisualStudio\Telemetry", "TurnOffSwitch", 1, null),
                Machine(@"SOFTWARE\Policies\Microsoft\VisualStudio\SQM", "OptIn", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\VisualStudio\Feedback", "DisableFeedbackDialog", 1, null),
            ]),

        Tweak("other.nvidia", OtherAppsCategory, "Turn off NVIDIA Control Panel usage data",
            "Opts out of the NVIDIA Control Panel's usage data collection. Drivers, G-SYNC and game settings are not touched.",
            RiskLevel.Safe, From(AtlasOS, ReviOS, Optimizer),
            [User(@"Software\NVIDIA Corporation\NVControlPanel2\Client", "OptInOrOutPreference", 0, null)])
        with
        {
            Requires = Needs("No NVIDIA graphics driver is installed",
                MachineKey(@"SOFTWARE\NVIDIA Corporation\Global"), UserKey(@"Software\NVIDIA Corporation\NVControlPanel2")),
        },
    ];

    private static IEnumerable<DebloatTweak> MoreGamingTweaks() =>
    [
        Tweak("gaming.dvrpolicy", GamingCategory, "Turn off Game DVR for every account (policy)",
            "Sets the policy that turns off game recording and broadcasting for all accounts, including new ones.",
            RiskLevel.Advanced, From(Win11Debloat, ShutUp10),
            [Machine(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, null)],
            advancedOnly: true,
            warning: "Game Bar's capture and recording features stop working for everyone."),

        Tweak("gaming.gamebarbutton", GamingCategory, "Don't open Game Bar with the controller Xbox button",
            "Pressing the Xbox button on a controller no longer opens Game Bar, and Game Bar stops showing tips when a game starts.",
            RiskLevel.Safe, From(Win11Debloat, Sophia),
            [User(GameBarKey, "UseNexusForGameBarEnabled", 0, null), User(GameBarKey, "ShowStartupPanel", 0, null)]),

        Tweak("gaming.gamebarprotocol", GamingCategory, "Stop \"ms-gamebar\" pop-ups after removing Xbox Game Bar",
            "Registers a do-nothing handler for the ms-gamebar links games still open, so Windows stops asking for an app to open them.",
            RiskLevel.Moderate, From(Win11Debloat),
            [
                .. GameBarLinkOps("ms-gamebar"),
                .. GameBarLinkOps("ms-gamebarservices"),
            ])
        with { OnlyWithoutApp = "Microsoft.XboxGamingOverlay" },
    ];

    /// <summary>The keys of an URL protocol that opens the harmless systray.exe; Revert deletes the keys it created.</summary>
    private static IEnumerable<RegistryValueOp> GameBarLinkOps(string protocol)
    {
        string key = @"SOFTWARE\Classes\" + protocol;
        yield return new RegistryValueOp(DebloatHive.LocalMachine, key, string.Empty, DebloatValueKind.String, "URL:" + protocol, null) { DeleteKeyOnRevert = key };
        yield return new RegistryValueOp(DebloatHive.LocalMachine, key, "URL Protocol", DebloatValueKind.String, string.Empty, null) { DeleteKeyOnRevert = key };
        yield return new RegistryValueOp(DebloatHive.LocalMachine, key, "NoOpenWith", DebloatValueKind.String, string.Empty, null) { DeleteKeyOnRevert = key };
        yield return new RegistryValueOp(DebloatHive.LocalMachine, key + @"\shell\open\command", string.Empty, DebloatValueKind.ExpandString,
            @"%SystemRoot%\System32\systray.exe", null) { DeleteKeyOnRevert = key };
    }

    private static IEnumerable<DebloatTweak> MoreUpdateTweaks() =>
    [
        Tweak("wu.latestasap", UpdateCategory, "Don't get updates as soon as they're available",
            "Turns off \"Get the latest updates as soon as they're available\" and optional preview updates. Security updates are not affected.",
            RiskLevel.Advanced, From(Win11Debloat, Sophia, ShutUp10),
            [Machine(UpdateUxSettings, "IsContinuousInnovationOptedIn", 0, null), Machine(UpdatePolicy, "AllowOptionalContent", 0, null)],
            advancedOnly: true),

        Tweak("wu.restartprefs", UpdateCategory, "Notify before update restarts and no expedited restarts",
            "Windows tells you when a restart is needed and stops restarting as soon as possible to finish updates.",
            RiskLevel.Advanced, From(Sophia),
            [Machine(UpdateUxSettings, "RestartNotificationsAllowed2", 1, null), Machine(UpdateUxSettings, "IsExpedited", 0, null)],
            advancedOnly: true),

        Tweak("wu.dohttponly", UpdateCategory, "Download updates only from Microsoft (no peer sharing)",
            "Delivery Optimization downloads only from Microsoft's servers and shares nothing with other PCs, not even at home.",
            RiskLevel.Advanced, From(WinUtil, Win11Debloat, Sophia),
            [Machine(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, null)],
            advancedOnly: true),

        Tweak("wu.reservedstorage", UpdateCategory, "Turn off Reserved Storage",
            "Frees the space (about 7 GB) Windows keeps for updates. Only done when at least 30 GB is free on the system drive.",
            RiskLevel.Advanced, From(WinUtil, Sophia, AtlasOS, Tiny11),
            [
                new PowerShellOp("reservedstorage", "Turn off Reserved Storage (Set-WindowsReservedStorageState -State Disabled)",
                    ReservedStorageDisableScript, ReservedStorageEnableScript, ReservedStorageDisabledDetectScript, null),
            ],
            advancedOnly: true,
            warning: "Updates can fail when the disk fills up later. Windows refuses the change while an update is pending; try again after restarting."),
    ];

    private static IEnumerable<DebloatTweak> MoreServiceTweaks() =>
    [
        Tweak("svc.offlinefiles", ServicesCategory, "Disable the Offline Files service",
            "Safe unless a work or school network makes your folders available offline.",
            RiskLevel.Advanced, From(WinUtil),
            [Service("CscService", DebloatStartMode.Disabled, DebloatStartMode.Manual)], advancedOnly: true),

        Tweak("svc.diaghub", ServicesCategory, "Disable the Diagnostics Hub collector service",
            "Only Visual Studio's profiler uses this service.",
            RiskLevel.Advanced, From(Optimizer, AtlasOS, ReviOS),
            [Service("diagnosticshub.standardcollector.service", DebloatStartMode.Disabled, DebloatStartMode.Manual)], advancedOnly: true,
            warning: "Developers who profile apps in Visual Studio need it; revert before profiling."),
    ];

    private static IEnumerable<DebloatTweak> InstallTweaks() =>
    [
        Tweak("installs.pushtoinstall", InstallsCategory, "Turn off remote app installs from the Store website",
            "Apps chosen on the Microsoft Store website can no longer be installed on this PC remotely.",
            RiskLevel.Safe, From(Tiny11),
            [Machine(@"SOFTWARE\Policies\Microsoft\PushToInstall", "DisablePushToInstall", 1, null)]),

        Tweak("installs.archiving", InstallsCategory, "Turn off automatic archiving of unused apps",
            "Windows stops removing Store apps you haven't used for a while (and downloading them again when you open them).",
            RiskLevel.Safe, From(AtlasOS),
            [Machine(@"SOFTWARE\Policies\Microsoft\Windows\Appx", "AllowAutomaticAppArchiving", 0, null)]),

        Tweak("installs.chat", InstallsCategory, "Stop the Chat (Teams) app reinstalling itself",
            "Sets the Chat icon policy to off and tells Windows not to reinstall the personal Teams app after updates (Windows 11 22H2 and 23H2).",
            RiskLevel.Advanced, From(AtlasOS, Tiny11),
            [
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\Windows Chat", "ChatIcon", 3, null),
                Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Communications", "ConfigureChatAutoInstall", 0, null),
            ],
            advancedOnly: true, minBuild: Windows11,
            warning: "Some builds protect the Communications key; then only the Chat icon policy is set and the result says so."),

        Tweak("installs.wpbt", InstallsCategory, "Block software that the PC's firmware installs (WPBT)",
            "Stops the firmware dropping PC-maker installers (such as ASUS Armoury Crate or Lenovo tools) into Windows after every clean install, on AMD and Intel boards alike. Takes effect after a restart.",
            RiskLevel.Advanced, From(WinUtil, AtlasOS),
            [Machine(@"SYSTEM\CurrentControlSet\Control\Session Manager", "DisableWpbtExecution", 1, null)],
            advancedOnly: true, restart: true,
            warning: "Also blocks firmware anti-theft agents (for example Absolute). Install your board's drivers and tools yourself."),

        Tweak("installs.coinstallers", InstallsCategory, "Stop devices installing their vendor apps automatically",
            "Plugging in a mouse, keyboard or headset no longer installs the maker's app (for example Razer Synapse) through device co-installers.",
            RiskLevel.Advanced, From(WinUtil),
            [Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Device Installer", "DisableCoInstallers", 1, null)],
            advancedOnly: true,
            warning: "A few older devices need their co-installer to work fully; revert if a new device misbehaves."),
    ];
}
