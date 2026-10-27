using PcChecker.Core.Optimization;
using static PcChecker.Core.Debloat.DebloatSources;

namespace PcChecker.Core.Debloat;

public static partial class DebloatCatalog
{
    private const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string ClassicMenuClsid = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string GalleryClsid = @"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";
    private const string HomeClsid = @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";

    private static IEnumerable<DebloatTweak> TaskbarTweaks() =>
    [
        Tweak("taskbar.alignleft", TaskbarCategory, "Align taskbar icons to the left",
            "Moves the Start button and taskbar icons to the left, like Windows 10.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia, BloatyNosy),
            [ExplorerFlag("TaskbarAl", 0, null)], minBuild: Windows11, explorer: true),

        Tweak("taskbar.widgets", TaskbarCategory, "Hide the Widgets button",
            "Removes the weather/news Widgets button from the taskbar.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, ShutUp10, Sophia),
            [ExplorerFlag("TaskbarDa", 0, null)], minBuild: Windows11, explorer: true,
            warning: "Some 24H2 builds block other apps from changing this setting. If it fails, use Settings > Personalization > Taskbar or the Widgets policy below."),

        Tweak("taskbar.widgetspolicy", TaskbarCategory, "Turn off Widgets completely (policy)",
            "Sets the Allow widgets policy to off, which removes the Widgets board for every user.",
            RiskLevel.Advanced, From(WinUtil, Win11Debloat, ShutUp10),
            [Machine(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, null)],
            advancedOnly: true, minBuild: Windows11, explorer: true),

        Tweak("taskbar.chat", TaskbarCategory, "Hide the Chat (Teams) button",
            "Removes the Microsoft Teams Chat button from the taskbar.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia),
            [ExplorerFlag("TaskbarMn", 0, null)], minBuild: Windows11, explorer: true),

        Tweak("taskbar.taskview", TaskbarCategory, "Hide the Task View button",
            "Removes the Task View button. Win+Tab still opens Task View.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia),
            [ExplorerFlag("ShowTaskViewButton", 0, null)], explorer: true),

        Tweak("taskbar.searchicon", TaskbarCategory, "Show search as an icon only",
            "Replaces the wide search box on the taskbar with a small search icon.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia),
            [User(SearchKey, "SearchboxTaskbarMode", 1, null)], explorer: true),

        Tweak("taskbar.searchhide", TaskbarCategory, "Hide search on the taskbar",
            "Removes search from the taskbar completely. Press the Windows key and type to search.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia),
            [User(SearchKey, "SearchboxTaskbarMode", 0, null)], explorer: true),

        Tweak("taskbar.endtask", TaskbarCategory, "Add \"End task\" to taskbar right-click",
            "Right-clicking an app on the taskbar offers End task to close a frozen app without Task Manager.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia),
            [User(ExplorerAdvanced + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, null)], minBuild: Windows11_23H2),

        Tweak("taskbar.seconds", TaskbarCategory, "Show seconds in the taskbar clock",
            "The clock shows seconds. Uses a little more power on laptops.",
            RiskLevel.Safe, From(Sophia),
            [ExplorerFlag("ShowSecondsInSystemClock", 1, null)], minBuild: Windows11_22H2, explorer: true),

        Tweak("start.morepins", TaskbarCategory, "Show more pins in Start",
            "Start shows an extra row of pinned apps and fewer recommendations.",
            RiskLevel.Safe, From(Sophia),
            [ExplorerFlag("Start_Layout", 1, null)], minBuild: Windows11_22H2, explorer: true),

        Tweak("start.settingshome", TaskbarCategory, "Hide the Settings Home page",
            "Settings opens straight to System instead of the Home page with its account and Microsoft 365 cards.",
            RiskLevel.Moderate, From(WinUtil),
            [MachineText(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "SettingsPageVisibility", "hide:home", null)],
            minBuild: Windows11_23H2,
            warning: "Replaces any existing Settings page visibility policy; Revert restores the previous one."),

        Tweak("taskbar.news", TaskbarCategory, "Turn off News and interests (Windows 10)",
            "Removes the weather and news panel from the Windows 10 taskbar.",
            RiskLevel.Safe, From(WinUtil, ShutUp10, Sophia),
            [User(@"Software\Microsoft\Windows\CurrentVersion\Feeds", "ShellFeedsTaskbarViewMode", 2, null)],
            maxBuild: Windows10Last, explorer: true),

        Tweak("taskbar.meetnow", TaskbarCategory, "Hide Meet Now (Windows 10)",
            "Removes the Skype Meet Now icon from the Windows 10 notification area.",
            RiskLevel.Safe, From(Sophia, Optimizer),
            [User(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "HideSCAMeetNow", 1, null)],
            maxBuild: Windows10Last, explorer: true),
    ];

    private static IEnumerable<DebloatTweak> ExplorerTweaks() =>
    [
        Tweak("explorer.extensions", ExplorerCategory, "Show file extensions",
            "Shows endings such as .exe and .pdf so you can spot fake documents that are really programs.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia, Optimizer, BloatyNosy),
            [ExplorerFlag("HideFileExt", 0, 1)]),

        Tweak("explorer.hidden", ExplorerCategory, "Show hidden files and folders",
            "File Explorer shows hidden items (shown faded). Protected system files stay hidden.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia, Optimizer),
            [ExplorerFlag("Hidden", 1, 2)]),

        Tweak("explorer.thispc", ExplorerCategory, "Open File Explorer to This PC",
            "File Explorer opens on your drives instead of Home/Quick access.",
            RiskLevel.Safe, From(Win11Debloat, Sophia, Optimizer),
            [ExplorerFlag("LaunchTo", 1, null)]),

        Tweak("explorer.classicmenu", ExplorerCategory, "Use the classic right-click menu",
            "Brings back the full Windows 10 style right-click menu instead of \"Show more options\".",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia, BloatyNosy),
            [
                new RegistryValueOp(DebloatHive.CurrentUser, ClassicMenuClsid + @"\InprocServer32", string.Empty, DebloatValueKind.String, string.Empty, null)
                {
                    DeleteKeyOnRevert = ClassicMenuClsid,
                },
            ],
            minBuild: Windows11, explorer: true),

        Tweak("explorer.homegallery", ExplorerCategory, "Remove Home and Gallery from the navigation pane",
            "Hides the Home and Gallery entries on the left side of File Explorer.",
            RiskLevel.Moderate, From(WinUtil, Win11Debloat),
            [
                new RegistryValueOp(DebloatHive.CurrentUser, GalleryClsid, "System.IsPinnedToNameSpaceTree", DebloatValueKind.DWord, "0", null) { DeleteKeyOnRevert = GalleryClsid },
                new RegistryValueOp(DebloatHive.CurrentUser, HomeClsid, "System.IsPinnedToNameSpaceTree", DebloatValueKind.DWord, "0", null) { DeleteKeyOnRevert = HomeClsid },
            ],
            minBuild: Windows11_22H2, explorer: true,
            warning: "Pair it with \"Open File Explorer to This PC\", otherwise Explorer may open on an empty page."),

        Tweak("explorer.compact", ExplorerCategory, "Use compact view",
            "Reduces the spacing between items in File Explorer so more files fit on screen.",
            RiskLevel.Safe, From(Sophia),
            [ExplorerFlag("UseCompactMode", 1, null)], minBuild: Windows11),

        Tweak("explorer.recent", ExplorerCategory, "Hide recent files and frequent folders in Quick access",
            "Quick access/Home stops listing files and folders you opened recently.",
            RiskLevel.Safe, From(Sophia, ShutUp10, Optimizer),
            [User(ExplorerKey, "ShowRecent", 0, null), User(ExplorerKey, "ShowFrequent", 0, null)]),

        Tweak("explorer.officecloud", ExplorerCategory, "Hide files from Office.com in Home",
            "File Explorer's Home stops listing your Microsoft 365 cloud files.",
            RiskLevel.Safe, From(Sophia, ShutUp10),
            [User(ExplorerKey, "ShowCloudFilesInQuickAccess", 0, null)], minBuild: Windows11_22H2),

        Tweak("explorer.fullpath", ExplorerCategory, "Show the full path in the title bar",
            "Shows the complete folder path (for example C:\\Users\\You\\Documents) in the window title.",
            RiskLevel.Safe, From(Sophia, Optimizer),
            [User(ExplorerKey + @"\CabinetState", "FullPath", 1, null)]),

        Tweak("explorer.autodiscovery", ExplorerCategory, "Turn off automatic folder type discovery",
            "Stops File Explorer guessing a folder's type (Pictures, Music…) from its content, which makes big folders open faster.",
            RiskLevel.Safe, From(WinUtil),
            [UserText(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell", "FolderType", "NotSpecified", null)],
            explorer: true),

        Tweak("explorer.longpaths", ExplorerCategory, "Allow long file paths",
            "Lets programs that support it use paths longer than 260 characters (common in developer tools and deep folders).",
            RiskLevel.Safe, From(WinUtil, Sophia),
            [Machine(@"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1, 0)], restart: true),
    ];
}
