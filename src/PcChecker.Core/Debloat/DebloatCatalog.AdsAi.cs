using PcChecker.Core.Optimization;
using static PcChecker.Core.Debloat.DebloatSources;

namespace PcChecker.Core.Debloat;

public static partial class DebloatCatalog
{
    private const string WindowsAiUser = @"Software\Policies\Microsoft\Windows\WindowsAI";
    private const string WindowsAiMachine = @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI";
    private const string SearchSettings = @"Software\Microsoft\Windows\CurrentVersion\SearchSettings";

    private static IEnumerable<DebloatTweak> AdsTweaks() =>
    [
        Tweak("ads.start", AdsCategory, "Turn off app suggestions in Start",
            "Start stops showing \"suggested\" apps and promoted Store content.",
            RiskLevel.Safe, From(Win11Debloat, ShutUp10, Sophia, Privatezilla, DebloatWindows10, Optimizer),
            [ContentFlagOff("SystemPaneSuggestionsEnabled"), ContentFlagOff("SubscribedContent-338388Enabled")]),

        Tweak("ads.lockscreen", AdsCategory, "Turn off fun facts and tips on the lock screen",
            "The lock screen stops showing tips, tricks and promotional text over the picture.",
            RiskLevel.Safe, From(Win11Debloat, ShutUp10, Sophia, Privatezilla),
            [ContentFlagOff("RotatingLockScreenOverlayEnabled"), ContentFlagOff("SubscribedContent-338387Enabled")]),

        Tweak("ads.settings", AdsCategory, "Turn off suggested content in Settings",
            "The Settings app stops showing suggested content and offers.",
            RiskLevel.Safe, From(Win11Debloat, ShutUp10, Sophia),
            [ContentFlagOff("SubscribedContent-338393Enabled"), ContentFlagOff("SubscribedContent-353694Enabled"), ContentFlagOff("SubscribedContent-353696Enabled")]),

        Linked("ads.tips", AdsCategory, "Turn off tips, tricks and the welcome experience",
            "No more \"Get tips and suggestions\" notifications or the welcome screen after updates. Same change as the Optimize page's notification action.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, ShutUp10, Sophia, Privatezilla), "notifications.focus"),

        Tweak("ads.silentinstall", AdsCategory, "Stop automatic installs of suggested apps",
            "Windows stops silently installing promoted apps (games, social apps) for your account.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia, Privatezilla, DebloatWindows10, Optimizer),
            [
                ContentFlagOff("SilentInstalledAppsEnabled"), ContentFlagOff("OemPreInstalledAppsEnabled"),
                ContentFlagOff("PreInstalledAppsEnabled"), ContentFlagOff("PreInstalledAppsEverEnabled"),
            ]),

        Tweak("ads.explorersync", AdsCategory, "Turn off sync provider ads in File Explorer",
            "File Explorer stops showing OneDrive and Microsoft 365 promotions at the top of the window.",
            RiskLevel.Safe, From(Win11Debloat, ShutUp10, Sophia),
            [ExplorerFlag("ShowSyncProviderNotifications", 0, null)], explorer: true),

        Tweak("ads.startrecommended", AdsCategory, "Turn off promotions and account notices in Start",
            "Removes app promotions from Start's Recommended section and the Microsoft account badges on your profile picture.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia),
            [ExplorerFlag("Start_IrisRecommendations", 0, null), ExplorerFlag("Start_AccountNotifications", 0, null)],
            minBuild: Windows11_22H2, explorer: true),

        Tweak("ads.notificationsuggest", AdsCategory, "Turn off suggestion notifications",
            "Stops the \"Suggested\" notifications Windows sends (for example, offers to set up your phone or try new features).",
            RiskLevel.Safe, From(Sophia, ShutUp10),
            [User(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested", "Enabled", 0, null)]),

        Tweak("ads.scoobe", AdsCategory, "Stop \"Let's finish setting up your device\" nags",
            "Windows stops showing the full-screen setup reminders that push Microsoft 365, OneDrive and Game Pass.",
            RiskLevel.Safe, From(Win11Debloat, Sophia, ShutUp10),
            [User(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, null)]),

        Tweak("ads.consumer", AdsCategory, "Turn off Microsoft consumer experiences (policy)",
            "The CloudContent policy that stops Windows adding third-party consumer apps and offers.",
            RiskLevel.Advanced, From(WinUtil, Privatezilla, DebloatWindows10, HardenDebloat),
            [Machine(@"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1, null)],
            advancedOnly: true,
            warning: "Windows fully honours this policy only on Enterprise and Education; on Home and Pro it has a partial effect."),
    ];

    private static IEnumerable<DebloatTweak> AiTweaks() =>
    [
        Tweak("ai.copilot", AiCategory, "Turn off Windows Copilot",
            "Sets the Turn off Windows Copilot policy for your account and the PC, and hides the Copilot taskbar button.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, ShutUp10, Sophia, BloatyNosy),
            [
                User(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
                ExplorerFlag("ShowCopilotButton", 0, null),
            ],
            minBuild: Windows11_22H2, explorer: true),

        Tweak("ai.copilotapp", AiCategory, "Remove the Copilot app",
            "Uninstalls the Microsoft Copilot app. You can reinstall it from the Microsoft Store.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, BloatyNosy),
            [new AppxRemovalOp("Microsoft.Copilot", "Copilot")], minBuild: Windows11_22H2),

        Tweak("ai.recall", AiCategory, "Turn off Recall snapshots",
            "Stops Windows saving snapshots of your screen for Recall and prevents Recall from being enabled (Copilot+ PCs).",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, ShutUp10, Sophia),
            [
                User(WindowsAiUser, "DisableAIDataAnalysis", 1, null),
                Machine(WindowsAiMachine, "DisableAIDataAnalysis", 1, null),
                Machine(WindowsAiMachine, "AllowRecallEnablement", 0, null),
            ],
            minBuild: Windows11_24H2),

        Tweak("ai.recallfeature", AiCategory, "Turn off the Recall Windows feature",
            "Turns off the optional feature that contains Recall, on PCs where it is installed.",
            RiskLevel.Moderate, From(WinUtil, Win11Debloat),
            [new OptionalFeatureOp("Recall", "Recall")], restart: true, minBuild: Windows11_24H2),

        Tweak("ai.clicktodo", AiCategory, "Turn off Click to Do",
            "Turns off the Click to Do overlay that analyses what is on your screen (Copilot+ PCs).",
            RiskLevel.Safe, From(Win11Debloat, ShutUp10),
            [User(WindowsAiUser, "DisableClickToDo", 1, null), Machine(WindowsAiMachine, "DisableClickToDo", 1, null)],
            minBuild: Windows11_24H2),

        Tweak("ai.paint", AiCategory, "Turn off AI features in Paint",
            "Uses Paint's documented policies to turn off Cocreator, Generative fill and Image Creator.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat),
            [
                Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableCocreator", 1, null),
                Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeFill", 1, null),
                Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableImageCreator", 1, null),
            ],
            minBuild: Windows11_22H2),

        Tweak("ai.notepad", AiCategory, "Turn off AI features in Notepad",
            "Uses Notepad's policy to hide Rewrite and other AI writing features.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat),
            [Machine(@"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1, null)],
            minBuild: Windows11_22H2),

        Tweak("ai.bing", AiCategory, "Turn off Bing web results in Start search",
            "Searching from Start or the taskbar shows only results from your PC, not Bing web results.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, ShutUp10, Sophia, Privatezilla, Optimizer),
            [
                User(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null),
                User(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, null),
                User(@"Software\Microsoft\Windows\CurrentVersion\Search", "CortanaConsent", 0, null),
            ],
            explorer: true),

        Tweak("ai.searchhighlights", AiCategory, "Turn off search highlights",
            "Removes the daily trivia, doodles and trending searches from the search box and search window.",
            RiskLevel.Safe, From(Win11Debloat, Sophia, ShutUp10),
            [
                User(SearchSettings, "IsDynamicSearchBoxEnabled", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "EnableDynamicContentInWSB", 0, null),
            ]),

        Tweak("ai.searchcloud", AiCategory, "Don't search your cloud content from Start",
            "Start search stops looking through your Microsoft account and work account files in the cloud.",
            RiskLevel.Safe, From(ShutUp10, Sophia),
            [User(SearchSettings, "IsAADCloudSearchEnabled", 0, null), User(SearchSettings, "IsMSACloudSearchEnabled", 0, null)]),

        Tweak("ai.cortana", AiCategory, "Turn off Cortana",
            "Sets the Allow Cortana policy to off. Mainly affects Windows 10; on Windows 11 Cortana is already retired.",
            RiskLevel.Safe, From(WinUtil, ShutUp10, Privatezilla, DebloatWindows10, Optimizer, HardenDebloat),
            [Machine(@"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, null)]),
    ];
}
