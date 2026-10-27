using PcChecker.Core.Optimization;
using static PcChecker.Core.Debloat.DebloatSources;

namespace PcChecker.Core.Debloat;

/// <summary>
/// Privacy, ads, AI, taskbar/Start and File Explorer tweaks added after comparing the catalog with current releases of
/// the reference tools (Win11Debloat, WinUtil, O&amp;O ShutUp10++, Sophia Script, Optimizer, AtlasOS, ReviOS, Tiny11
/// and xd-AntiSpy). Most values are documented by Microsoft or set by at least two of those tools; a few long-standing
/// Settings switches and Explorer options come from a single tool and are the same value the Settings app or Folder
/// Options writes (see the Sources column in docs/DEBLOAT.md).
/// </summary>
public static partial class DebloatCatalog
{
    private const string DataCollectionPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    private const string SystemPolicy = @"SOFTWARE\Policies\Microsoft\Windows\System";
    private const string CloudContentMachine = @"SOFTWARE\Policies\Microsoft\Windows\CloudContent";
    private const string CloudContentUser = @"Software\Policies\Microsoft\Windows\CloudContent";
    private const string ExplorerPolicyUser = @"Software\Policies\Microsoft\Windows\Explorer";
    private const string ExplorerPolicyMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string SystemPoliciesMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string SettingSyncPolicy = @"SOFTWARE\Policies\Microsoft\Windows\SettingSync";
    private const string StartKey = @"Software\Microsoft\Windows\CurrentVersion\Start";
    private const string ShellBlocked = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string OneDriveClsid = @"Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}";
    private const string RemovableDrivesDelegate = @"Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\DelegateFolders\{F5FB2C77-0E2F-4A16-A381-3E560C68BC83}";
    private const string EnterpriseOnlyNote = "Microsoft documents this policy for Enterprise and Education; on Home and Pro it may have no effect.";

    /// <summary>App permissions in Settings &gt; Privacy &amp; security that hold personal data (hardware ones are deliberately left out).</summary>
    private static (string Id, string Capability, string Name, string Breaks)[] PersonalDataPermissions =>
    [
        ("accountinfo", "userAccountInformation", "your account info (name, picture)", "Apps can no longer show your name or picture."),
        ("contacts", "contacts", "your contacts", "Mail, People and messaging apps can no longer read your contacts."),
        ("calendar", "appointments", "your calendar", "Calendar and mail apps can no longer read your appointments."),
        ("callhistory", "phoneCallHistory", "your call history", "Phone Link can no longer show calls made from this PC."),
        ("email", "email", "your email", "Store mail apps can no longer read your email."),
        ("messages", "chat", "your text messages", "Phone Link and messaging apps can no longer read or send texts."),
        ("tasks", "userDataTasks", "your tasks", "To Do and calendar apps can no longer read your tasks."),
        ("notifications", "userNotificationListener", "your notifications", "Smartwatch and phone companion apps stop mirroring notifications."),
    ];

    private static IEnumerable<DebloatTweak> MorePrivacyTweaks()
    {
        yield return Tweak("privacy.telemetrypolicies", PrivacyCategory, "Limit what diagnostic data contains",
            "Keeps the device name out of diagnostic data and stops Windows collecting extra diagnostic logs and memory dumps for Microsoft.",
            RiskLevel.Safe, From(ShutUp10, AtlasOS, ReviOS),
            [
                Machine(DataCollectionPolicy, "AllowDeviceNameInTelemetry", 0, null),
                Machine(DataCollectionPolicy, "LimitDiagnosticLogCollection", 1, null),
                Machine(DataCollectionPolicy, "LimitDumpCollection", 1, null),
            ]);

        yield return Tweak("privacy.diagtrackautologger", PrivacyCategory, "Stop the DiagTrack and SQM trace sessions",
            "Stops the two background event-trace sessions that collect telemetry from starting at boot. Takes effect after a restart.",
            RiskLevel.Advanced, From(AtlasOS, ReviOS, Optimizer),
            [
                Machine(@"SYSTEM\CurrentControlSet\Control\WMI\Autologger\Diagtrack-Listener", "Start", 0, 1),
                Machine(@"SYSTEM\CurrentControlSet\Control\WMI\Autologger\SQMLogger", "Start", 0, 1),
            ],
            advancedOnly: true, restart: true);

        yield return Tweak("privacy.appcompat", PrivacyCategory, "Turn off the inventory collector and application telemetry",
            "Windows stops sending an inventory of your apps and drivers, and app-usage telemetry, to Microsoft. Compatibility fixes for old programs keep working.",
            RiskLevel.Safe, From(Optimizer, AtlasOS, ShutUp10),
            [
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "AITEnable", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisableInventory", 1, null),
            ]);

        foreach (var permission in PersonalDataPermissions)
        {
            yield return Tweak("privacy.perm." + permission.Id, PrivacyCategory, $"Don't let apps access {permission.Name}",
                $"Turns off app access to {permission.Name} for every account on this PC (the switch in Settings > Privacy & security).",
                RiskLevel.Moderate, From(ShutUp10, AtlasOS),
                [MachineText(ConsentStore + permission.Capability, "Value", "Deny", "Allow")],
                warning: permission.Breaks);
        }

        yield return Tweak("privacy.perm.genai", PrivacyCategory, "Don't let apps use text and image generation",
            "Turns off app access to the Windows generative AI models (Settings > Privacy & security > Text and image generation).",
            RiskLevel.Moderate, From(ShutUp10),
            [MachineText(ConsentStore + "systemAIModels", "Value", "Deny", "Allow")],
            minBuild: Windows11_24H2,
            warning: "Apps that use Windows' built-in AI models lose those features.")
        with
        {
            // Only change the permission where Windows already created it, so an unknown key is never invented.
            Requires = Needs("This Windows has no text and image generation permission", MachineKey(ConsentStore + "systemAIModels")),
        };

        yield return Tweak("privacy.voiceactivation", PrivacyCategory, "Don't let apps listen for voice activation",
            "Apps can no longer listen for a keyword to start themselves, including above the lock screen.",
            RiskLevel.Safe, From(Optimizer, ReviOS, ShutUp10),
            [
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsActivateWithVoice", 2, null),
                Machine(@"SOFTWARE\Microsoft\Speech_OneCore\Preferences", "VoiceActivationEnableAboveLockscreen", 0, null),
            ]);

        yield return Tweak("privacy.speechmodel", PrivacyCategory, "Turn off automatic speech-model updates",
            "Windows stops downloading updated speech recognition and synthesis models in the background.",
            RiskLevel.Safe, From(AtlasOS, ShutUp10),
            [Machine(@"SOFTWARE\Policies\Microsoft\Speech", "AllowSpeechModelUpdate", 0, null)]);

        yield return Tweak("privacy.settingsync", PrivacyCategory, "Turn off settings sync to your Microsoft account",
            "Windows stops syncing your theme, passwords, language and app settings to your Microsoft account.",
            RiskLevel.Moderate, From(AtlasOS, Optimizer, ShutUp10),
            [
                Machine(SettingSyncPolicy, "DisableSettingSync", 2, null),
                Machine(SettingSyncPolicy, "DisableSettingSyncUserOverride", 1, null),
                Machine(SettingSyncPolicy, "DisableApplicationSettingSync", 2, null),
                Machine(SettingSyncPolicy, "DisableApplicationSettingSyncUserOverride", 1, null),
                Machine(SettingSyncPolicy, "DisableCredentialsSettingSync", 2, null),
                Machine(SettingSyncPolicy, "DisableCredentialsSettingSyncUserOverride", 1, null),
            ],
            warning: "Your other PCs stop receiving this PC's theme, passwords and preferences.");

        yield return Tweak("privacy.nearbysharing", PrivacyCategory, "Turn off Nearby sharing and shared experiences",
            "Turns off Nearby sharing and \"Share across devices\" for your account (the switches in Settings > System > Nearby sharing).",
            RiskLevel.Safe, From(AtlasOS, Optimizer),
            [
                User(@"Software\Microsoft\Windows\CurrentVersion\CDP", "NearShareChannelUserAuthzPolicy", 0, null),
                User(@"Software\Microsoft\Windows\CurrentVersion\CDP", "CdpSessionUserAuthzPolicy", 0, null),
            ]);

        yield return Tweak("privacy.cdppolicy", PrivacyCategory, "Turn off connected device experiences (policy)",
            "Sets the policy that turns off cross-device features for every account.",
            RiskLevel.Advanced, From(AtlasOS, Optimizer),
            [Machine(SystemPolicy, "EnableCdp", 0, null)],
            advancedOnly: true,
            warning: "Phone Link, the cross-device clipboard and Nearby sharing stop working.");

        yield return Tweak("privacy.resume", PrivacyCategory, "Turn off cross-device Resume",
            "Windows stops offering to continue on this PC what you were doing in an app on your phone.",
            RiskLevel.Safe, From(AtlasOS),
            [User(@"Software\Microsoft\Windows\CurrentVersion\CrossDeviceResume\Configuration", "IsResumeAllowed", 0, null)],
            minBuild: Windows11_24H2);

        yield return Tweak("privacy.lockscreencamera", PrivacyCategory, "Turn off the camera on the lock screen",
            "The camera can no longer be opened from the lock screen.",
            RiskLevel.Safe, From(AtlasOS, ShutUp10),
            [Machine(@"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreenCamera", 1, null)]);

        yield return Tweak("privacy.oobe", PrivacyCategory, "Skip the privacy settings screen for new accounts",
            "New accounts on this PC no longer see the privacy choices screen at first sign-in; they get the current defaults.",
            RiskLevel.Advanced, From(XdAntiSpy, AtlasOS, ShutUp10),
            [Machine(@"SOFTWARE\Policies\Microsoft\Windows\OOBE", "DisablePrivacyExperience", 1, null)],
            advancedOnly: true,
            warning: "New accounts are not asked about location, diagnostic data and the other privacy choices.");

        yield return Tweak("privacy.arso", PrivacyCategory, "Don't use sign-in info to finish setup after an update",
            "Windows no longer signs you in automatically after an update restart to finish setting up and reopen your apps.",
            RiskLevel.Safe, From(Sophia, Optimizer),
            [Machine(SystemPoliciesMachine, "DisableAutomaticRestartSignOn", 1, null)]);

        yield return Tweak("privacy.kms", PrivacyCategory, "Turn off KMS client online validation",
            "Stops the Software Protection Platform sending activation data for KMS (volume) licences. Retail and OEM activation are not affected.",
            RiskLevel.Advanced, From(AtlasOS, ReviOS, Optimizer, ShutUp10),
            [Machine(@"SOFTWARE\Policies\Microsoft\Windows NT\CurrentVersion\Software Protection Platform", "NoGenTicket", 1, null)],
            advancedOnly: true);

        yield return Tweak("privacy.passwordreveal", PrivacyCategory, "Hide the password reveal button",
            "Removes the eye button that shows a typed password in Windows password boxes.",
            RiskLevel.Safe, From(ShutUp10),
            [Machine(@"SOFTWARE\Policies\Microsoft\Windows\CredUI", "DisablePasswordReveal", 1, null)]);

        yield return Tweak("privacy.remoteassistance", PrivacyCategory, "Turn off Remote Assistance connections",
            "Nobody can be invited to control this PC with the old Remote Assistance tool. Quick Assist keeps working.",
            RiskLevel.Safe, From(Optimizer, AtlasOS, ShutUp10),
            [Machine(@"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", 0, 1)]);

        yield return Tweak("privacy.findmydevice", PrivacyCategory, "Turn off Find my device",
            "Sets the documented Find My Device policy to off, so this PC's location is no longer saved to your Microsoft account.",
            RiskLevel.Moderate, From(Win11Debloat, Optimizer, ShutUp10),
            [
                Machine(@"SOFTWARE\Policies\Microsoft\FindMyDevice", "AllowFindMyDevice", 0, null),
                Machine(@"SOFTWARE\Microsoft\Settings\FindMyDevice", "LocationSyncEnabled", 0, null),
            ],
            warning: "You can no longer locate or lock this PC from account.microsoft.com if it is lost or stolen.");

        yield return Tweak("privacy.locationservice", PrivacyCategory, "Turn off the Windows location service completely",
            "Sets the location policies to off, disables the Geolocation service (lfsvc) and the location sensor, instead of only blocking apps.",
            RiskLevel.Advanced, From(Win11Debloat, WinUtil, Optimizer),
            [
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocationScripting", 1, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableWindowsLocationProvider", 1, null),
                Machine(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Sensor\Overrides\{BFA794E4-F964-4FDB-90F6-51056BFE4B44}", "SensorPermissionState", 0, null),
                Service("lfsvc", DebloatStartMode.Disabled, DebloatStartMode.Manual),
            ],
            advancedOnly: true,
            warning: "Automatic time zone, Find my device, Maps and weather for your location stop working.");

        yield return Tweak("privacy.msrt", PrivacyCategory, "Don't send infection reports from the Malicious Software Removal Tool",
            "The monthly Malicious Software Removal Tool keeps running and cleaning, but no longer reports what it found to Microsoft.",
            RiskLevel.Safe, From(AtlasOS),
            [Machine(@"SOFTWARE\Policies\Microsoft\MRT", "DontReportInfectionInformation", 1, null)]);

        yield return Tweak("privacy.msaoptional", PrivacyCategory, "Make Microsoft accounts optional for apps",
            "Store apps that support it let you use them without signing in with a Microsoft account.",
            RiskLevel.Safe, From(ReviOS, AtlasOS),
            [Machine(SystemPoliciesMachine, "MSAOptional", 1, null)]);
    }

    private static IEnumerable<DebloatTweak> MoreAdsTweaks() =>
    [
        Tweak("ads.settingsm365", AdsCategory, "Hide Microsoft 365 and account ads in Settings Home",
            "Settings Home stops showing Microsoft 365, OneDrive storage and account-state promotions (CloudContent policy).",
            RiskLevel.Safe, From(Win11Debloat, Tiny11, ShutUp10),
            [Machine(CloudContentMachine, "DisableConsumerAccountStateContent", 1, null)], minBuild: Windows11_23H2),

        Tweak("ads.accountnotifications", AdsCategory, "Turn off account notifications in Settings",
            "Settings stops showing Microsoft account reminders and offers (the switch in Settings > Privacy & security > General).",
            RiskLevel.Safe, From(Win11Debloat, AtlasOS, ShutUp10),
            [User(@"Software\Microsoft\Windows\CurrentVersion\SystemSettings\AccountNotifications", "EnableAccountNotifications", 0, null)],
            minBuild: Windows11_22H2),

        Tweak("ads.phone", AdsCategory, "Turn off phone suggestions and the mobile device pane in Start",
            "Windows stops suggesting you link a phone, and Start stops showing the Phone Link pane.",
            RiskLevel.Safe, From(Win11Debloat, Optimizer, ShutUp10),
            [
                User(@"Software\Microsoft\Windows\CurrentVersion\Mobility", "OptedIn", 0, null),
                User(StartKey + @"\Companions\Microsoft.YourPhone_8wekyb3d8bbwe", "IsEnabled", 0, null),
            ],
            minBuild: Windows11_22H2, explorer: true),

        Tweak("ads.phonepolicy", AdsCategory, "Turn off phone-to-PC linking (policy)",
            "Sets the policy that turns off linking a phone to this PC for every account.",
            RiskLevel.Advanced, From(Win11Debloat, ShutUp10),
            [Machine(SystemPolicy, "EnableMmx", 0, null)],
            advancedOnly: true,
            warning: "Phone Link can no longer connect your phone to this PC."),

        Tweak("ads.cloudoptimized", AdsCategory, "Turn off cloud-optimized content (policy)",
            "The CloudContent policy that stops Windows downloading cloud-optimized content such as suggested pins and promotions.",
            RiskLevel.Advanced, From(AtlasOS, ReviOS, Tiny11, Optimizer),
            [Machine(CloudContentMachine, "DisableCloudOptimizedContent", 1, null)],
            advancedOnly: true, warning: EnterpriseOnlyNote),

        Tweak("ads.spotlightthirdparty", AdsCategory, "Don't suggest third-party content in Windows Spotlight (policy)",
            "Windows Spotlight on the lock screen and in Windows stops suggesting apps and content from other companies.",
            RiskLevel.Advanced, From(AtlasOS, ReviOS),
            [User(CloudContentUser, "DisableThirdPartySuggestions", 1, null)],
            advancedOnly: true),

        Tweak("ads.desktopspotlight", AdsCategory, "Turn off Windows Spotlight on the desktop",
            "The desktop background stops being replaced by daily Bing pictures with promotional links.",
            RiskLevel.Safe, From(Win11Debloat, ShutUp10, AtlasOS),
            [User(CloudContentUser, "DisableSpotlightCollectionOnDesktop", 1, null)],
            minBuild: Windows11_22H2, explorer: true),

        Tweak("ads.spotlighticon", AdsCategory, "Hide the \"Learn about this picture\" desktop icon",
            "Removes the Spotlight icon that links to Bing from the desktop.",
            RiskLevel.Safe, From(Win11Debloat, ShutUp10),
            [User(ExplorerKey + @"\HideDesktopIcons\NewStartPanel", "{2cc5ca98-6485-489a-920e-b3e88a6ccce3}", 1, null)],
            minBuild: Windows11_22H2, explorer: true),

        Tweak("ads.inkworkspace", AdsCategory, "Turn off suggested apps in the pen menu",
            "Windows Ink Workspace and the pen menu stop suggesting apps from the Store. Pen features keep working.",
            RiskLevel.Safe, From(AtlasOS, Optimizer),
            [
                User(@"Software\Microsoft\Windows\CurrentVersion\PenWorkspace", "PenWorkspaceAppSuggestionsEnabled", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\WindowsInkWorkspace", "AllowSuggestedAppsInWindowsInkWorkspace", 0, null),
            ]),

        Tweak("ads.balloons", AdsCategory, "Turn off feature advertisement notifications",
            "Windows stops showing balloon notifications that advertise its features.",
            RiskLevel.Safe, From(ReviOS),
            [User(ExplorerPolicyUser, "NoBalloonFeatureAdvertisements", 1, null)]),

        Tweak("ads.storeopenwith", AdsCategory, "Don't offer to look for an app in the Microsoft Store",
            "Opening an unknown file type no longer offers to search the Store or the internet for an app.",
            RiskLevel.Safe, From(Sophia, AtlasOS, ReviOS),
            [User(ExplorerPolicyUser, "NoUseStoreOpenWith", 1, null), Machine(ExplorerPolicyMachine, "NoInternetOpenWith", 1, null)]),
    ];

    private static IEnumerable<DebloatTweak> MoreAiTweaks() =>
    [
        Tweak("ai.searchhistory", AiCategory, "Turn off device search history and search location",
            "Start search stops keeping a history of your searches on this PC and stops using your location.",
            RiskLevel.Safe, From(AtlasOS, ReviOS, ShutUp10),
            [
                User(SearchSettings, "IsDeviceSearchHistoryEnabled", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowSearchToUseLocation", 0, null),
            ]),

        Tweak("ai.copilotpolicy", AiCategory, "Uninstall Copilot by policy",
            "Sets Microsoft's Remove Microsoft Copilot app policy. Windows uninstalls the app itself when its conditions are met, and stops when you revert.",
            RiskLevel.Advanced, From(WinUtil, Win11Debloat),
            [Machine(WindowsAiMachine, "RemoveMicrosoftCopilotApp", 1, null)],
            advancedOnly: true, minBuild: Windows11_24H2,
            warning: "Only honoured on Enterprise and Education, and only when Microsoft 365 Copilot is also installed and Copilot was not used for 28 days. Use \"Remove the Copilot app\" on other PCs."),

        Tweak("ai.settingsagent", AiCategory, "Turn off the AI agent in Settings search",
            "Settings search stops using an AI model to answer natural-language questions; normal search keeps working.",
            RiskLevel.Advanced, From(ShutUp10),
            [Machine(WindowsAiMachine, "DisableSettingsAgent", 1, null)],
            advancedOnly: true, minBuild: Windows11_24H2, warning: EnterpriseOnlyNote),

        Tweak("ai.fabricservice", AiCategory, "Stop the Windows AI Fabric service starting automatically",
            "Sets the Windows AI Fabric service (WSAIFabricSvc) to manual, so it only starts when an AI feature needs it.",
            RiskLevel.Advanced, From(Win11Debloat, WinUtil),
            [Service("WSAIFabricSvc", DebloatStartMode.Manual, DebloatStartMode.Automatic)],
            advancedOnly: true, minBuild: Windows11_24H2),
    ];

    private static IEnumerable<DebloatTweak> MoreTaskbarTweaks()
    {
        yield return Tweak("start.recentmostused", TaskbarCategory, "Hide recently added and most used apps in Start",
            "Start stops listing apps you installed recently and the apps you use most (the switches in Settings > Personalization > Start).",
            RiskLevel.Safe, From(Sophia, XdAntiSpy),
            [User(StartKey, "ShowRecentList", 0, null), User(StartKey, "ShowFrequentList", 0, null)],
            minBuild: Windows11_24H2, explorer: true);

        yield return Tweak("start.hiderecommended", TaskbarCategory, "Hide the Recommended section in Start (policy)",
            "Sets the Remove Recommended section policy for your account, so Start shows only pinned apps.",
            RiskLevel.Advanced, From(Win11Debloat, WinUtil, Sophia),
            [User(ExplorerPolicyUser, "HideRecommendedSection", 1, null)],
            advancedOnly: true, minBuild: Windows11_22H2, explorer: true,
            warning: "Only Windows 11 builds that support this policy honour it (it started on SE and Education). Elsewhere nothing changes.");

        foreach (var (id, mode, title, text) in new[]
                 {
                     ("category", 0, "Show All apps in Start by category", "The All apps list in the new Start menu groups apps by category (the default)."),
                     ("grid", 1, "Show All apps in Start as a grid", "The All apps list in the new Start menu shows a grid of icons."),
                     ("list", 2, "Show All apps in Start as a list", "The All apps list in the new Start menu shows an alphabetical list."),
                 })
        {
            yield return Tweak("start.allapps." + id, TaskbarCategory, title, text,
                RiskLevel.Safe, From(Sophia, Win11Debloat),
                [User(StartKey, "AllAppsViewMode", mode, null)],
                minBuild: Windows11_24H2, explorer: true,
                warning: "Only the redesigned Start menu (recent 24H2 and 25H2 updates) has this setting.");
        }

        foreach (var (id, level, title) in new[] { ("always", 0, "Always combine taskbar buttons"), ("whenfull", 1, "Combine taskbar buttons only when the taskbar is full"), ("never", 2, "Never combine taskbar buttons") })
        {
            yield return Tweak("taskbar.combine." + id, TaskbarCategory, title,
                "Sets how buttons of the same app are grouped on the main taskbar, with labels when they are not combined.",
                RiskLevel.Safe, From(Win11Debloat, Sophia),
                [ExplorerFlag("TaskbarGlomLevel", level, null)], explorer: true);
        }

        foreach (var (id, level, title) in new[] { ("whenfull", 1, "On other displays, combine buttons only when full"), ("never", 2, "On other displays, never combine buttons") })
        {
            yield return Tweak("taskbar.combineother." + id, TaskbarCategory, title,
                "Sets how app buttons are grouped on the taskbars of your other monitors.",
                RiskLevel.Safe, From(Win11Debloat, Sophia),
                [ExplorerFlag("MMTaskbarGlomLevel", level, null)], explorer: true);
        }

        foreach (var (id, mode, title, text) in new[]
                 {
                     ("all", 0, "Show app buttons on all taskbars", "With several monitors, every taskbar shows all open apps (the default)."),
                     ("mainandactive", 1, "Show app buttons on the main taskbar and where the window is open", "With several monitors, the main taskbar shows all apps and the others only their own windows."),
                     ("activeonly", 2, "Show app buttons only on the taskbar where the window is open", "With several monitors, each taskbar shows only the windows on its own monitor."),
                 })
        {
            yield return Tweak("taskbar.multimonitor." + id, TaskbarCategory, title, text,
                RiskLevel.Safe, From(Win11Debloat, Sophia),
                [ExplorerFlag("MMTaskbarMode", mode, null)], explorer: true);
        }

        yield return Tweak("taskbar.lastactiveclick", TaskbarCategory, "Clicking a grouped app opens its last active window",
            "Clicking an app with several windows on the taskbar switches to the window you used last instead of showing thumbnails.",
            RiskLevel.Safe, From(Win11Debloat),
            [ExplorerFlag("LastActiveClick", 1, null)], explorer: true);

        yield return Tweak("taskbar.clocknotify", TaskbarCategory, "Show the clock in the notification centre",
            "The notification centre shows a large clock above the calendar.",
            RiskLevel.Safe, From(Sophia),
            [ExplorerFlag("ShowClockInNotificationCenter", 1, null)], minBuild: Windows11_24H2, explorer: true);
    }

    private static IEnumerable<DebloatTweak> MoreExplorerTweaks()
    {
        yield return Tweak("explorer.onedrivenav", ExplorerCategory, "Hide OneDrive in the navigation pane",
            "Removes OneDrive from the left side of File Explorer. OneDrive keeps syncing and its folder stays reachable from your user folder.",
            RiskLevel.Safe, From(Win11Debloat),
            [User(OneDriveClsid, "System.IsPinnedToNameSpaceTree", 0, 1)],
            explorer: true)
        with
        {
            Requires = Needs("OneDrive is not set up for this account", UserKey(OneDriveClsid)),
        };

        yield return Tweak("explorer.dupdrives", ExplorerCategory, "Hide duplicate removable drives in the navigation pane",
            "USB sticks and SD cards show once under This PC instead of a second time on their own.",
            RiskLevel.Safe, From(Win11Debloat, AtlasOS),
            [
                new RegistryKeyRemovalOp(DebloatHive.LocalMachine, @"SOFTWARE\" + RemovableDrivesDelegate, "Removable Drives"),
                new RegistryKeyRemovalOp(DebloatHive.LocalMachine, @"SOFTWARE\WOW6432Node\" + RemovableDrivesDelegate, "Removable Drives"),
            ],
            explorer: true);

        foreach (var (id, value, place) in new[] { ("downloads", 3, "Downloads"), ("onedrive", 4, "OneDrive"), ("home", 2, "Home") })
        {
            yield return Tweak("explorer.launch." + id, ExplorerCategory, $"Open File Explorer to {place}",
                $"File Explorer opens on {place} instead of the page it opens on now.",
                RiskLevel.Safe, From(Win11Debloat, Sophia),
                [ExplorerFlag("LaunchTo", value, null)]);
        }

        foreach (var (id, clsid, entry, tools) in new[]
                 {
                     ("giveaccess", "{f81e9010-6ea4-11ce-a7ff-00aa003ca9f6}", "Give access to", From(Win11Debloat, AtlasOS)),
                     ("library", "{3dad6c5d-2167-4cae-9914-f99e41c12cfa}", "Include in library", From(Win11Debloat, AtlasOS)),
                     ("share", "{e2bf9676-5f8f-435c-97eb-11607a5bedf7}", "Share", From(Win11Debloat, AtlasOS)),
                     ("cast", "{7AD84985-87B4-4a16-BE58-8B72A5B390F7}", "Cast to device", From(Optimizer, AtlasOS)),
                     ("compat", "{1d27f844-3a1f-4410-85ac-14651078412d}", "Troubleshoot compatibility", From(AtlasOS, Win11Debloat)),
                 })
        {
            yield return Tweak("explorer.ctx." + id, ExplorerCategory, $"Remove \"{entry}\" from the right-click menu",
                $"Blocks the \"{entry}\" entry of the classic right-click menu through Windows' shell extension block list, which Revert simply clears.",
                RiskLevel.Safe, tools,
                [MachineText(ShellBlocked, clsid, string.Empty, null)], explorer: true);
        }

        yield return Tweak("explorer.checkboxes", ExplorerCategory, "Hide item check boxes",
            "File Explorer stops showing a check box next to files and folders when you select them.",
            RiskLevel.Safe, From(Sophia),
            [ExplorerFlag("AutoCheckSelect", 0, null)]);

        yield return Tweak("explorer.aeroshake", ExplorerCategory, "Turn on Aero Shake",
            "Shaking a window by its title bar minimises all other windows again.",
            RiskLevel.Safe, From(Sophia),
            [ExplorerFlag("DisallowShaking", 0, null)]);

        yield return Tweak("explorer.navexpand", ExplorerCategory, "Expand the navigation pane to the open folder",
            "The left pane of File Explorer follows the folder you are in.",
            RiskLevel.Safe, From(Sophia),
            [ExplorerFlag("NavPaneExpandToCurrentFolder", 1, null)]);

        yield return Tweak("explorer.restorefolders", ExplorerCategory, "Reopen your folder windows at sign-in",
            "File Explorer windows that were open when you signed out open again.",
            RiskLevel.Safe, From(Sophia),
            [ExplorerFlag("PersistBrowsers", 1, null)]);

        yield return Tweak("explorer.mergeconflicts", ExplorerCategory, "Show folder merge conflicts",
            "Asks before merging a copied folder into a folder with the same name.",
            RiskLevel.Safe, From(Sophia),
            [ExplorerFlag("HideMergeConflicts", 0, null)]);

        yield return Tweak("explorer.copydetails", ExplorerCategory, "Always show copy and move details",
            "The copy dialog opens with its speed graph and details expanded.",
            RiskLevel.Safe, From(Sophia),
            [User(ExplorerKey + @"\OperationStatusManager", "EnthusiastMode", 1, null)]);

        yield return Tweak("explorer.shortcutsuffix", ExplorerCategory, "Don't add \" - Shortcut\" to new shortcuts",
            "New shortcuts get the plain name of what they point to.",
            RiskLevel.Safe, From(Sophia, AtlasOS),
            [UserText(ExplorerKey + @"\NamingTemplates", "ShortcutNameTemplate", "%s.lnk", null)]);

        yield return Tweak("explorer.driveletters.first", ExplorerCategory, "Show drive letters before drive names",
            "This PC shows \"(C:) Windows\" instead of \"Windows (C:)\".",
            RiskLevel.Safe, From(Win11Debloat),
            [User(ExplorerKey, "ShowDriveLettersFirst", 4, null)]);

        yield return Tweak("explorer.driveletters.hide", ExplorerCategory, "Hide drive letters",
            "This PC shows drive names without their letters.",
            RiskLevel.Safe, From(Win11Debloat),
            [User(ExplorerKey, "ShowDriveLettersFirst", 2, null)]);

        yield return Tweak("explorer.noresolve", ExplorerCategory, "Don't search for the target of broken shortcuts",
            "Opening a broken shortcut no longer makes Windows search your drives for a file with the same name.",
            RiskLevel.Safe, From(AtlasOS),
            [User(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoResolveSearch", 1, null)]);
    }
}
