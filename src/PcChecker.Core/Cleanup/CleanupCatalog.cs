using PcChecker.Core.Optimization;

namespace PcChecker.Core.Cleanup;

/// <summary>
/// Fixed, reviewed list of cleanup categories with path templates. Templates use the environment variables %TEMP%,
/// %WINDIR%, %LOCALAPPDATA%, %APPDATA%, %PROGRAMDATA%, %SYSTEMDRIVE%, %USERPROFILE% and %PROGRAMFILES(X86)%, plus the
/// values PC Checker works out itself (see <see cref="ComputedVariables"/>): %STEAM% and %STEAMLIBRARIES% (Steam and
/// every Steam library), %UBISOFT% (Ubisoft Connect), %DUMPFILEDIR% and %MINIDUMPDIR% (where Windows writes crash
/// dumps) and %FIXEDDRIVES% (each fixed drive). A path segment may contain '*' or '?' to match several folders (for
/// example one entry per browser profile); <see cref="CleanupPathResolver"/> expands those on the real file system.
/// </summary>
public static class CleanupCatalog
{
    private static readonly string[] AllFiles = ["*"];

    /// <summary>Variables that come from the process environment.</summary>
    public static readonly IReadOnlyList<string> EnvironmentVariables =
        ["TEMP", "TMP", "WINDIR", "LOCALAPPDATA", "APPDATA", "PROGRAMDATA", "SYSTEMDRIVE", "USERPROFILE", "PROGRAMFILES(X86)"];

    /// <summary>Variables PC Checker resolves in code (registry, launcher config, drive list).</summary>
    public static readonly IReadOnlyList<string> ComputedVariables =
        ["STEAM", "STEAMLIBRARIES", "UBISOFT", "DUMPFILEDIR", "MINIDUMPDIR", "FIXEDDRIVES"];

    /// <summary>Computed variables that expand to several folders (one root per value).</summary>
    public static readonly IReadOnlyList<string> MultiValueVariables = ["STEAMLIBRARIES", "FIXEDDRIVES"];

    /// <summary>
    /// Where the Delivery Optimization service (DoSvc, running as NetworkService) keeps its cache on current Windows 10
    /// and 11. The two older folders stay in the list for PCs that still use them.
    /// </summary>
    private const string DeliveryOptimizationCache = @"%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache";

    private const string SystemProfile = @"%WINDIR%\System32\config\systemprofile\AppData";

    /// <summary>Cache sub-folders of a Chromium profile (Chrome, Edge, Brave, Vivaldi, …). Never the whole Service Worker
    /// folder: that unregisters service workers and drops push subscriptions.</summary>
    private static readonly string[] ChromiumCacheFolders =
    [
        @"Cache\Cache_Data", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache",
        "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", @"Shared Dictionary\cache",
    ];

    /// <summary>Caches directly in "User Data" (shared by every profile), which a "User Data\*\" template does not reach.</summary>
    private static readonly string[] ChromiumRootCacheFolders = ["GrShaderCache", "GraphiteDawnCache", "ShaderCache", @"Crashpad\reports"];

    // Each channel is listed by name rather than with "Chrome*" so a sibling folder (Chrome Remote Desktop, EdgeUpdate,
    // EdgeWebView) can never become a root.
    private static readonly string[] ChromeUserData =
    [
        @"%LOCALAPPDATA%\Google\Chrome\User Data", @"%LOCALAPPDATA%\Google\Chrome Beta\User Data",
        @"%LOCALAPPDATA%\Google\Chrome Dev\User Data", @"%LOCALAPPDATA%\Google\Chrome SxS\User Data",
    ];

    private static readonly string[] EdgeUserData =
    [
        @"%LOCALAPPDATA%\Microsoft\Edge\User Data", @"%LOCALAPPDATA%\Microsoft\Edge Beta\User Data",
        @"%LOCALAPPDATA%\Microsoft\Edge Dev\User Data", @"%LOCALAPPDATA%\Microsoft\Edge SxS\User Data",
    ];

    private static readonly string[] BraveUserData =
    [
        @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data", @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser-Beta\User Data",
        @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser-Nightly\User Data",
    ];

    private const string VivaldiUserData = @"%LOCALAPPDATA%\Vivaldi\User Data";
    private const string ChromiumUserData = @"%LOCALAPPDATA%\Chromium\User Data";
    private const string ArcUserData = @"%LOCALAPPDATA%\Packages\TheBrowserCompany.Arc_ttt1ap7aakyb4\LocalCache\Local\Arc\User Data";
    private const string YandexUserData = @"%LOCALAPPDATA%\Yandex\YandexBrowser\User Data";

    /// <summary>Every Chromium "User Data" folder PC Checker knows (for the privacy categories).</summary>
    private static readonly string[] AllChromiumUserData =
        [.. ChromeUserData, .. EdgeUserData, .. BraveUserData, VivaldiUserData, ChromiumUserData, ArcUserData, YandexUserData];

    /// <summary>Opera keeps its profile in Roaming and its HTTP cache in Local; side profiles sit under _side_profiles.</summary>
    private static readonly string[] OperaRoamingCacheFolders = ["Code Cache", "GPUCache", @"Service Worker\CacheStorage", "GrShaderCache", "ShaderCache"];

    /// <summary>Firefox-family profile folders in Roaming (bookmarks, cookies, history, sessions).</summary>
    private static readonly string[] FirefoxFamilyRoamingProfiles =
    [
        @"%APPDATA%\Mozilla\Firefox\Profiles\*", @"%APPDATA%\Waterfox\Profiles\*", @"%APPDATA%\librewolf\Profiles\*",
        @"%APPDATA%\Floorp\Profiles\*", @"%APPDATA%\zen\Profiles\*",
    ];

    /// <summary>Files no cleanup category may ever touch in a browser profile: saved passwords, cards, bookmarks.</summary>
    private static readonly string[] BrowserProtectedFiles =
        ["Login Data*", "Web Data*", "places.sqlite*", "favicons.sqlite*", "key4.db", "logins.json", "logins-backup.json", "Bookmarks*", "Preferences", "Local State"];

    public static IReadOnlyList<CleanupCategory> All { get; } = Build();

    public static CleanupCategory? Find(string id) => All.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<string> DefaultIds => All.Where(c => c.EnabledByDefault).Select(c => c.Id);

    /// <summary>Plain-English name of a group for the page filter.</summary>
    public static string GroupName(CleanupGroup group) => group switch
    {
        CleanupGroup.Windows => "Windows",
        CleanupGroup.Graphics => "Graphics drivers",
        CleanupGroup.Browsers => "Browsers",
        CleanupGroup.Apps => "Apps",
        CleanupGroup.Games => "Game launchers",
        CleanupGroup.Developer => "Developer tools",
        CleanupGroup.Privacy => "Privacy",
        _ => group.ToString(),
    };

    /// <summary>Expands template variables using the supplied environment lookup (so it can be tested on any OS).</summary>
    public static string ExpandTemplate(string template, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var result = template;
        int guard = 0;
        while (guard++ < 10)
        {
            int start = result.IndexOf('%');
            if (start < 0)
            {
                break;
            }

            int end = result.IndexOf('%', start + 1);
            if (end < 0)
            {
                break;
            }

            var name = result[(start + 1)..end];
            var value = getEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                // Unknown variable: leave the text and stop to avoid loops.
                break;
            }

            result = result[..start] + value.TrimEnd('\\') + result[(end + 1)..];
        }

        return result;
    }

    /// <summary>Expands using the current process environment.</summary>
    public static string ExpandTemplate(string template) => ExpandTemplate(template, Environment.GetEnvironmentVariable);

    /// <summary>All expanded roots for a category (even if they do not exist). Wildcard segments are kept as written.</summary>
    public static IReadOnlyList<string> ExpandRoots(CleanupCategory category, Func<string, string?> getEnvironmentVariable) =>
        category.Paths.Select(p => ExpandTemplate(p.Template, getEnvironmentVariable)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Every %NAME% used in a template, upper-cased.</summary>
    public static IReadOnlyList<string> VariablesIn(string template)
    {
        var names = new List<string>();
        int start = 0;
        while (true)
        {
            int open = template.IndexOf('%', start);
            if (open < 0)
            {
                break;
            }

            int close = template.IndexOf('%', open + 1);
            if (close < 0)
            {
                break;
            }

            names.Add(template[(open + 1)..close].ToUpperInvariant());
            start = close + 1;
        }

        return names;
    }

    private static string[] ChromiumTemplates(params string[] userDataRoots) =>
        userDataRoots.SelectMany(root =>
                ChromiumCacheFolders.Select(f => $@"{root}\*\{f}")
                    .Concat(ChromiumRootCacheFolders.Select(f => $@"{root}\{f}")))
            .ToArray();

    private static string[] FirefoxForkTemplates(string localProfiles, string roamingProfiles) =>
    [
        $@"{localProfiles}\*\cache2", $@"{localProfiles}\*\startupCache", $@"{localProfiles}\*\thumbnails",
        $@"{roamingProfiles}\*\shader-cache",
    ];

    private static string[] OperaTemplates(string folder) =>
    [
        $@"%LOCALAPPDATA%\Opera Software\{folder}\*Cache*",
        .. OperaRoamingCacheFolders.Select(f => $@"%APPDATA%\Opera Software\{folder}\{f}"),
        .. OperaRoamingCacheFolders.Select(f => $@"%APPDATA%\Opera Software\{folder}\_side_profiles\*\{f}"),
    ];

    /// <summary>Single files (exact names) inside each Chromium profile and inside the profile's Network folder.</summary>
    private static CleanupPath[] ChromiumProfileFiles(string[] names, bool includeNetwork) =>
        AllChromiumUserData.SelectMany(root => includeNetwork
                ? new[] { new CleanupPath($@"{root}\*", names, false), new CleanupPath($@"{root}\*\Network", names, false) }
                : new[] { new CleanupPath($@"{root}\*", names, false) })
            .ToArray();

    private static CleanupPath[] FirefoxProfileFiles(params string[] names) =>
        FirefoxFamilyRoamingProfiles.Select(p => new CleanupPath(p, names, false)).ToArray();

    private static List<CleanupCategory> Build() =>
    [
        // ---- Windows ------------------------------------------------------------------------------------------
        new("user.temp", "Your temporary files", "Files programs left behind in your personal Temp folder.",
            RiskLevel.Safe, ["%TEMP%"], AllFiles, true, true) { PruneAnywhere = true },
        new("windows.temp", "Windows temporary files", "Temporary files created by Windows, services and installers.",
            RiskLevel.Safe, [@"%WINDIR%\Temp"], AllFiles, true, true)
        {
            PruneAnywhere = true,
            // Windows 11 24H2 sends temp files of SYSTEM processes to SystemTemp; services use their own profile's Temp.
            // Their ACLs only let SYSTEM and Administrators in, so they are scanned when PC Checker runs elevated.
            ExtraPaths =
            [
                new(@"%WINDIR%\SystemTemp", ElevatedOnly: true),
                new(@"%WINDIR%\ServiceProfiles\LocalService\AppData\Local\Temp", ElevatedOnly: true),
                new(@"%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Temp", ElevatedOnly: true),
                new(SystemProfile + @"\Local\Temp", ElevatedOnly: true),
            ],
        },
        new("windows.update.cache", "Windows Update downloads", "Installer files Windows Update has already used. The update service is paused briefly while they are removed.",
            RiskLevel.Safe, [@"%WINDIR%\SoftwareDistribution\Download"], AllFiles, true, true) { PruneAnywhere = true },
        new("delivery.optimization", "Delivery Optimization cache", "Update files cached for sharing with other PCs.",
            RiskLevel.Safe, [DeliveryOptimizationCache, @"%WINDIR%\SoftwareDistribution\DeliveryOptimization", @"%PROGRAMDATA%\Microsoft\Windows\DeliveryOptimization\Cache"], AllFiles, true, true) { PruneAnywhere = true },
        new("prefetch", "Old prefetch data", "Prefetch files of programs you have not started for 30 days. Recent ones stay, so your usual programs keep starting fast.",
            RiskLevel.Moderate, [@"%WINDIR%\Prefetch"], ["*.pf"], false, false)
        {
            MinAgeDays = 30,
            ExcludePatterns = ["Layout.ini"],
            ExcludePathTemplates = [@"%WINDIR%\Prefetch\ReadyBoot"],
            Hint = "Removing all prefetch data slows program starts for almost no space, so only entries unused for 30 days are offered.",
        },
        new("thumbnails", "Thumbnail cache", "Cached picture previews shown in File Explorer. Windows rebuilds them when needed.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Microsoft\Windows\Explorer"], ["thumbcache_*.db"], true, false)
        {
            Hint = "File Explorer keeps most of these files open, so they are skipped as in use. Restart File Explorer, then clean again.",
        },
        new("icon.cache", "Icon cache", "Cached icons shown in File Explorer. Windows rebuilds them when needed.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Microsoft\Windows\Explorer"], ["iconcache_*.db"], false, false)
        {
            ExtraPaths = [new(@"%LOCALAPPDATA%", ["IconCache.db"], false)],
            Hint = "File Explorer keeps most of these files open, so they are skipped as in use. Restart File Explorer, then clean again.",
        },
        new("recycle.bin", "Recycle Bin", "Files you deleted but have not emptied yet.",
            RiskLevel.Safe, [@"%SYSTEMDRIVE%\$Recycle.Bin"], AllFiles, true, true),
        new("crash.dumps", "Crash dumps", "Memory snapshots saved when Windows, a program or a graphics driver crashed or timed out. Useful for diagnosis, otherwise just large.",
            RiskLevel.Moderate, [@"%WINDIR%\Minidump", @"%LOCALAPPDATA%\CrashDumps", @"%WINDIR%"], ["*.dmp", "MEMORY.DMP"], false, false)
        {
            ExtraPaths =
            [
                // WATCHDOG and VIDEO_TDR dumps written when an AMD, NVIDIA or Intel driver stops responding.
                new(@"%WINDIR%\LiveKernelReports", ["*.dmp"], true),
                new(@"%PROGRAMDATA%\Microsoft\Windows\WER\Temp", AllFiles, true),
                new(SystemProfile + @"\Local\CrashDumps", ElevatedOnly: true),
                new(@"%WINDIR%\ServiceProfiles\*\AppData\Local\CrashDumps", ElevatedOnly: true),
                // Wherever the user moved the dumps to (Startup and Recovery settings).
                new("%DUMPFILEDIR%"),
                new("%MINIDUMPDIR%"),
            ],
            Hint = "Keep these if you are troubleshooting crashes or graphics driver timeouts.",
        },
        new("windows.error.reports", "Windows error reports", "Reports about crashes queued for Microsoft.",
            RiskLevel.Safe, [@"%PROGRAMDATA%\Microsoft\Windows\WER\ReportQueue", @"%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive", @"%LOCALAPPDATA%\Microsoft\Windows\WER"], AllFiles, true, true) { PruneAnywhere = true },
        new("windows.logs", "Windows log files", "Old log files from Windows components, Windows Update, Delivery Optimization and installers.",
            RiskLevel.Moderate, [@"%WINDIR%\Logs", @"%WINDIR%\Panther"], ["*.log", "*.etl", "*.cab"], false, true)
        {
            ExtraPaths =
            [
                // Never SoftwareDistribution\DataStore\Logs: those edb*.log files are the transaction logs of the
                // Windows Update database, not text logs.
                new(@"%WINDIR%\SoftwareDistribution", ["*.log"], false),
                new(@"%PROGRAMDATA%\USOShared\Logs", ["*.log", "*.etl"], true),
                new(@"%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Logs", ["*.log", "*.etl"], false),
                new(@"%WINDIR%\INF", ["setupapi*.log"], false),
                new(@"%WINDIR%\debug", ["*.log"], false),
            ],
            Hint = "CBS.log is skipped while Windows is installing updates. setupapi.dev.log is the main record for diagnosing driver installs (for example a failed AMD or NVIDIA install).",
        },
        new("windows.old", "Previous Windows installation", "The Windows.old folder from a major upgrade, removed through Windows' own Disk Cleanup. You cannot go back to the old version afterwards.",
            RiskLevel.Advanced, [@"%SYSTEMDRIVE%\Windows.old"], AllFiles, false, true)
        {
            Kind = CleanupKind.Command,
            Command = CleanupCommand.DiskCleanupHandlers,
            DiskCleanupHandlers = ["Previous Installations"],
            Requires = CleanupRequirement.Elevated | CleanupRequirement.NoPendingReboot,
            NeverScheduled = true,
            ConfirmWarning = "Windows.old is removed: going back to the previous version of Windows is no longer possible.",
        },
        new("windows.upgrade.leftovers", "Windows upgrade leftovers", "Setup files left by a feature update ($WINDOWS.~BT, $WINDOWS.~WS, ESD and $GetCurrent), removed through Windows' own Disk Cleanup.",
            RiskLevel.Advanced,
            [
                @"%SYSTEMDRIVE%\$WINDOWS.~BT", @"%SYSTEMDRIVE%\$WINDOWS.~WS", @"%SYSTEMDRIVE%\$GetCurrent", @"%SYSTEMDRIVE%\ESD\Download",
                @"%SYSTEMDRIVE%\ESD\Windows",
            ],
            AllFiles, false, true)
        {
            Kind = CleanupKind.Command,
            Command = CleanupCommand.DiskCleanupHandlers,
            DiskCleanupHandlers = ["Temporary Setup Files", "Windows ESD installation files"],
            Requires = CleanupRequirement.Elevated | CleanupRequirement.NoPendingReboot | CleanupRequirement.NoUpdateInstalling,
            NeverScheduled = true,
            Hint = "$WINDOWS.~BT holds rollback data used together with Windows.old for 10 days after an upgrade.",
            ConfirmWarning = "Windows upgrade leftovers are removed: some \"Reset this PC\" options may need to download Windows again, and going back to the previous version may no longer work.",
        },
        new("windows.component.store", "Windows Update cleanup (component store)", "Superseded Windows components in WinSxS, removed by DISM /StartComponentCleanup. Takes 5 to 30 minutes.",
            RiskLevel.Moderate, [], AllFiles, false, false)
        {
            Kind = CleanupKind.Command,
            Command = CleanupCommand.ComponentCleanup,
            Requires = CleanupRequirement.Elevated | CleanupRequirement.NoPendingReboot | CleanupRequirement.NoUpdateInstalling,
            NeverScheduled = true,
            Hint = "The size comes from DISM's own analysis. Disk Cleanup can show a Windows Update Cleanup figure on Windows 11 24H2 that never goes away; trust DISM.",
        },
        new("windows.component.resetbase", "Reset the component store base", "DISM /StartComponentCleanup /ResetBase: also removes the superseded versions DISM keeps, so installed updates can no longer be uninstalled.",
            RiskLevel.Advanced, [], AllFiles, false, false)
        {
            Kind = CleanupKind.Command,
            Command = CleanupCommand.ComponentResetBase,
            Requires = CleanupRequirement.Elevated | CleanupRequirement.NoPendingReboot | CleanupRequirement.NoUpdateInstalling,
            NeverScheduled = true,
            Hint = "Included in the Windows Update cleanup size above.",
            ConfirmWarning = "ResetBase is permanent: no update installed so far can be uninstalled afterwards.",
        },
        new("restore.points", "Older restore points", "Every System Restore point except the newest, like Disk Cleanup's More Options. The newest one always stays.",
            RiskLevel.Advanced, [], AllFiles, false, false)
        {
            Kind = CleanupKind.Command,
            Command = CleanupCommand.RestorePointsKeepNewest,
            Requires = CleanupRequirement.Elevated,
            NeverScheduled = true,
            Hint = "To use less space for restore points in future, lower the maximum in System Protection.",
            ConfirmWarning = "Older restore points are deleted: you can only go back to the newest one afterwards.",
        },
        new("font.cache", "Font cache", "Cached font data. The Windows Font Cache service is paused while it is removed.",
            RiskLevel.Moderate, [@"%LOCALAPPDATA%\Microsoft\Windows\Fonts\Cache", @"%WINDIR%\ServiceProfiles\LocalService\AppData\Local\FontCache"], ["*.dat", "*.tmp"], false, false)
        {
            ExtraPaths =
            [
                new(@"%WINDIR%\ServiceProfiles\LocalService\AppData\Local", ["*FontCache*.dat"], false),
                new(@"%LOCALAPPDATA%", ["GDIPFONTCACHEV1.DAT"], false),
            ],
            // The service cache lives in LocalService's profile and the service can only be paused as administrator.
            Requires = CleanupRequirement.Elevated,
            Hint = "Fonts draw slowly until Windows rebuilds the cache; a restart may be needed.",
        },
        new("temp.internet.files", "Internet temporary files", "Legacy cache used by Internet Explorer mode and some older apps.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Microsoft\Windows\INetCache", @"%LOCALAPPDATA%\Microsoft\Windows\Temporary Internet Files"], AllFiles, true, true) { PruneAnywhere = true },
        new("windows.store.app.temp", "Store app temporary files", "Temporary files and web caches of Store apps. Their saved data, settings and sign-in stay.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Packages\*\AC\INetCache", @"%LOCALAPPDATA%\Packages\*\AC\Temp", @"%LOCALAPPDATA%\Packages\*\TempState"], AllFiles, true, true)
        {
            PruneAnywhere = true,
        },
        new("windows.installer.temp", "Windows Installer leftovers", "Rollback files left in Config.Msi by an install that did not finish, and Installer *.tmp files.",
            RiskLevel.Moderate, [@"%SYSTEMDRIVE%\Config.Msi"], AllFiles, false, true)
        {
            ExtraPaths = [new(@"%WINDIR%\Installer", ["*.tmp"], false)],
            ExcludePatterns = ["*.msi", "*.msp", "*.mst"],
            Requires = CleanupRequirement.Elevated | CleanupRequirement.NoInstallerRunning | CleanupRequirement.NoPendingFileRenames,
        },
        new("windows.installer.patchcache", "Windows Installer patch cache", "The $PatchCache$ baseline copies of MSI patches. Some patch uninstalls or repairs may then ask for the original media.",
            RiskLevel.Advanced, [@"%WINDIR%\Installer\$PatchCache$"], AllFiles, false, true)
        {
            ExcludePatterns = ["*.msi", "*.msp"],
            Requires = CleanupRequirement.Elevated | CleanupRequirement.NoInstallerRunning | CleanupRequirement.NoPendingFileRenames,
            NeverScheduled = true,
        },
        new("rdp.bitmap.cache", "Remote Desktop bitmap cache", "Screen pieces Remote Desktop saved to redraw faster.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Microsoft\Terminal Server Client\Cache"], ["bcache*.bmc", "cache*.bin"], true, false),
        new("old.chkdsk", "Old CHKDSK fragments", "found.### folders where CHKDSK put pieces of damaged files, removed through Windows' own Disk Cleanup. They can be parts of your own files: check the list first.",
            RiskLevel.Moderate, [@"%FIXEDDRIVES%\found.*"], AllFiles, false, true)
        {
            Kind = CleanupKind.Command,
            Command = CleanupCommand.DiskCleanupHandlers,
            DiskCleanupHandlers = ["Old ChkDsk Files"],
            Requires = CleanupRequirement.Elevated,
            NeverScheduled = true,
        },

        // Windows Disk Cleanup handlers: only Windows can remove these properly (ACLs, Tamper Protection, ActiveX).
        DiskCleanup("diskcleanup.upgrade.logs", "Windows upgrade log files", "Logs from Windows setup and upgrades.", RiskLevel.Moderate, "Windows Upgrade Log Files"),
        DiskCleanup("diskcleanup.downloaded.programs", "Downloaded program files", "Old ActiveX controls and Java applets from web pages, unregistered properly by Windows.", RiskLevel.Safe, "Downloaded Program Files", @"%WINDIR%\Downloaded Program Files"),
        DiskCleanup("diskcleanup.offline.pages", "Offline web pages", "Web pages saved for offline reading by old Internet Explorer features.", RiskLevel.Safe, "Offline Pages Files", @"%WINDIR%\Offline Web Pages"),
        DiskCleanup("diskcleanup.retaildemo", "Retail demo content", "Store-display demo videos and apps on PCs that came from a shop.", RiskLevel.Safe, "RetailDemo Offline Content", @"%PROGRAMDATA%\Microsoft\Windows\RetailDemo"),
        DiskCleanup("diskcleanup.diagnostic.data", "Diagnostic Data Viewer files", "The local database of the Diagnostic Data Viewer.", RiskLevel.Moderate, "Diagnostic Data Viewer database files"),
        DiskCleanup("diskcleanup.feedback.hub", "Feedback Hub archives", "Archived log files from Feedback Hub reports.", RiskLevel.Safe, "Feedback Hub Archive log files"),
        DiskCleanup("diskcleanup.defender", "Microsoft Defender temporary files", "Non-critical files Microsoft Defender no longer needs. Windows decides what to remove; quarantine and history stay.", RiskLevel.Safe, "Windows Defender"),
        DiskCleanup("diskcleanup.branchcache", "BranchCache", "Content cached for other PCs on a company network.", RiskLevel.Safe, "BranchCache"),

        // ---- Graphics drivers ---------------------------------------------------------------------------------
        new("dx.shader.cache", "DirectX shader cache", "Compiled graphics shaders. Games rebuild them on next launch.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\D3DSCache"], AllFiles, true, true) { Group = CleanupGroup.Graphics, PruneAnywhere = true },
        new("nvidia.shader.cache", "NVIDIA shader cache", "Compiled graphics shaders from the NVIDIA driver, including CUDA and Store game caches. Games rebuild them on next launch, with brief stutter.",
            RiskLevel.Safe,
            [
                @"%USERPROFILE%\AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache", @"%USERPROFILE%\AppData\LocalLow\NVIDIA\DXCache",
                @"%LOCALAPPDATA%\NVIDIA\DXCache", @"%LOCALAPPDATA%\NVIDIA\GLCache", @"%APPDATA%\NVIDIA\ComputeCache",
                @"%LOCALAPPDATA%\NVIDIA Corporation\NV_Cache", @"%PROGRAMDATA%\NVIDIA Corporation\NV_Cache",
                @"%LOCALAPPDATA%\Packages\*\AC\NVIDIA\DXCache", @"%LOCALAPPDATA%\Packages\*\AC\NVIDIA Corporation\NV_Cache",
            ],
            AllFiles, true, true)
        {
            Group = CleanupGroup.Graphics,
            PruneAnywhere = true,
            ExtraPaths = [new(SystemProfile + @"\LocalLow\NVIDIA\PerDriverVersion\*Cache", ElevatedOnly: true)],
            Hint = "Close your games first; shader files in use are skipped.",
        },
        new("amd.shader.cache", "AMD shader cache", "Compiled graphics shaders from the AMD Radeon driver (DirectX 9 to 12, OpenGL, Vulkan, Store and Game Pass games). Games rebuild them on next launch.",
            RiskLevel.Safe,
            [
                // Every *Cache folder (DxCache, DxcCache, Dx9Cache, GLCache, VkCache and future ones); AMD\CN (settings) never matches.
                @"%LOCALAPPDATA%\AMD\*Cache", @"%LOCALAPPDATA%\AMD\RadeonSoftware\vkcache",
                @"%LOCALAPPDATA%\Packages\*\AC\AMD\DxCache", @"%LOCALAPPDATA%\Packages\*\AC\*\AMD\DxCache",
            ],
            AllFiles, true, true)
        {
            Group = CleanupGroup.Graphics,
            PruneAnywhere = true,
            ExcludePathTemplates = [@"%LOCALAPPDATA%\AMD\CN"],
            Hint = "Close your games first. AMD Software can also do this itself: Graphics > Advanced > Reset Shader Cache.",
        },
        new("intel.shader.cache", "Intel shader cache", "Compiled graphics shaders from the Intel Arc and Iris driver. Games rebuild them on next launch.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Intel\ShaderCache", @"%USERPROFILE%\AppData\LocalLow\Intel\ShaderCache", @"%PROGRAMDATA%\Intel\ShaderCache"], AllFiles, true, true)
        {
            Group = CleanupGroup.Graphics,
            PruneAnywhere = true,
            ExtraPaths = [new(@"%WINDIR%\ServiceProfiles\LocalService\AppData\LocalLow\Intel\ShaderCache", ElevatedOnly: true)],
            Hint = "Close your games first; shader files in use are skipped.",
        },
        new("driver.leftovers.amd", "AMD driver installer files", "Files AMD's installer unpacked to C:\\AMD. Safe after the driver is installed and the PC has restarted.",
            RiskLevel.Moderate, [@"%SYSTEMDRIVE%\AMD"], AllFiles, false, true)
        {
            Group = CleanupGroup.Graphics,
            Requires = CleanupRequirement.NoPendingReboot,
            NeverScheduled = true,
            Hint = "Folders Windows needs to uninstall AMD software (newer chipset drivers keep their uninstaller here) are always kept.",
        },
        new("driver.leftovers.nvidia", "NVIDIA driver installer files", "Files the NVIDIA installer unpacked to C:\\NVIDIA and NVIDIA app update packages already installed.",
            RiskLevel.Safe, [@"%SYSTEMDRIVE%\NVIDIA", @"%PROGRAMDATA%\NVIDIA Corporation\NVIDIA App\UpdateFramework\ota-artifacts"], AllFiles, false, true)
        {
            Group = CleanupGroup.Graphics,
            PruneAnywhere = true,
            Requires = CleanupRequirement.NoPendingReboot,
            ExcludePathTemplates = [@"%PROGRAMFILES(X86)%\NVIDIA Corporation\Installer2"],
        },
        new("driver.downloads.nvidia", "NVIDIA driver downloads", "Driver packages GeForce Experience or the NVIDIA app downloaded. They are downloaded again if a reinstall needs them.",
            RiskLevel.Moderate, [@"%PROGRAMDATA%\NVIDIA Corporation\Downloader"], AllFiles, false, true)
        {
            Group = CleanupGroup.Graphics,
            Requires = CleanupRequirement.NoPendingReboot,
        },
        new("driver.leftovers.intel", "Intel driver installer files", "Intel graphics installer batch files, logs and dumps. The rest of C:\\Intel is left alone, because Intel and PC-maker tools keep data there.",
            RiskLevel.Moderate, [@"%SYSTEMDRIVE%\Intel\GfxCPLBatchFiles"], AllFiles, false, true)
        {
            Group = CleanupGroup.Graphics,
            ExtraPaths = [new(@"%SYSTEMDRIVE%\Intel\Logs", ["*.log"], false), new(@"%PROGRAMDATA%\Intel\GFXInstaller", ["*.log", "*.dmp"], true)],
        },
        new("amd.software.cache", "AMD Software logs and web cache", "Logs and web page caches of AMD Software: Adrenalin Edition and Ryzen Master. Settings stay.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\AMD\RadeonSoftware\Cache", @"%LOCALAPPDATA%\AMD\RadeonSoftware\QtWebEngine\Default\*Cache*", @"%LOCALAPPDATA%\RadeonSettings\Cache\QtWebEngine\Default\*Cache*", @"%LOCALAPPDATA%\AMD Ryzen Master\cache\qmlcache"], AllFiles, true, true)
        {
            Group = CleanupGroup.Graphics,
            PruneAnywhere = true,
            // Only the logs in AMD\CN; the settings next to them are never touched.
            ExtraPaths = [new(@"%LOCALAPPDATA%\AMD\CN", ["*.log"], false), new(@"%LOCALAPPDATA%\AMD\Fuel", ["ClientProxyLog*"], false)],
        },
        new("nvidia.app.cache", "NVIDIA app logs and web cache", "Logs, crash dumps and the web page cache of the NVIDIA app and driver.",
            RiskLevel.Safe, [@"%PROGRAMDATA%\NVIDIA Corporation\umdlogs", @"%PROGRAMDATA%\NVIDIA Corporation\CrashDumps", @"%LOCALAPPDATA%\NVIDIA Corporation\NVIDIA App\CefCache\*cache*"], AllFiles, true, true)
        {
            Group = CleanupGroup.Graphics,
            PruneAnywhere = true,
            ExtraPaths =
            [
                new(@"%PROGRAMDATA%\NVIDIA Corporation\NvTelemetry", ["*.log"], false),
                new(@"%PROGRAMDATA%\NVIDIA", ["*.log*"], false),
                new(@"%LOCALAPPDATA%\NVIDIA Corporation\NVIDIA App", ["*.log"], false),
            ],
        },
        new("intel.graphics.logs", "Intel Graphics Command Center logs", "Log files of the Intel graphics control panel.",
            RiskLevel.Safe, [@"%LOCALAPPDATA%\Intel\GCC"], ["*log*"], true, true) { Group = CleanupGroup.Graphics },

        // ---- Browsers -----------------------------------------------------------------------------------------
        Browser("browser.chrome.cache", "Google Chrome cache", "Chrome", ChromiumTemplates(ChromeUserData)),
        Browser("browser.edge.cache", "Microsoft Edge cache", "Edge", ChromiumTemplates(EdgeUserData)),
        Browser("browser.brave.cache", "Brave cache", "Brave", ChromiumTemplates(BraveUserData)),
        Browser("browser.firefox.cache", "Mozilla Firefox cache", "Firefox",
        [
            @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2", @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\startupCache",
            @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\thumbnails", @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\jumpListCache",
            @"%APPDATA%\Mozilla\Firefox\Profiles\*\shader-cache",
            @"%APPDATA%\Mozilla\Firefox\Crash Reports\pending", @"%APPDATA%\Mozilla\Firefox\Crash Reports\submitted",
            // Firefox from the Microsoft Store (MSIX).
            @"%LOCALAPPDATA%\Packages\Mozilla.Firefox_n80bbvh6b1yt2\LocalCache\Local\Mozilla\Firefox\Profiles\*\cache2",
        ]),
        Browser("browser.opera.cache", "Opera cache", "Opera", OperaTemplates("Opera*")) with
        {
            ExcludePathTemplates = [@"%LOCALAPPDATA%\Opera Software\Opera GX*", @"%APPDATA%\Opera Software\Opera GX*"],
        },
        Browser("browser.operagx.cache", "Opera GX cache", "Opera GX", OperaTemplates("Opera GX*")),
        Browser("browser.vivaldi.cache", "Vivaldi cache", "Vivaldi", ChromiumTemplates(VivaldiUserData)),
        Browser("browser.chromium.cache", "Chromium cache", "Chromium", ChromiumTemplates(ChromiumUserData)),
        Browser("browser.waterfox.cache", "Waterfox cache", "Waterfox", FirefoxForkTemplates(@"%LOCALAPPDATA%\Waterfox\Profiles", @"%APPDATA%\Waterfox\Profiles")),
        Browser("browser.librewolf.cache", "LibreWolf cache", "LibreWolf",
        [
            .. FirefoxForkTemplates(@"%LOCALAPPDATA%\librewolf\Profiles", @"%APPDATA%\librewolf\Profiles"),
            @"%LOCALAPPDATA%\Packages\31856maltejur.LibreWolf_ssmwz6s360tct\LocalCache\Local\librewolf\Profiles\*\cache2",
        ]),
        Browser("browser.floorp.cache", "Floorp cache", "Floorp", FirefoxForkTemplates(@"%LOCALAPPDATA%\Floorp\Profiles", @"%APPDATA%\Floorp\Profiles")),
        Browser("browser.zen.cache", "Zen Browser cache", "Zen", FirefoxForkTemplates(@"%LOCALAPPDATA%\zen\Profiles", @"%APPDATA%\zen\Profiles")),
        Browser("browser.arc.cache", "Arc cache", "Arc", ChromiumTemplates(ArcUserData)),
        Browser("browser.yandex.cache", "Yandex Browser cache", "Yandex Browser", ChromiumTemplates(YandexUserData)),

        // ---- Apps ---------------------------------------------------------------------------------------------
        App("app.thunderbird.cache", "Thunderbird cache", "Web content and startup caches of Thunderbird. Your mail is never touched.",
            [@"%LOCALAPPDATA%\Thunderbird\Profiles\*\cache2", @"%LOCALAPPDATA%\Thunderbird\Profiles\*\startupCache"]) with
        {
            ExcludePathTemplates = [@"%APPDATA%\Thunderbird\Profiles\*\Mail", @"%APPDATA%\Thunderbird\Profiles\*\ImapMail"],
        },
        App("app.discord.cache", "Discord cache", "Cache, logs, crash reports and update packages of Discord, PTB and Canary. You stay signed in; the next Discord update downloads in full.",
            [
                @"%APPDATA%\discord*\Cache\Cache_Data", @"%APPDATA%\discord*\Code Cache", @"%APPDATA%\discord*\GPUCache",
                @"%APPDATA%\discord*\Crashpad\reports", @"%APPDATA%\discord*\logs", @"%LOCALAPPDATA%\Discord*\packages\SquirrelTemp",
            ]) with
        {
            ExtraPaths = [new(@"%LOCALAPPDATA%\Discord*\packages", ["*.nupkg"], false)],
            ExcludePatterns = ["settings.json"],
            ExcludePathTemplates = [@"%APPDATA%\discord*\Local Storage", @"%APPDATA%\discord*\IndexedDB"],
        },
        App("app.teams.cache", "Microsoft Teams cache", "Web caches of new and classic Teams. Only cache folders are removed, so you stay signed in.",
            [
                @"%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\*\Cache\Cache_Data",
                @"%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\*\Code Cache",
                @"%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\*\GPUCache",
                @"%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\*\Service Worker\CacheStorage",
                @"%APPDATA%\Microsoft\Teams\Cache", @"%APPDATA%\Microsoft\Teams\Code Cache", @"%APPDATA%\Microsoft\Teams\GPUCache",
                @"%APPDATA%\Microsoft\Teams\blob_storage", @"%APPDATA%\Microsoft\Teams\Service Worker\CacheStorage", @"%APPDATA%\Microsoft\Teams\tmp",
            ]) with
        {
            ExtraPaths = [new(@"%APPDATA%\Microsoft\Teams", ["logs.txt"], false)],
        },
        App("app.spotify.cache", "Spotify streaming cache", "Music Spotify cached while streaming. Songs you downloaded for offline listening are never touched.",
            [@"%LOCALAPPDATA%\Spotify\Data", @"%LOCALAPPDATA%\Spotify\Browser", @"%LOCALAPPDATA%\Packages\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\LocalCache\Spotify\Data"]) with
        {
            ExcludePathTemplates = [@"%LOCALAPPDATA%\Spotify\Storage", @"%LOCALAPPDATA%\Packages\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\LocalState\Spotify\Storage"],
        },
        App("app.office.cache", "Microsoft Office caches", "Office telemetry, diagnostics and web service caches. Documents, recovery files and Outlook data stay.",
            [@"%LOCALAPPDATA%\Microsoft\Office\OTele", @"%LOCALAPPDATA%\Microsoft\Office\OffDiag", @"%LOCALAPPDATA%\Microsoft\Office\16.0\WebServiceCache", @"%LOCALAPPDATA%\Microsoft\Office\*\BackstageInAppNavCache"]) with
        {
            ExcludePathTemplates = [@"%LOCALAPPDATA%\Microsoft\Office\UnsavedFiles", @"%LOCALAPPDATA%\Microsoft\Outlook\RoamCache", @"%LOCALAPPDATA%\Microsoft\OneNote"],
        },
        new("app.office.filecache", "Office upload and add-in cache", "The Office upload cache (co-authoring) and the add-in cache of your account. Add-ins download again.",
            RiskLevel.Advanced, [@"%LOCALAPPDATA%\Microsoft\Office\16.0\OfficeFileCache", @"%LOCALAPPDATA%\Microsoft\Office\16.0\Wef"], AllFiles, false, true)
        {
            Group = CleanupGroup.Apps,
            NeverScheduled = true,
            CurrentUserOnly = true,
            Hint = "Office's own Delete cached files button (Upload Center or Word Options, Save) does the same with Office's checks.",
            ConfirmWarning = "Check first that Office and OneDrive show no pending uploads: unsynced changes in the Office upload cache would be lost.",
        },
        App("app.vscode.cache", "Visual Studio Code cache", "Caches, logs, crash reports and downloaded extension packages of VS Code. Settings and extensions stay.",
            [@"%APPDATA%\Code\Cache", @"%APPDATA%\Code\CachedData", @"%APPDATA%\Code\Code Cache", @"%APPDATA%\Code\GPUCache", @"%APPDATA%\Code\CachedExtensionVSIXs", @"%APPDATA%\Code\logs", @"%APPDATA%\Code\Crashpad\reports"]),
        App("app.slack.cache", "Slack cache", "Web caches and logs of the Slack desktop app. You stay signed in.",
            [@"%APPDATA%\Slack\Cache", @"%APPDATA%\Slack\Code Cache", @"%APPDATA%\Slack\GPUCache", @"%APPDATA%\Slack\Service Worker\CacheStorage", @"%APPDATA%\Slack\logs"]),
        App("app.zoom.logs", "Zoom logs", "Log files of the Zoom desktop app.", [@"%APPDATA%\Zoom\logs"]),
        App("app.obs.logs", "OBS Studio logs", "Log files and crash reports of OBS Studio.", [@"%APPDATA%\obs-studio\logs", @"%APPDATA%\obs-studio\crashes"]),
        new("app.adobe.mediacache", "Adobe media cache", "Conformed audio and video Premiere Pro and After Effects cache. They rebuild it when a project opens again, which takes time.",
            RiskLevel.Moderate, [@"%APPDATA%\Adobe\Common\Media Cache Files", @"%APPDATA%\Adobe\Common\Media Cache"], AllFiles, false, true)
        {
            Group = CleanupGroup.Apps,
        },

        // ---- Game launchers -----------------------------------------------------------------------------------
        Game("game.steam.cache", "Steam cache and logs", "Steam's web cache, logs, crash dumps and client update packages. Games and downloads are never touched; Steam downloads its client packages again at the next start.",
            [@"%LOCALAPPDATA%\Steam\htmlcache", @"%STEAM%\appcache\httpcache", @"%STEAM%\logs", @"%STEAM%\dumps", @"%STEAMLIBRARIES%\steamapps\temp"]) with
        {
            ExtraPaths = [new(@"%STEAM%\package", ["*.zip.*"], false)],
            ExcludePathTemplates = [@"%STEAMLIBRARIES%\steamapps\downloading", @"%STEAMLIBRARIES%\steamapps\common"],
        },
        new("game.steam.depotcache", "Steam depot cache", "Download manifests Steam keeps. Steam fetches them again when needed.",
            RiskLevel.Moderate, [@"%STEAM%\depotcache"], AllFiles, false, true)
        {
            Group = CleanupGroup.Games,
        },
        new("game.steam.downloading", "Abandoned Steam downloads", "Partial downloads of games that are no longer installed or queued. Active and paused downloads are always kept.",
            RiskLevel.Advanced, [@"%STEAMLIBRARIES%\steamapps\downloading"], AllFiles, false, true)
        {
            Group = CleanupGroup.Games,
            NeverScheduled = true,
            // Download state files sit next to the folders; they are tiny and Steam manages them.
            ExcludePatterns = ["state_*"],
            ConfirmWarning = "Partial Steam downloads are thrown away and would have to download again.",
        },
        new("game.steam.shadercache", "Steam shader pre-caches", "Per-game shader caches in every Steam library (including Vulkan and DXVK caches). Games stutter until they are rebuilt and Steam downloads pre-caches again.",
            RiskLevel.Moderate, [@"%STEAMLIBRARIES%\steamapps\shadercache"], AllFiles, false, true)
        {
            Group = CleanupGroup.Games,
            Hint = "Close Steam and your games first.",
        },
        Game("game.epic.cache", "Epic Games Launcher cache", "The launcher's web caches (what Epic support tells you to delete), logs and crash reports. Installed games stay.",
            [
                @"%LOCALAPPDATA%\EpicGamesLauncher\Saved\webcache*", @"%LOCALAPPDATA%\EpicGamesLauncher\Saved\Logs", @"%LOCALAPPDATA%\EpicGamesLauncher\Saved\Crashes",
                @"%LOCALAPPDATA%\EpicGames\eosoverlay\BrowserCache", @"%LOCALAPPDATA%\CrashReportClient\Saved\Logs", @"%PROGRAMDATA%\Epic\EpicOnlineServices\*\Logs",
            ]) with
        {
            ExcludePathTemplates = [@"%PROGRAMDATA%\Epic\EpicGamesLauncher\Data\Manifests"],
        },
        Game("game.battlenet.cache", "Battle.net cache", "Caches and logs of the Battle.net app and its update agent.",
            [
                @"%LOCALAPPDATA%\Battle.net\Cache", @"%LOCALAPPDATA%\Battle.net\BrowserCaches", @"%LOCALAPPDATA%\Battle.net\Logs", @"%LOCALAPPDATA%\Battle.net\Errors",
                @"%LOCALAPPDATA%\Blizzard Entertainment\Battle.net\Cache", @"%PROGRAMDATA%\Battle.net\Agent\data\*cache",
                @"%PROGRAMDATA%\Battle.net\Agent\Agent.*\Logs", @"%PROGRAMDATA%\Blizzard Entertainment\Battle.net\Cache",
            ]),
        Game("game.ea.cache", "EA app cache", "Caches and logs of the EA app (and old Origin logs).",
            [
                @"%LOCALAPPDATA%\Electronic Arts\EA Desktop\*Cache", @"%LOCALAPPDATA%\Electronic Arts\EA Desktop\Logs", @"%LOCALAPPDATA%\EADesktop\cache",
                @"%LOCALAPPDATA%\EADesktop\QtWebEngine\Default\*cache", @"%PROGRAMDATA%\EA Desktop\Logs", @"%PROGRAMDATA%\EA Logs", @"%PROGRAMDATA%\Origin\Logs",
            ]),
        Game("game.ubisoft.cache", "Ubisoft Connect cache", "Web cache and logs of Ubisoft Connect. Ownership, settings and configuration data stay.",
            [@"%UBISOFT%\cache\http*", @"%UBISOFT%\cache\assets", @"%UBISOFT%\logs"]) with
        {
            ExtraPaths = [new("%UBISOFT%", ["*.log"], false)],
            ExcludePathTemplates = [@"%UBISOFT%\cache\ownership", @"%UBISOFT%\cache\configuration", @"%UBISOFT%\cache\settings"],
        },
        new("game.ubisoft.installers", "Ubisoft Connect installers", "Game installer files Ubisoft Connect keeps. Only remove them after every game was started once.",
            RiskLevel.Advanced, [@"%UBISOFT%\cache\installers"], AllFiles, false, true)
        {
            Group = CleanupGroup.Games,
            NeverScheduled = true,
        },
        Game("game.gog.cache", "GOG Galaxy cache", "Web cache, logs, crash dumps and updater leftovers of GOG Galaxy.",
            [@"%PROGRAMDATA%\GOG.com\Galaxy\webcache\common\*cache*", @"%PROGRAMDATA%\GOG.com\Galaxy\logs", @"%PROGRAMDATA%\GOG.com\Galaxy\crashdumps", @"%PROGRAMDATA%\GOG.com\Galaxy\temp\desktop-galaxy-updater"]),

        // ---- Developer tools (Advanced: builds download their dependencies again) ------------------------------
        Dev("dev.npm.cache", "npm and Yarn caches", "Like npm cache clean --force and the Yarn v1 cache. Offline installs fail until packages download again.",
            [@"%LOCALAPPDATA%\npm-cache\_cacache", @"%LOCALAPPDATA%\npm-cache\_logs", @"%LOCALAPPDATA%\npm-cache\_npx", @"%LOCALAPPDATA%\Yarn\Cache"]),
        Dev("dev.pip.cache", "pip cache", "Like pip cache purge. Python packages download again when next installed.", [@"%LOCALAPPDATA%\pip\Cache"]),
        new("dev.nuget.http", "NuGet HTTP cache", "Like dotnet nuget locals http-cache --clear. Package lists download again.",
            RiskLevel.Moderate, [@"%LOCALAPPDATA%\NuGet\v3-cache", @"%LOCALAPPDATA%\NuGet\plugins-cache"], AllFiles, false, true)
        {
            Group = CleanupGroup.Developer,
            PruneAnywhere = true,
        },
        Dev("dev.nuget.packages", "NuGet global packages", "The .nuget\\packages folder, often several GB. Offline builds break until the next dotnet restore.", [@"%USERPROFILE%\.nuget\packages"]),
        Dev("dev.java.cache", "Java caches", "The Java Web Start and installer caches.",
            [@"%USERPROFILE%\AppData\LocalLow\Sun\Java\Deployment\cache", @"%LOCALAPPDATA%\Sun\Java\Deployment\*Cache", @"%PROGRAMDATA%\Oracle\Java\installcache"]),
        Dev("dev.dotnet.temp", ".NET temporary files", ".NET CLI telemetry storage, Temporary ASP.NET Files and the .NET Framework setup cache. Repairing .NET Framework may then ask for its installer.",
            [@"%USERPROFILE%\.dotnet\TelemetryStorageService", @"%WINDIR%\Microsoft.NET\Framework*\v*\Temporary ASP.NET Files", @"%WINDIR%\Microsoft.NET\Framework*\v4.0.30319\SetupCache"]),
        Dev("dev.gradle.cache", "Gradle caches", "The Gradle caches folder. Builds download their dependencies again.", [@"%USERPROFILE%\.gradle\caches"]),
        Dev("dev.maven.repository", "Maven repository", "The local Maven repository. Builds download their dependencies again.", [@"%USERPROFILE%\.m2\repository"]),
        Dev("dev.go.cache", "Go build cache", "Like go clean -cache. Builds take longer the next time.", [@"%LOCALAPPDATA%\go-build"]),
        Dev("dev.cargo.cache", "Cargo registry cache", "Downloaded crate archives. Builds download them again.", [@"%USERPROFILE%\.cargo\registry\cache"]),

        // ---- Privacy (opt-in, never scheduled, almost no space) ---------------------------------------------------
        Privacy("privacy.recent.items", "Recent items", "The list of recently opened files in File Explorer and Start.", RiskLevel.Safe,
            [@"%APPDATA%\Microsoft\Windows\Recent"], ["*.lnk"]),
        Privacy("privacy.jump.lists", "Taskbar jump lists", "Recent and frequent items in taskbar jump lists. Items you pinned to a jump list are lost too.", RiskLevel.Moderate,
            [@"%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations", @"%APPDATA%\Microsoft\Windows\Recent\CustomDestinations"], ["*.automaticDestinations-ms", "*.customDestinations-ms"]),
        Privacy("privacy.office.recent", "Office recent documents", "Office's list of recently opened documents.", RiskLevel.Safe,
            [@"%APPDATA%\Microsoft\Office\Recent"], AllFiles),
        new("privacy.clipboard", "Clipboard and clipboard history", "Empties the clipboard and the Win+V history. Pinned items stay. Frees no disk space.",
            RiskLevel.Safe, [], AllFiles, false, false)
        {
            Group = CleanupGroup.Privacy,
            Kind = CleanupKind.Command,
            Command = CleanupCommand.ClipboardClear,
            NeverScheduled = true,
            CurrentUserOnly = true,
        },
        new("browser.cookies", "Browser cookies", "Cookies of every supported browser.",
            RiskLevel.Advanced, [], ["Cookies", "Cookies-journal", "cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm"], false, false)
        {
            Group = CleanupGroup.Privacy,
            NeverScheduled = true,
            CurrentUserOnly = true,
            ExtraPaths =
            [
                .. ChromiumProfileFiles(["Cookies", "Cookies-journal"], includeNetwork: true),
                new(@"%APPDATA%\Opera Software\Opera*\Network", ["Cookies", "Cookies-journal"], false),
                new(@"%APPDATA%\Opera Software\Opera*", ["Cookies", "Cookies-journal"], false),
                .. FirefoxProfileFiles("cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm"),
            ],
            ExcludePatterns = BrowserProtectedFiles,
            Hint = "Close every browser first.",
            ConfirmWarning = "You will be signed out of all websites in your account's browsers.",
        },
        new("browser.history", "Browser history", "Browsing and download history of Chromium browsers, and search and form history of Firefox-family browsers. Bookmarks stay.",
            RiskLevel.Moderate, [], AllFiles, false, false)
        {
            Group = CleanupGroup.Privacy,
            NeverScheduled = true,
            CurrentUserOnly = true,
            ExtraPaths =
            [
                .. ChromiumProfileFiles(["History", "History-journal", "Visited Links", "Top Sites*", "Shortcuts*", "Network Action Predictor*"], includeNetwork: false),
                // places.sqlite holds bookmarks as well as history, so Firefox history itself needs a database step (later).
                .. FirefoxProfileFiles("formhistory.sqlite", "formhistory.sqlite-wal", "formhistory.sqlite-shm"),
            ],
            ExcludePatterns = BrowserProtectedFiles,
            Hint = "Close every browser first. Browser sync can bring history back.",
        },
        new("browser.session", "Browser saved sessions", "The tabs a browser reopens after a restart.",
            RiskLevel.Moderate, [], AllFiles, false, false)
        {
            Group = CleanupGroup.Privacy,
            NeverScheduled = true,
            CurrentUserOnly = true,
            ExtraPaths =
            [
                .. AllChromiumUserData.Select(root => new CleanupPath($@"{root}\*\Sessions", AllFiles, true)),
                .. FirefoxProfileFiles("sessionstore.jsonlz4"),
                .. FirefoxFamilyRoamingProfiles.Select(p => new CleanupPath($@"{p}\sessionstore-backups", AllFiles, false)),
            ],
            ExcludePatterns = BrowserProtectedFiles,
            Hint = "Close every browser first. Open tabs will not be restored.",
        },
    ];

    private static CleanupCategory DiskCleanup(string id, string name, string description, RiskLevel risk, string handler, string? measuredFolder = null) =>
        new(id, name, description + " Removed through Windows' own Disk Cleanup.", risk, measuredFolder is null ? [] : [measuredFolder], AllFiles, false, true)
        {
            Kind = CleanupKind.Command,
            Command = CleanupCommand.DiskCleanupHandlers,
            DiskCleanupHandlers = [handler],
            Requires = CleanupRequirement.Elevated,
            // Scheduled and tray cleanups never start a Windows tool.
            NeverScheduled = true,
        };

    private static CleanupCategory Browser(string id, string name, string browser, string[] templates) =>
        new(id, name, $"Web pages, images and compiled code {browser} saved for faster loading. Skipped while {browser} is running.",
            RiskLevel.Safe, templates, AllFiles, true, true)
        {
            Group = CleanupGroup.Browsers,
            PruneAnywhere = true,
            ExcludePatterns = BrowserProtectedFiles,
        };

    private static CleanupCategory App(string id, string name, string description, string[] templates) =>
        new(id, name, description, RiskLevel.Safe, templates, AllFiles, true, true) { Group = CleanupGroup.Apps, PruneAnywhere = true };

    private static CleanupCategory Game(string id, string name, string description, string[] templates) =>
        new(id, name, description, RiskLevel.Safe, templates, AllFiles, true, true) { Group = CleanupGroup.Games, PruneAnywhere = true };

    private static CleanupCategory Dev(string id, string name, string description, string[] templates) =>
        new(id, name, description, RiskLevel.Advanced, templates, AllFiles, false, true)
        {
            Group = CleanupGroup.Developer,
            PruneAnywhere = true,
            NeverScheduled = true,
        };

    private static CleanupCategory Privacy(string id, string name, string description, RiskLevel risk, string[] templates, string[] patterns) =>
        new(id, name, description, risk, templates, patterns, false, false) { Group = CleanupGroup.Privacy, NeverScheduled = true, CurrentUserOnly = true };
}
