using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>Demo junk-file scan for the sample gaming desktop: believable sizes, file names and notes per category.</summary>
public sealed class DemoTempFileScanner : ITempFileScanner
{
    private const string User = @"C:\Users\Connor";

    private sealed record Sample(int Files, long Bytes, string Folder, string[] Names, string[] Notes);

    private static readonly Dictionary<string, Sample> Samples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user.temp"] = new(412, 1_380_000_000, User + @"\AppData\Local\Temp",
            ["msedge_installer.log", "nvidia_install_temp_{0}.tmp", "~DF{0:X6}.tmp", "wct{0:X4}.tmp", "Setup Log {0}.txt", "chrome_Unpacked_{0}\\manifest.json", "steam_shader_{0}.bin", "DiscordSetup_{0}.tmp"],
            []),
        ["windows.temp"] = new(96, 240_000_000, @"C:\Windows\Temp",
            ["MpCmdRun.log", "vmware-SYSTEM\\vmware-{0}.log", "dd_vcredist_{0}.log", "DMI{0:X4}.tmp", "CBS\\CbsPersist_{0}.cab", "MSI{0:X4}.LOG"],
            []),
        ["windows.update.cache"] = new(38, 2_900_000_000, @"C:\Windows\SoftwareDistribution\Download",
            ["{0:x8}\\windows11.0-kb5044284-x64.cab", "{0:x8}\\Windows11.0-KB5043080-x64.psf", "{0:x8}\\defender-dpl-update.exe", "SharedFileCache\\{0:x8}.psf"],
            []),
        ["delivery.optimization"] = new(12, 1_100_000_000, @"C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache",
            ["{0:x8}-{0:x4}.do", "{0:x8}.cache"],
            []),
        ["thumbnails"] = new(9, 310_000_000, User + @"\AppData\Local\Microsoft\Windows\Explorer",
            ["thumbcache_256.db", "thumbcache_1024.db", "thumbcache_wide.db", "thumbcache_sr.db", "thumbcache_idx.db", "thumbcache_96.db", "thumbcache_48.db", "thumbcache_32.db", "thumbcache_16.db"],
            []),
        ["icon.cache"] = new(14, 42_000_000, User + @"\AppData\Local\Microsoft\Windows\Explorer",
            ["iconcache_256.db", "iconcache_48.db", "iconcache_32.db", "iconcache_16.db", "iconcache_idx.db", "iconcache_wide.db"],
            []),
        ["recycle.bin"] = new(57, 4_600_000_000, @"C:\$Recycle.Bin",
            [],
            ["Emptied through Windows; individual files are not listed."]),
        ["browser.chrome.cache"] = new(3120, 1_750_000_000, User + @"\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data",
            ["f_{0:x6}", "data_{0}", "index"],
            []),
        ["browser.edge.cache"] = new(0, 0, User + @"\AppData\Local\Microsoft\Edge\User Data\Default\Cache\Cache_Data",
            [],
            ["Skipped: Microsoft Edge is running. Close it and scan again to clean its cache."]),
        ["browser.brave.cache"] = new(0, 0, User + @"\AppData\Local\BraveSoftware\Brave-Browser\User Data", [], []),
        ["browser.firefox.cache"] = new(0, 0, User + @"\AppData\Local\Mozilla\Firefox\Profiles", [], []),
        ["crash.dumps"] = new(3, 1_460_000_000, @"C:\Windows\Minidump",
            ["092825-9843-01.dmp", "081425-10203-01.dmp", "..\\MEMORY.DMP"],
            []),
        ["windows.error.reports"] = new(24, 86_000_000, @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue",
            ["AppCrash_Overwatch.exe_{0:x8}\\WER{0:X4}.tmp.dmp", "Kernel_141_{0:x8}\\Report.wer", "NonCritical_{0:x8}\\WERInternalMetadata.xml"],
            []),
        ["windows.logs"] = new(210, 530_000_000, @"C:\Windows\Logs",
            ["CBS\\CBS.log", "CBS\\CbsPersist_{0}.log", "DISM\\dism.log", "WindowsUpdate\\WindowsUpdate.{0}.etl", "NetSetup\\service.0.etl", "..\\Panther\\setupact.log"],
            []),
        ["dx.shader.cache"] = new(210, 980_000_000, User + @"\AppData\Local\D3DSCache",
            ["{0:x16}\\{0:x8}.idx", "{0:x16}\\{0:x8}.bin", "{0:x16}\\{0:x8}.val"],
            []),
        ["nvidia.shader.cache"] = new(330, 2_200_000_000, User + @"\AppData\Local\NVIDIA\DXCache",
            ["{0:x16}.toc", "{0:x16}.bin", "..\\GLCache\\{0:x8}\\{0:x8}.bin"],
            []),
        ["amd.shader.cache"] = new(0, 0, User + @"\AppData\Local\AMD\DxCache", [], []),
        ["intel.shader.cache"] = new(0, 0, User + @"\AppData\Local\Intel\ShaderCache", [], []),
        ["windows.old"] = new(41_212, 8_100_000_000, @"C:\Windows.old",
            ["Windows\\System32\\DriverStore\\FileRepository\\nv_dispi.inf_amd64_{0:x8}\\nvlddmkm.sys", "Windows\\WinSxS\\amd64_microsoft-windows-{0:x8}\\wow64.dll", "Users\\Connor\\AppData\\Local\\Temp\\{0:x8}.tmp"],
            []),
        ["windows.upgrade.leftovers"] = new(2_318, 4_300_000_000, @"C:\$WINDOWS.~BT",
            ["Sources\\install.esd", "Sources\\SafeOS\\winre.wim", "Sources\\Panther\\setupact.log", "..\\ESD\\Windows\\sources\\{0:x8}.cab"],
            ["Windows was upgraded 6 days ago. For 10 days after an upgrade these files are needed to go back to the previous version."]),
        ["windows.store.app.temp"] = new(640, 210_000_000, User + @"\AppData\Local\Packages",
            ["Microsoft.WindowsStore_8wekyb3d8bbwe\\AC\\INetCache\\{0:X8}\\banner[{0}].jpg", "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe\\AC\\Temp\\{0:x8}.tmp", "Microsoft.Windows.Photos_8wekyb3d8bbwe\\TempState\\{0:x8}.jpg"],
            []),
        ["driver.leftovers.amd"] = new(1_942, 2_650_000_000, @"C:\AMD",
            ["AMD-Software-Installer\\Packages\\Drivers\\Display\\WT6A_INF\\{0:x8}.dll", "Radeon-Software-Adrenalin-24.{0}\\Packages\\{0:x8}.cab", "AMD-Software-Installer\\Bin64\\cncmd.exe"],
            ["Kept C:\\AMD\\Chipset_Software: the AMD chipset driver needs it to uninstall."]),
        ["amd.software.cache"] = new(318, 96_000_000, User + @"\AppData\Local\AMD",
            ["CN\\gmdb.log", "CN\\NewsFeed\\{0:x8}.log", "RadeonSoftware\\Cache\\{0:x8}", "RadeonSoftware\\QtWebEngine\\Default\\Cache\\data_{0}"],
            []),
        ["driver.leftovers.nvidia"] = new(712, 1_240_000_000, @"C:\NVIDIA\DisplayDriver",
            ["566.36\\Win11_Win10-DCH_64\\International\\Display.Driver\\nvlddmkm.sy_", "566.36\\Win11_Win10-DCH_64\\International\\PhysX\\PhysX_{0}.msi", "572.16\\Win11_Win10-DCH_64\\International\\setup.exe"],
            []),
        ["driver.downloads.nvidia"] = new(3, 1_870_000_000, @"C:\ProgramData\NVIDIA Corporation\Downloader",
            ["{0:x8}\\566.36-desktop-win10-win11-64bit-international-dch-whql.exe", "{0:x8}\\572.16-desktop-win10-win11-64bit-international-dch-whql.exe"],
            []),
        ["nvidia.app.cache"] = new(144, 58_000_000, @"C:\ProgramData\NVIDIA Corporation",
            ["umdlogs\\{0:x8}.log", "CrashDumps\\nvcontainer.exe.{0}.dmp", "NvTelemetry\\NvTelemetryContainer.log"],
            []),
        ["browser.operagx.cache"] = new(2_410, 920_000_000, User + @"\AppData\Local\Opera Software\Opera GX Stable\Cache\Cache_Data",
            ["f_{0:x6}", "data_{0}", "index"],
            []),
        ["app.discord.cache"] = new(0, 0, User + @"\AppData\Roaming\discord\Cache\Cache_Data",
            [],
            ["Skipped: Discord is running. Quit it from the system tray and scan again to clean this."]),
        ["app.spotify.cache"] = new(1_208, 3_100_000_000, User + @"\AppData\Local\Spotify\Data",
            ["{0:x2}\\{0:x8}.file", "{0:x2}\\{0:x8}.cnt"],
            []),
        ["app.teams.cache"] = new(860, 470_000_000, User + @"\AppData\Local\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\WV2Profile_tfw",
            ["Cache\\Cache_Data\\f_{0:x6}", "Code Cache\\js\\{0:x8}_0", "Service Worker\\CacheStorage\\{0:x8}\\{0:x8}_0"],
            []),
        ["game.steam.cache"] = new(1_390, 1_420_000_000, @"C:\Program Files (x86)\Steam",
            ["appcache\\httpcache\\{0:x2}\\{0:x8}", "logs\\content_log.txt", "dumps\\steamwebhelper_{0}.dmp"],
            []),
        ["game.steam.shadercache"] = new(4_820, 5_600_000_000, @"D:\SteamLibrary\steamapps\shadercache",
            ["1172470\\DX12\\{0:x8}.bin", "1086940\\fozpipelinesv6\\steamapp_pipeline_cache.foz", "730\\nvidiav1\\GLCache\\{0:x8}.bin"],
            []),
        ["game.epic.cache"] = new(522, 380_000_000, User + @"\AppData\Local\EpicGamesLauncher\Saved",
            ["webcache_4430\\Cache\\Cache_Data\\f_{0:x6}", "Logs\\EpicGamesLauncher-backup-{0}.log", "Crashes\\UECC-Windows-{0:X8}\\minidump.dmp"],
            []),
        ["game.battlenet.cache"] = new(96, 120_000_000, @"C:\ProgramData\Battle.net\Agent",
            ["Agent.9{0}\\Logs\\Agent-{0}.log", "data\\cache\\{0:x2}\\{0:x8}"],
            []),
        ["app.vscode.cache"] = new(780, 310_000_000, User + @"\AppData\Roaming\Code",
            ["CachedData\\{0:x8}\\chrome\\js\\{0:x8}_0", "CachedExtensionVSIXs\\ms-python.python-2024.{0}.0", "logs\\{0}\\main.log"],
            []),
        ["dev.nuget.packages"] = new(18_340, 6_200_000_000, User + @"\.nuget\packages",
            ["microsoft.netcore.app.runtime.win-x64\\8.0.{0}\\runtimes\\win-x64\\lib\\net8.0\\System.Private.CoreLib.dll", "communitytoolkit.mvvm\\8.2.{0}\\lib\\net6.0\\CommunityToolkit.Mvvm.dll"],
            []),
        ["dev.npm.cache"] = new(9_120, 1_900_000_000, User + @"\AppData\Local\npm-cache\_cacache",
            ["content-v2\\sha512\\{0:x2}\\{0:x8}", "index-v5\\{0:x2}\\{0:x8}"],
            []),
        ["privacy.recent.items"] = new(212, 300_000, User + @"\AppData\Roaming\Microsoft\Windows\Recent",
            ["report-{0}.docx.lnk", "screenshot-{0}.png.lnk", "Downloads.lnk"],
            []),
        ["font.cache"] = new(4, 61_000_000, User + @"\AppData\Local\Microsoft\Windows\Fonts\Cache",
            ["FontCache-S-1-5-21-{0}.dat", "FontCache-System.dat", "~FontCache-{0}.tmp"],
            []),
        ["temp.internet.files"] = new(1_180, 390_000_000, User + @"\AppData\Local\Microsoft\Windows\INetCache",
            ["IE\\{0:X8}\\favicon[1].ico", "IE\\{0:X8}\\script[{0}].js", "IE\\{0:X8}\\image[{0}].png", "Content.Outlook\\{0:X8}\\attachment.pdf"],
            []),
    };

    public IReadOnlyList<CleanupCategory> Categories => CleanupCatalog.All;

    public async Task<CleanupScanResult> ScanAsync(IEnumerable<string> categoryIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(categoryIds ?? [], StringComparer.OrdinalIgnoreCase);
        var categories = Categories.Where(c => wanted.Count == 0 || wanted.Contains(c.Id)).ToList();
        var results = new List<CategoryScanResult>(categories.Count);
        var random = DemoData.CreateRandom();
        var now = DateTime.Now;

        for (int i = 0; i < categories.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = categories[i];
            progress?.Report(new ProgressUpdate(category.Name, 100.0 * i / Math.Max(1, categories.Count)));
            await Task.Delay(random.Next(40, 120), cancellationToken).ConfigureAwait(false);
            results.Add(BuildCategory(category, random, now));
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        var sorted = CleanupRules.SortBySize(results);
        return new CleanupScanResult(sorted, sorted.Sum(r => r.TotalBytes), DateTimeOffset.Now);
    }

    private static CategoryScanResult BuildCategory(CleanupCategory category, Random random, DateTime now)
    {
        if (!Samples.TryGetValue(category.Id, out var sample))
        {
            if (category.Kind == CleanupKind.Command)
            {
                var estimate = DemoCleanupCommandService.Sample(category);
                return new CategoryScanResult(category, 0, estimate.Bytes, [], estimate.Notes.Select(CleanupRules.Note).ToList()) { SizeKnown = estimate.SizeKnown };
            }

            return new CategoryScanResult(category, 0, 0, [], []);
        }

        var notes = sample.Notes.Select(CleanupRules.Note).ToList();
        var blocked = sample.Notes.FirstOrDefault(n => n.StartsWith("Skipped:", StringComparison.Ordinal));
        if (blocked is not null)
        {
            return new CategoryScanResult(category, 0, 0, [], notes) { BlockedReason = blocked };
        }
        if (sample.Names.Length == 0 || sample.Files == 0)
        {
            return new CategoryScanResult(category, sample.Files, sample.Bytes, [], notes);
        }

        // A handful of named files that add up to the category total, biggest first (like the real scanner).
        int listed = Math.Min(sample.Files, 50);
        var files = new List<CleanupFile>(listed);
        long remaining = sample.Bytes;
        for (int n = 0; n < listed; n++)
        {
            var name = string.Format(System.Globalization.CultureInfo.InvariantCulture, sample.Names[n % sample.Names.Length], 1000 + n * 37 + random.Next(0, 9));
            var path = CombineDemoPath(sample.Folder, name);
            long size = n == listed - 1 ? remaining : (long)(remaining * (0.08 + random.NextDouble() * 0.12));
            size = Math.Max(1024, Math.Min(size, remaining));
            remaining -= size;
            files.Add(new CleanupFile(path, size, now.AddHours(-2 - n * 9 - random.Next(0, 5))));
        }

        files.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        return new CategoryScanResult(category, sample.Files, sample.Bytes, files, notes);
    }

    /// <summary>Joins Windows-style paths and collapses "..": works the same on every OS, so the demo looks right anywhere.</summary>
    private static string CombineDemoPath(string folder, string relative)
    {
        var parts = new List<string>(folder.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (parts.Count > 1)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else
            {
                parts.Add(segment);
            }
        }

        return string.Join('\\', parts);
    }
}
