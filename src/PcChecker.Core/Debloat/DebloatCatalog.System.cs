using PcChecker.Core.Optimization;
using static PcChecker.Core.Debloat.DebloatSources;

namespace PcChecker.Core.Debloat;

public static partial class DebloatCatalog
{
    private const string EdgePolicy = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string UpdatePolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string EdgeManagedNote = "Edge shows \"Your browser is managed by your organization\" while any Edge policy is set. Revert removes the policies again.";

    /// <summary>Uninstalls OneDrive with Microsoft's own setup program; nothing is downloaded.</summary>
    internal const string OneDriveUninstallScript =
        "$ErrorActionPreference = 'Continue'; " +
        "Get-Process OneDrive -ErrorAction SilentlyContinue | Stop-Process -Force; " +
        "$candidates = @(\"$env:SystemRoot\\System32\\OneDriveSetup.exe\", \"$env:SystemRoot\\SysWOW64\\OneDriveSetup.exe\") + " +
        "@(Get-ChildItem \"$env:LOCALAPPDATA\\Microsoft\\OneDrive\\*\\OneDriveSetup.exe\" -ErrorAction SilentlyContinue | ForEach-Object FullName) + " +
        "@(Get-ChildItem \"$env:ProgramFiles\\Microsoft OneDrive\\*\\OneDriveSetup.exe\" -ErrorAction SilentlyContinue | ForEach-Object FullName); " +
        "$exe = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1; " +
        "if (-not $exe) { [Console]::Error.WriteLine('OneDriveSetup.exe was not found.'); exit 1 }; " +
        "$p = Start-Process -FilePath $exe -ArgumentList '/uninstall' -Wait -PassThru; exit $p.ExitCode";

    /// <summary>Prints True when OneDrive is no longer installed for this user or the PC.</summary>
    internal const string OneDriveRemovedDetectScript =
        "-not ((Test-Path \"$env:LOCALAPPDATA\\Microsoft\\OneDrive\\OneDrive.exe\") -or (Test-Path \"$env:ProgramFiles\\Microsoft OneDrive\\OneDrive.exe\"))";

    private static IEnumerable<DebloatTweak> EdgeTweaks() =>
    [
        Tweak("edge.background", EdgeCategory, "Stop Microsoft Edge running in the background",
            "Turns off Startup boost and background mode, so Edge only runs while its window is open.",
            RiskLevel.Moderate, From(WinUtil, ShutUp10, Optimizer),
            [Machine(EdgePolicy, "StartupBoostEnabled", 0, null), Machine(EdgePolicy, "BackgroundModeEnabled", 0, null)],
            warning: EdgeManagedNote),

        Tweak("edge.nags", EdgeCategory, "Turn off Edge sidebar, shopping and first-run nags",
            "Hides the Edge sidebar (including Copilot), the shopping assistant, product recommendations and the first-run setup pages.",
            RiskLevel.Moderate, From(WinUtil, ShutUp10, Optimizer),
            [
                Machine(EdgePolicy, "HubsSidebarEnabled", 0, null),
                Machine(EdgePolicy, "EdgeShoppingAssistantEnabled", 0, null),
                Machine(EdgePolicy, "ShowRecommendationsEnabled", 0, null),
                Machine(EdgePolicy, "HideFirstRunExperience", 1, null),
            ],
            warning: EdgeManagedNote),

        Tweak("onedrive.autostart", EdgeCategory, "Stop OneDrive starting with Windows",
            "Marks OneDrive as disabled in Startup apps (the same switch Task Manager uses). OneDrive still works when you open it.",
            RiskLevel.Safe, From(Optimizer, Sophia),
            [new RegistryValueOp(DebloatHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "OneDrive", DebloatValueKind.Binary, "030000000000000000000000", null)],
            warning: "Files stop syncing until you open OneDrive yourself."),

        Tweak("onedrive.nags", EdgeCategory, "Turn off OneDrive backup prompts and notifications",
            "Stops OneDrive notifications and the prompts that ask to back up Desktop, Documents and Pictures to OneDrive.",
            RiskLevel.Safe, From(ShutUp10, Sophia),
            [
                User(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Microsoft.SkyDrive.Desktop", "Enabled", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\OneDrive", "KFMBlockOptIn", 1, null),
            ]),

        Tweak("onedrive.remove", EdgeCategory, "Uninstall OneDrive",
            "Runs Microsoft's OneDrive setup with /uninstall. Your files stay in the OneDrive folder on this PC; syncing stops.",
            RiskLevel.Advanced, From(WinUtil, Win11Debloat, Sophia, Optimizer, DebloatWindows10, BloatyNosy),
            [
                new PowerShellOp("onedrive.uninstall", "Uninstall OneDrive with OneDriveSetup.exe /uninstall",
                    OneDriveUninstallScript, null, OneDriveRemovedDetectScript,
                    "Reinstall OneDrive from microsoft.com/onedrive or with: winget install Microsoft.OneDrive"),
            ],
            advancedOnly: true,
            warning: "Files that were online-only are not on this PC. Make sure everything you need is downloaded first."),
    ];

    private static IEnumerable<DebloatTweak> GamingTweaks() =>
    [
        Linked("gaming.gamedvr", GamingCategory, "Turn off Game Bar background recording",
            "Stops the Xbox Game Bar capturing gameplay in the background. Same change as the Optimize page's Game Bar action.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, Sophia, Optimizer), "gamebar.disable"),

        Linked("gaming.gamemode", GamingCategory, "Turn on Game Mode",
            "Windows gives games priority over background work. Same change as the Optimize page's Game Mode action.",
            RiskLevel.Safe, From(WinUtil, Sophia), "gamemode.enable"),

        Tweak("gaming.fso", GamingCategory, "Turn off fullscreen optimizations",
            "Makes games use true exclusive fullscreen. Can lower input latency in older DirectX games.",
            RiskLevel.Moderate, From(WinUtil, Optimizer, AtlasOS),
            [
                User(@"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, null),
                User(@"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, null),
                User(@"System\GameConfigStore", "GameDVR_FSEBehavior", 2, null),
                User(@"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 1, null),
            ],
            warning: "Modern games often run better with optimizations on, and alt-tab becomes slower. Revert if a game misbehaves."),

        Tweak("gaming.xboxservices", GamingCategory, "Disable Xbox services",
            "Disables Xbox Live auth, game saves, networking and accessory services for PCs that never use Xbox features.",
            RiskLevel.Advanced, From(WinUtil, DebloatWindows10, AtlasOS, HardenDebloat),
            [
                Service("XblAuthManager", DebloatStartMode.Disabled, DebloatStartMode.Manual),
                Service("XblGameSave", DebloatStartMode.Disabled, DebloatStartMode.Manual),
                Service("XboxNetApiSvc", DebloatStartMode.Disabled, DebloatStartMode.Manual),
                Service("XboxGipSvc", DebloatStartMode.Disabled, DebloatStartMode.Manual),
            ],
            advancedOnly: true,
            warning: "Breaks Game Pass, Xbox app sign-in, cloud saves in Xbox games and Xbox accessory updates."),
    ];

    private static IEnumerable<DebloatTweak> UpdateTweaks() =>
    [
        Tweak("wu.drivers", UpdateCategory, "Don't install drivers through Windows Update",
            "Windows Update stops replacing your drivers. Use PC Checker's Drivers page or your vendor's tools instead.",
            RiskLevel.Advanced, From(WinUtil, ShutUp10, Sophia),
            [Machine(UpdatePolicy, "ExcludeWUDriversInQualityUpdate", 1, null)],
            advancedOnly: true,
            warning: "You become responsible for driver updates; the Drivers page is then your driver source."),

        Tweak("wu.deferfeature", UpdateCategory, "Delay feature updates by 180 days",
            "New Windows versions install about six months after release, once early problems are fixed. Security updates are not delayed.",
            RiskLevel.Advanced, From(WinUtil, ShutUp10),
            [Machine(UpdatePolicy, "DeferFeatureUpdates", 1, null), Machine(UpdatePolicy, "DeferFeatureUpdatesPeriodInDays", 180, null)],
            advancedOnly: true,
            warning: "Windows Home ignores this policy."),

        Tweak("wu.noautoreboot", UpdateCategory, "No automatic restart while you are signed in",
            "Windows waits for you to restart after installing updates instead of restarting on its own.",
            RiskLevel.Advanced, From(WinUtil, ShutUp10, Sophia),
            [Machine(UpdatePolicy + @"\AU", "NoAutoRebootWithLoggedOnUsers", 1, null)],
            advancedOnly: true,
            warning: "Updates only finish after you restart, so restart regularly."),

        Tweak("wu.dolan", UpdateCategory, "Share update downloads only on your local network",
            "Delivery Optimization stops uploading updates to other PCs on the internet; it still shares between PCs at home.",
            RiskLevel.Advanced, From(WinUtil, ShutUp10, Sophia, Privatezilla),
            [Machine(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1, null)],
            advancedOnly: true),
    ];

    private static IEnumerable<DebloatTweak> ServiceTweaks() =>
    [
        Tweak("svc.fax", ServicesCategory, "Disable the Fax service",
            "Safe if you never send faxes from this PC.",
            RiskLevel.Advanced, From(WinUtil, DebloatWindows10, AtlasOS, ReviOS),
            [Service("Fax", DebloatStartMode.Disabled, DebloatStartMode.Manual)], advancedOnly: true),

        Tweak("svc.spooler", ServicesCategory, "Disable the Print Spooler",
            "Safe if you never print. Applied only when no physical printer is installed.",
            RiskLevel.Advanced, From(AtlasOS, ReviOS, HardenDebloat),
            [Service("Spooler", DebloatStartMode.Disabled, DebloatStartMode.Automatic)], advancedOnly: true,
            warning: "Printing, Microsoft Print to PDF and printer setup stop working until you revert.")
        with { Precondition = DebloatPrecondition.NoPhysicalPrinters },

        Tweak("svc.remoteregistry", ServicesCategory, "Keep Remote Registry disabled",
            "Makes sure other computers cannot edit this PC's registry over the network. Already disabled on most PCs.",
            RiskLevel.Advanced, From(DebloatWindows10, HardenDebloat, AtlasOS),
            [Service("RemoteRegistry", DebloatStartMode.Disabled, DebloatStartMode.Disabled)], advancedOnly: true),

        Tweak("svc.mapsbroker", ServicesCategory, "Set Downloaded Maps Manager to manual",
            "Safe if you don't use offline maps; the Maps app starts the service when it needs it.",
            RiskLevel.Advanced, From(WinUtil, DebloatWindows10, AtlasOS),
            [Service("MapsBroker", DebloatStartMode.Manual, DebloatStartMode.AutomaticDelayed)], advancedOnly: true),

        Tweak("svc.retaildemo", ServicesCategory, "Disable the Retail Demo service",
            "Only used by store display PCs. Safe on every home PC.",
            RiskLevel.Advanced, From(WinUtil, DebloatWindows10, AtlasOS),
            [Service("RetailDemo", DebloatStartMode.Disabled, DebloatStartMode.Manual)], advancedOnly: true),

        Tweak("svc.wmpnetwork", ServicesCategory, "Disable Windows Media Player network sharing",
            "Safe if you don't stream your media library to other devices with Windows Media Player.",
            RiskLevel.Advanced, From(DebloatWindows10, HardenDebloat, AtlasOS),
            [Service("WMPNetworkSvc", DebloatStartMode.Disabled, DebloatStartMode.Manual)], advancedOnly: true),

        Tweak("svc.wisvc", ServicesCategory, "Disable the Windows Insider service",
            "Safe if you are not in the Windows Insider Program.",
            RiskLevel.Advanced, From(AtlasOS, ReviOS),
            [Service("wisvc", DebloatStartMode.Disabled, DebloatStartMode.Manual)], advancedOnly: true),

        Linked("svc.searchindex", ServicesCategory, "Disable Windows Search indexing",
            "Stops the background indexer. Same change as the Optimize page's search indexing action.",
            RiskLevel.Advanced, From(WinUtil, Optimizer, AtlasOS), "search.indexing.disable", advancedOnly: true),

        Linked("svc.sysmain", ServicesCategory, "Disable SysMain (Superfetch)",
            "Stops the service that preloads apps into memory. Same change as the Optimize page's SysMain action.",
            RiskLevel.Advanced, From(WinUtil, Optimizer, AtlasOS, ReviOS), "sysmain.disable", advancedOnly: true),
    ];
}
