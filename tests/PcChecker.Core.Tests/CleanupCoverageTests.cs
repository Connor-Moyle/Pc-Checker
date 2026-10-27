using PcChecker.Core.Cleanup;
using PcChecker.Core.Optimization;
using PcChecker.Core.Scheduling;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>The wider cleanup catalog: GPU vendor caches (AMD, NVIDIA, Intel), browsers, apps, launchers and Windows tools.</summary>
public class CleanupCatalogCoverageTests
{
    internal static string? Env(string name) => name.ToUpperInvariant() switch
    {
        "TEMP" => @"C:\Users\me\AppData\Local\Temp",
        "WINDIR" => @"C:\Windows",
        "LOCALAPPDATA" => @"C:\Users\me\AppData\Local",
        "APPDATA" => @"C:\Users\me\AppData\Roaming",
        "USERPROFILE" => @"C:\Users\me",
        "PROGRAMDATA" => @"C:\ProgramData",
        "SYSTEMDRIVE" => "C:",
        "PROGRAMFILES(X86)" => @"C:\Program Files (x86)",
        "STEAM" => @"C:\Program Files (x86)\Steam",
        "UBISOFT" => @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher",
        _ => null,
    };

    private static readonly Dictionary<string, string[]> Tree = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\Users\me\AppData\Local\AMD"] = ["DxCache", "DxcCache", "Dx9Cache", "GLCache", "VkCache", "CN", "RadeonSoftware"],
        [@"C:\Users\me\AppData\Local\Packages"] = ["Microsoft.GamingApp_8wekyb3d8bbwe", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0"],
        [@"C:\Users\me\AppData\Local\Packages\Microsoft.GamingApp_8wekyb3d8bbwe\AC"] = ["AMD", "NVIDIA", "Temp", "INetCache"],
        [@"C:\Users\me\AppData\Local\Opera Software"] = ["Opera Stable", "Opera GX Stable"],
        [@"C:\Users\me\AppData\Roaming\Opera Software"] = ["Opera Stable", "Opera GX Stable"],
        [@"C:\Users\me\AppData\Local\Opera Software\Opera Stable"] = ["Cache", "System Cache"],
        [@"C:\Users\me\AppData\Local\Opera Software\Opera GX Stable"] = ["Cache"],
        ["D:\\"] = ["found.000", "found.001", "Games"],
        ["E:\\"] = ["found.000"],
    };

    internal static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PcChecker.sln")))
        {
            root = root.Parent;
        }

        return root?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    internal static IEnumerable<string> List(string parent) => Tree.TryGetValue(parent, out var names) ? names : [];

    private static ResolvedRoots Resolve(string id, bool elevated = true, Func<string, IReadOnlyList<string>>? multi = null) =>
        CleanupPathResolver.Resolve(CleanupCatalog.Find(id)!, Env, [], List, multi, elevated);

    [Fact]
    public void Amd_shader_cache_takes_every_cache_folder_but_never_the_settings_folder()
    {
        var roots = Resolve("amd.shader.cache").Roots;
        Assert.Contains(@"C:\Users\me\AppData\Local\AMD\Dx9Cache", roots);
        Assert.Contains(@"C:\Users\me\AppData\Local\AMD\DxcCache", roots);
        Assert.Contains(@"C:\Users\me\AppData\Local\AMD\VkCache", roots);
        Assert.Contains(@"C:\Users\me\AppData\Local\AMD\RadeonSoftware\vkcache", roots);
        Assert.Contains(@"C:\Users\me\AppData\Local\Packages\Microsoft.GamingApp_8wekyb3d8bbwe\AC\AMD\DxCache", roots);
        Assert.DoesNotContain(roots, r => r.Contains(@"\AMD\CN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Reset Shader Cache", CleanupCatalog.Find("amd.shader.cache")!.Hint);
    }

    [Fact]
    public void Nvidia_shader_cache_covers_current_driver_locations_and_store_games()
    {
        var roots = Resolve("nvidia.shader.cache", elevated: false).Roots;
        Assert.Contains(@"C:\Users\me\AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache", roots);
        Assert.Contains(@"C:\Users\me\AppData\Roaming\NVIDIA\ComputeCache", roots);
        Assert.Contains(@"C:\Users\me\AppData\Local\NVIDIA Corporation\NV_Cache", roots);
        Assert.Contains(@"C:\Users\me\AppData\Local\Packages\Microsoft.GamingApp_8wekyb3d8bbwe\AC\NVIDIA\DXCache", roots);
        Assert.DoesNotContain(roots, r => r.Contains("systemprofile", StringComparison.OrdinalIgnoreCase));
        Assert.True(CleanupPathResolver.IsPerUserTemplate(@"%USERPROFILE%\AppData\LocalLow\NVIDIA\DXCache"));
    }

    [Fact]
    public void Intel_shader_cache_includes_locallow_and_the_service_profile_when_elevated()
    {
        var plain = Resolve("intel.shader.cache", elevated: false).Roots;
        Assert.Contains(@"C:\Users\me\AppData\LocalLow\Intel\ShaderCache", plain);
        Assert.Contains(@"C:\ProgramData\Intel\ShaderCache", plain);
        Assert.DoesNotContain(plain, r => r.Contains("ServiceProfiles", StringComparison.Ordinal));
        Assert.Contains(@"C:\Windows\ServiceProfiles\LocalService\AppData\LocalLow\Intel\ShaderCache", Resolve("intel.shader.cache").Roots);
    }

    [Fact]
    public void Crash_dumps_include_gpu_timeout_reports_and_the_configured_dump_folders()
    {
        var category = CleanupCatalog.Find("crash.dumps")!;
        var live = category.Paths.Single(p => p.Template.EndsWith(@"\LiveKernelReports", StringComparison.Ordinal));
        Assert.True(live.Recurse);
        Assert.Equal(["*.dmp"], live.Patterns);
        Assert.Contains(category.Paths, p => p.Template == "%DUMPFILEDIR%" && p.Recurse == false);
        Assert.Contains(category.Paths, p => p.Template == "%MINIDUMPDIR%");
        Assert.Equal(RiskLevel.Moderate, category.Risk);
        Assert.Contains("driver timeouts", category.Hint);
    }

    [Fact]
    public void Chromium_caches_take_service_worker_cache_storage_but_never_the_whole_service_worker_folder()
    {
        foreach (var id in new[] { "browser.chrome.cache", "browser.edge.cache", "browser.brave.cache", "browser.vivaldi.cache" })
        {
            var templates = CleanupCatalog.Find(id)!.PathTemplates;
            Assert.Contains(templates, t => t.EndsWith(@"\*\Service Worker\CacheStorage", StringComparison.Ordinal));
            Assert.Contains(templates, t => t.EndsWith(@"\User Data\GrShaderCache", StringComparison.Ordinal));
            Assert.DoesNotContain(templates, t => t.EndsWith(@"\Service Worker", StringComparison.Ordinal));
        }

        Assert.Contains(@"%LOCALAPPDATA%\Microsoft\Edge Beta\User Data\*\Cache\Cache_Data", CleanupCatalog.Find("browser.edge.cache")!.PathTemplates);
        Assert.Contains(@"%LOCALAPPDATA%\Google\Chrome SxS\User Data\*\Cache\Cache_Data", CleanupCatalog.Find("browser.chrome.cache")!.PathTemplates);
    }

    [Fact]
    public void Opera_and_opera_gx_are_separate_cards()
    {
        var opera = Resolve("browser.opera.cache").Roots;
        var gx = Resolve("browser.operagx.cache").Roots;
        Assert.Contains(@"C:\Users\me\AppData\Local\Opera Software\Opera Stable\Cache", opera);
        Assert.Contains(@"C:\Users\me\AppData\Local\Opera Software\Opera Stable\System Cache", opera);
        Assert.DoesNotContain(opera, r => r.Contains("Opera GX", StringComparison.Ordinal));
        Assert.Contains(@"C:\Users\me\AppData\Local\Opera Software\Opera GX Stable\Cache", gx);
        Assert.All(gx, r => Assert.Contains("Opera GX", r));
    }

    [Fact]
    public void Old_chkdsk_fragments_are_found_on_every_fixed_drive_but_a_drive_root_never_becomes_a_root()
    {
        var resolved = Resolve("old.chkdsk", multi: v => v == "FIXEDDRIVES" ? ["D:\\", "E:\\"] : []);
        Assert.Equal([@"D:\found.000", @"D:\found.001", @"E:\found.000"], resolved.Roots);
        Assert.Empty(Resolve("old.chkdsk", multi: _ => []).Roots);
    }

    [Fact]
    public void Steam_categories_follow_every_library()
    {
        var resolved = Resolve("game.steam.shadercache", multi: v => v == "STEAMLIBRARIES" ? [@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary\"] : []);
        Assert.Equal([@"C:\Program Files (x86)\Steam\steamapps\shadercache", @"D:\SteamLibrary\steamapps\shadercache"], resolved.Roots);
        Assert.Equal(RiskLevel.Moderate, CleanupCatalog.Find("game.steam.shadercache")!.Risk);
        Assert.Equal(RiskLevel.Advanced, CleanupCatalog.Find("game.steam.downloading")!.Risk);
    }

    [Fact]
    public void Spotify_downloads_and_mail_are_excluded()
    {
        var spotify = Resolve("app.spotify.cache");
        Assert.Contains(@"C:\Users\me\AppData\Local\Spotify\Storage", spotify.ExcludedPaths);
        Assert.True(CleanupSafety.IsExcluded(@"C:\Users\me\AppData\Local\Spotify\Storage\a1\song.file", spotify.ExcludedPaths, []));
        Assert.False(CleanupSafety.IsExcluded(@"C:\Users\me\AppData\Local\Spotify\Data\a1\chunk.file", spotify.ExcludedPaths, []));
        Assert.Contains(CleanupCatalog.Find("app.thunderbird.cache")!.ExcludePathTemplates, t => t.EndsWith(@"\ImapMail", StringComparison.Ordinal));
    }

    [Fact]
    public void Browser_cards_never_take_passwords_bookmarks_or_firefox_places()
    {
        foreach (var category in CleanupCatalog.All.Where(c => c.Id.StartsWith("browser.", StringComparison.Ordinal) && c.Kind == CleanupKind.Files))
        {
            foreach (var protectedName in new[] { "Login Data", "Web Data", "places.sqlite", "favicons.sqlite", "key4.db", "logins.json" })
            {
                Assert.True(CleanupSafety.IsExcluded(@"C:\Users\me\AppData\Local\X\" + protectedName, [], category.ExcludePatterns), category.Id + " " + protectedName);
            }
        }
    }

    [Fact]
    public void Privacy_items_are_opt_in_and_never_scheduled()
    {
        var privacy = CleanupCatalog.All.Where(c => c.Group == CleanupGroup.Privacy).ToList();
        Assert.Contains(privacy, c => c.Id == "browser.cookies");
        Assert.All(privacy, c =>
        {
            Assert.False(c.EnabledByDefault);
            Assert.True(c.NeverScheduled);
            Assert.False(CleanupRules.IsSchedulable(c));
        });
        var cookies = CleanupCatalog.Find("browser.cookies")!;
        Assert.Equal(RiskLevel.Advanced, cookies.Risk);
        Assert.Contains("signed out of all websites", cookies.ConfirmWarning);
    }

    [Fact]
    public void Disk_cleanup_categories_need_elevation_and_never_touch_driver_or_language_packages()
    {
        var handlers = CleanupCatalog.All.Where(c => c.Command == CleanupCommand.DiskCleanupHandlers).ToList();
        Assert.True(handlers.Count >= 10);
        Assert.All(handlers, c =>
        {
            Assert.True(c.Requires.HasFlag(CleanupRequirement.Elevated));
            Assert.NotEmpty(c.DiskCleanupHandlers);
            Assert.DoesNotContain("Device Driver Packages", c.DiskCleanupHandlers);
            Assert.DoesNotContain("Language Pack", c.DiskCleanupHandlers);
        });
        Assert.Contains(handlers, c => c.Id == "windows.upgrade.leftovers" && c.DiskCleanupHandlers.Contains("Windows ESD installation files"));
    }

    [Fact]
    public void Broad_shared_folders_are_only_scanned_flat_with_exact_patterns()
    {
        // A folder such as %LOCALAPPDATA% or %WINDIR%\Installer may only give up named files, never everything below it.
        string[] broad = ["%LOCALAPPDATA%", "%APPDATA%", "%USERPROFILE%", "%WINDIR%", "%PROGRAMDATA%", "%STEAM%", "%UBISOFT%", @"%WINDIR%\Installer",
            @"%WINDIR%\INF", @"%WINDIR%\SoftwareDistribution", @"%PROGRAMDATA%\NVIDIA", @"%WINDIR%\ServiceProfiles\LocalService\AppData\Local"];
        foreach (var category in CleanupCatalog.All)
        {
            foreach (var path in category.Paths.Where(p => broad.Contains(p.Template, StringComparer.OrdinalIgnoreCase)))
            {
                Assert.False(path.Recurse, category.Id + " " + path.Template);
                Assert.DoesNotContain("*", path.Patterns!);
            }
        }
    }

    [Fact]
    public void Every_app_rule_points_at_a_category_and_every_cache_of_an_app_has_one()
    {
        Assert.All(CleanupRules.AppProcesses.Keys, id => Assert.NotNull(CleanupCatalog.Find(id)));
        foreach (var category in CleanupCatalog.All.Where(c => c.Group is CleanupGroup.Browsers or CleanupGroup.Games || c.Id.StartsWith("app.", StringComparison.Ordinal)))
        {
            Assert.True(CleanupRules.AppProcesses.ContainsKey(category.Id), category.Id);
        }
    }

    [Fact]
    public void Prune_anywhere_covers_scratch_caches_but_not_windows_logs()
    {
        var prune = CleanupRules.PruneAnywhereCategories;
        Assert.Contains("windows.temp", prune);
        Assert.Contains("amd.shader.cache", prune);
        Assert.Contains("browser.vivaldi.cache", prune);
        Assert.DoesNotContain("windows.logs", prune);
        Assert.DoesNotContain("prefetch", prune);
    }

    [Fact]
    public void Font_cache_pauses_the_font_cache_service_and_is_moderate()
    {
        Assert.Contains("FontCache", CleanupRules.ServicesToStop(["font.cache"]));
        Assert.Equal(RiskLevel.Moderate, CleanupCatalog.Find("font.cache")!.Risk);
        Assert.Contains(CleanupCatalog.Find("font.cache")!.Paths, p => p.Patterns!.Contains("GDIPFONTCACHEV1.DAT"));
    }

    [Fact]
    public void Scheduled_cleanup_never_takes_privacy_command_or_developer_categories()
    {
        var everything = CleanupCatalog.All.Select(c => c.Id).ToList();
        var unattended = ScheduleRules.UnattendedCleanupCategories(CleanupCatalog.All, everything);
        Assert.Contains("user.temp", unattended);
        Assert.Contains("amd.shader.cache", unattended);
        Assert.DoesNotContain("privacy.clipboard", unattended);
        Assert.DoesNotContain("privacy.recent.items", unattended);
        Assert.DoesNotContain("windows.component.store", unattended);
        Assert.DoesNotContain("restore.points", unattended);
        Assert.DoesNotContain("dev.nuget.packages", unattended);
        Assert.All(unattended, id => Assert.Equal(RiskLevel.Safe, CleanupCatalog.Find(id)!.Risk));
    }

    [Fact]
    public void Docs_list_every_protected_file_name_and_the_gpu_vendors()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PcChecker.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var safety = File.ReadAllText(Path.Combine(root!.FullName, "docs", "SAFETY.md"));
        Assert.All(CleanupSafety.NeverDeleteNames, n => Assert.Contains(n, safety, StringComparison.OrdinalIgnoreCase));
        var features = File.ReadAllText(Path.Combine(root.FullName, "docs", "FEATURES.md"));
        foreach (var word in new[] { "AMD", "NVIDIA", "Intel", "Brave", "Opera GX", "Discord", "DISM" })
        {
            Assert.Contains(word, features);
        }
    }
}

public class CleanupAppGuardTests
{
    [Fact]
    public void Chrome_and_chromium_share_chrome_exe_and_are_told_apart_by_path()
    {
        var chromium = new RunningProcess("chrome", @"C:\Users\me\AppData\Local\Chromium\Application\chrome.exe");
        Assert.Null(CleanupRules.AppRunningReason("browser.chrome.cache", [chromium]));
        Assert.NotNull(CleanupRules.AppRunningReason("browser.chromium.cache", [chromium]));

        var chrome = new RunningProcess("chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        Assert.NotNull(CleanupRules.AppRunningReason("browser.chrome.cache", [chrome]));
        Assert.Null(CleanupRules.AppRunningReason("browser.chromium.cache", [chrome]));

        // A path that could not be read counts as running (the safe side).
        Assert.NotNull(CleanupRules.AppRunningReason("browser.chrome.cache", [new RunningProcess("chrome")]));
    }

    [Fact]
    public void Opera_gx_and_yandex_are_recognised_by_their_install_folder()
    {
        var gx = new RunningProcess("opera", @"C:\Users\me\AppData\Local\Programs\Opera GX\opera.exe");
        Assert.NotNull(CleanupRules.AppRunningReason("browser.operagx.cache", [gx]));
        Assert.Null(CleanupRules.AppRunningReason("browser.opera.cache", [gx]));

        var otherBrowserExe = new RunningProcess("browser", @"C:\Tools\SomethingElse\browser.exe");
        Assert.Null(CleanupRules.AppRunningReason("browser.yandex.cache", [otherBrowserExe]));
        Assert.NotNull(CleanupRules.AppRunningReason("browser.yandex.cache", [new RunningProcess("browser", @"C:\Users\me\AppData\Local\Yandex\YandexBrowser\Application\browser.exe")]));
    }

    [Fact]
    public void Tray_apps_get_a_hint_and_privacy_items_wait_for_every_browser()
    {
        var reason = CleanupRules.AppRunningReason("app.discord.cache", [new RunningProcess("DiscordPTB")]);
        Assert.NotNull(reason);
        Assert.Contains("system tray", reason);
        Assert.NotNull(CleanupRules.AppRunningReason("browser.cookies", [new RunningProcess("vivaldi")]));
        Assert.NotNull(CleanupRules.AppRunningReason("game.battlenet.cache", [new RunningProcess("Agent", @"C:\ProgramData\Battle.net\Agent\Agent.9001\Agent.exe")]));
        Assert.Null(CleanupRules.AppRunningReason("game.battlenet.cache", [new RunningProcess("Agent", @"C:\Program Files\Other\Agent.exe")]));
        Assert.Null(CleanupRules.AppRunningReason("user.temp", [new RunningProcess("Discord")]));
    }

    [Fact]
    public void Amd_installer_blocks_the_amd_leftovers_only_while_it_runs_from_c_amd()
    {
        Assert.NotNull(CleanupRules.AppRunningReason("driver.leftovers.amd", [new RunningProcess("Setup", @"C:\AMD\AMD-Software-Installer\Setup.exe")]));
        Assert.NotNull(CleanupRules.AppRunningReason("driver.leftovers.amd", [new RunningProcess("AMD_Chipset_Software")]));
        Assert.Null(CleanupRules.AppRunningReason("driver.leftovers.amd", [new RunningProcess("setup", @"C:\Downloads\game\setup.exe")]));
    }

    [Theory]
    [InlineData("chrome.exe", true)]
    [InlineData("AMD_Chipset_Software", true)]
    [InlineData("NVIDIA app", true)]
    [InlineData("notepad", false)]
    [InlineData("", false)]
    public void Watched_process_names_support_wildcards(string name, bool expected)
    {
        Assert.Equal(expected, CleanupRules.IsWatchedProcess(name));
    }
}

public class CleanupConditionTests
{
    private static CleanupCategory WithRequirement(CleanupRequirement requirement) =>
        new("x", "X", "x", RiskLevel.Moderate, [@"%WINDIR%\Temp"], ["*"], false, true) { Requires = requirement };

    [Fact]
    public void Each_requirement_gives_its_own_plain_reason()
    {
        var clear = CleanupConditions.Clear;
        Assert.Null(CleanupRules.BlockedReason(WithRequirement(CleanupRequirement.Elevated | CleanupRequirement.NoPendingReboot), clear));
        Assert.Contains("administrator", CleanupRules.BlockedReason(WithRequirement(CleanupRequirement.Elevated), clear with { IsElevated = false }));
        Assert.Contains("restart", CleanupRules.BlockedReason(WithRequirement(CleanupRequirement.NoPendingReboot), clear with { PendingReboot = true }));
        Assert.Contains("Installer", CleanupRules.BlockedReason(WithRequirement(CleanupRequirement.NoInstallerRunning), clear with { InstallerRunning = true }));
        Assert.Contains("next restart", CleanupRules.BlockedReason(WithRequirement(CleanupRequirement.NoPendingFileRenames), clear with { PendingFileRenames = true }));
        Assert.Contains("Windows Update", CleanupRules.BlockedReason(WithRequirement(CleanupRequirement.NoUpdateInstalling), clear with { UpdateInstalling = true }));
        Assert.Null(CleanupRules.BlockedReason(WithRequirement(CleanupRequirement.None), new CleanupConditions(false, true, true, true, true)));
    }

    [Fact]
    public void Recent_upgrades_get_a_rollback_warning_for_ten_days()
    {
        var now = new DateTime(2026, 10, 27, 12, 0, 0);
        Assert.Contains("6 days ago", CleanupRules.RecentUpgradeWarning(now.AddDays(-6), now));
        Assert.Contains("today", CleanupRules.RecentUpgradeWarning(now.AddHours(-3), now));
        Assert.Null(CleanupRules.RecentUpgradeWarning(now.AddDays(-11), now));
        Assert.Null(CleanupRules.RecentUpgradeWarning(null, now));
        Assert.Null(CleanupRules.RecentUpgradeWarning(now.AddDays(1), now));
    }

    [Fact]
    public void Prefetch_keeps_entries_used_in_the_last_thirty_days()
    {
        var prefetch = CleanupCatalog.Find("prefetch")!;
        Assert.Equal(30 * 24 * 60, CleanupRules.EffectiveSkipMinutes(prefetch, 60));
        Assert.Equal(60, CleanupRules.EffectiveSkipMinutes(CleanupCatalog.Find("user.temp")!, 60));
        var now = new DateTime(2026, 10, 27);
        int minutes = CleanupRules.EffectiveSkipMinutes(prefetch, 60);
        Assert.True(CleanupSafety.ShouldSkip(new CleanupFile(@"C:\Windows\Prefetch\GAME.EXE-1234.pf", 10, now.AddDays(-5)), now, minutes));
        Assert.False(CleanupSafety.ShouldSkip(new CleanupFile(@"C:\Windows\Prefetch\OLD.EXE-1234.pf", 10, now.AddDays(-45)), now, minutes));
        Assert.True(CleanupSafety.IsExcluded(@"C:\Windows\Prefetch\Layout.ini", [], prefetch.ExcludePatterns));
    }

    [Fact]
    public void A_file_must_match_its_folder_pattern_and_depth_at_delete_time()
    {
        ResolvedRoot[] entries =
        [
            new(@"C:\Users\me\AppData\Local", ["IconCache.db"], false),
            new(@"C:\Users\me\AppData\Local\Microsoft\Windows\Explorer", ["iconcache_*.db"], false),
        ];
        Assert.True(CleanupRules.MatchesEntry(@"C:\Users\me\AppData\Local\IconCache.db", entries));
        Assert.True(CleanupRules.MatchesEntry(@"C:\Users\me\AppData\Local\Microsoft\Windows\Explorer\iconcache_32.db", entries));
        Assert.False(CleanupRules.MatchesEntry(@"C:\Users\me\AppData\Local\Other\IconCache.db", entries));
        Assert.False(CleanupRules.MatchesEntry(@"C:\Users\me\AppData\Local\important.db", entries));
        Assert.False(CleanupRules.MatchesEntry(@"C:\Users\me\AppData\Local\Microsoft\Windows\Explorer\thumbcache_32.db", entries));
        Assert.True(CleanupRules.MatchesEntry(@"C:\Cache\a\b\c.bin", [new ResolvedRoot(@"C:\Cache", ["*"], true)]));
    }

    [Fact]
    public void Explorer_held_cache_files_suggest_restarting_explorer()
    {
        Assert.Equal(2, CleanupRules.ExplorerHeldFiles(
        [
            @"C:\Users\me\AppData\Local\Microsoft\Windows\Explorer\thumbcache_256.db (in use)",
            @"C:\Users\me\AppData\Local\Microsoft\Windows\Explorer\iconcache_32.db (in use)",
            @"C:\Users\me\AppData\Local\Temp\x.tmp (in use)",
            @"C:\Users\me\AppData\Local\Microsoft\Windows\Explorer\thumbcache_16.db (recently modified or protected name)",
        ]));
        Assert.Equal(0, CleanupRules.ExplorerHeldFiles([]));
    }
}

public class CleanupRootRuleTests
{
    [Fact]
    public void Steam_libraries_are_read_from_both_vdf_formats()
    {
        const string Current = """
            "libraryfolders"
            {
                "0"
                {
                    "path"      "C:\\Program Files (x86)\\Steam"
                    "label"     ""
                }
                "1"
                {
                    "path"      "D:\\SteamLibrary"
                }
                "2"
                {
                    "path"      "\\\\nas\\games"
                }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], CleanupRootRules.ParseSteamLibraries(Current));

        const string Legacy = """
            "LibraryFolders"
            {
                "TimeNextStatsReport"   "1700000000"
                "1"     "E:\\Games\\Steam"
            }
            """;
        Assert.Equal([@"E:\Games\Steam"], CleanupRootRules.ParseSteamLibraries(Legacy));
        Assert.Empty(CleanupRootRules.ParseSteamLibraries(null));
    }

    [Fact]
    public void Steam_path_from_the_registry_is_normalised()
    {
        Assert.Equal(@"C:\program files (x86)\steam", CleanupRootRules.NormalizeSteamPath("c:/program files (x86)/steam"));
        Assert.Null(CleanupRootRules.NormalizeSteamPath(@"\\server\steam"));
        Assert.Null(CleanupRootRules.NormalizeSteamPath(""));
    }

    [Fact]
    public void Queued_and_paused_steam_downloads_are_kept()
    {
        var active = CleanupRootRules.ActiveSteamDownloads(@"D:\SteamLibrary\steamapps\downloading",
            ["1172470", "730", "temp-folder"],
            ["appmanifest_1172470.acf", "appmanifest_570.acf", "libraryfolders.vdf"]);
        Assert.Contains(@"D:\SteamLibrary\steamapps\downloading\1172470", active);
        Assert.Contains(@"D:\SteamLibrary\steamapps\downloading\temp-folder", active);
        Assert.DoesNotContain(@"D:\SteamLibrary\steamapps\downloading\730", active);
    }

    [Fact]
    public void Dump_settings_give_a_local_folder()
    {
        static string Expand(string s) => s.Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"C:\Windows", CleanupRootRules.DumpFolder(@"%SystemRoot%\MEMORY.DMP", true, Expand));
        Assert.Equal(@"D:\Dumps\Mini", CleanupRootRules.DumpFolder(@"D:\Dumps\Mini\", false, Expand));
        Assert.Null(CleanupRootRules.DumpFolder(@"D:\MEMORY.DMP", true, Expand));
        Assert.Null(CleanupRootRules.DumpFolder(@"\\server\dumps", false, Expand));
        Assert.Null(CleanupRootRules.DumpFolder(null, false, Expand));
    }

    [Fact]
    public void Folders_an_uninstaller_needs_under_c_amd_are_kept()
    {
        var kept = CleanupRootRules.FoldersReferencedUnder(@"C:\AMD",
        [
            "\"C:\\AMD\\Chipset_Software\\AMD_Chipset_Software.exe\" /uninstall",
            @"C:\AMD\Ryzen_Master_Installer",
            "MsiExec.exe /X{12345678-1234-1234-1234-123456789012}",
            @"D:\Other\C:\AMD\Fake\x.exe",
            @"C:\AMDX\Something\setup.exe",
            null,
        ]);
        Assert.Equal([@"C:\AMD\Chipset_Software", @"C:\AMD\Ryzen_Master_Installer"], kept);

        // An uninstaller directly in C:\AMD keeps the whole folder.
        Assert.Equal([@"C:\AMD"], CleanupRootRules.FoldersReferencedUnder(@"C:\AMD", [@"C:\AMD\setup.exe -uninstall"]));
    }

    [Fact]
    public void Newest_chipset_folder_is_the_fallback_when_uninstall_entries_cannot_be_read()
    {
        var newest = CleanupRootRules.NewestChipsetFolder(
        [
            (@"C:\AMD\Chipset_Software_6.10", new DateTime(2025, 1, 1)),
            (@"C:\AMD\Chipset_Software_7.01", new DateTime(2026, 3, 1)),
            (@"C:\AMD\AMD-Software-Installer", new DateTime(2026, 9, 1)),
        ]);
        Assert.Equal(@"C:\AMD\Chipset_Software_7.01", newest);
        Assert.Null(CleanupRootRules.NewestChipsetFolder([]));
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp", true)]
    [InlineData(@"D:\found.000", true)]
    [InlineData(@"C:\", false)]
    [InlineData("C:", false)]
    [InlineData(@"\\server\share\cache", false)]
    [InlineData(@"relative\path", false)]
    [InlineData(@"\\?\C:\Windows\Temp", false)]
    public void Only_local_absolute_non_drive_folders_are_roots(string path, bool expected)
    {
        Assert.Equal(expected, CleanupPathResolver.IsAcceptableRoot(path));
    }

    [Fact]
    public void Multi_value_variables_give_one_template_per_value()
    {
        Assert.Equal([@"C:\Steam\steamapps\temp", @"D:\Lib\steamapps\temp"],
            CleanupPathResolver.ExpandMultiValue(@"%STEAMLIBRARIES%\steamapps\temp", _ => [@"C:\Steam\", @"D:\Lib"]));
        Assert.Empty(CleanupPathResolver.ExpandMultiValue(@"%STEAMLIBRARIES%\steamapps\temp", _ => []));
        Assert.Equal([@"%WINDIR%\Temp"], CleanupPathResolver.ExpandMultiValue(@"%WINDIR%\Temp", null));
    }
}

public class CleanupCommandRuleTests
{
    private const string EnglishAnalysis = """
        Deployment Image Servicing and Management tool
        Version: 10.0.26100.1150

        Image Version: 10.0.26100.2033

        [===========================100.0%==========================]

        Component Store (WinSxS) information:

        Windows Explorer Reported Size of Component Store : 9.21 GB

        Actual Size of Component Store : 8.84 GB

            Shared with Windows : 5.92 GB
            Backups and Disabled Features : 2.67 GB
            Cache and Temporary Data :  254.30 MB

        Date of Last Cleanup : 2026-09-14 03:12:44

        Number of Reclaimable Packages : 4
        Component Store Cleanup Recommended : Yes

        The operation completed successfully.
        """;

    private const string GermanAnalysis = """
        Tatsächliche Größe des Komponentenspeichers : 7,98 GB
        Von Windows gemeldete Größe des Komponentenspeichers : 8,27 GB
            Mit Windows freigegeben : 5,64 GB
            Sicherungen und deaktivierte Features : 2,14 GB
            Cache und temporäre Daten : 0 Bytes
        Datum der letzten Bereinigung : 2026-01-02 10:12:34
        Anzahl der freigebbaren Pakete : 0
        Bereinigung des Komponentenspeichers empfohlen : Nein
        """;

    [Fact]
    public void Dism_analysis_is_read_by_position_in_any_language()
    {
        var english = CleanupCommandRules.ParseAnalyzeComponentStore(EnglishAnalysis)!;
        Assert.Equal(4, english.ReclaimablePackages);
        Assert.True(english.CleanupRecommended);
        Assert.Equal(CleanupCommandRules.ParseSize("2.67 GB")!.Value + CleanupCommandRules.ParseSize("254.30 MB")!.Value, english.EstimatedReclaimableBytes);

        var german = CleanupCommandRules.ParseAnalyzeComponentStore(GermanAnalysis)!;
        Assert.Equal(0, german.ReclaimablePackages);
        Assert.False(german.CleanupRecommended);
        Assert.Equal(0, german.EstimatedReclaimableBytes);

        Assert.Null(CleanupCommandRules.ParseAnalyzeComponentStore("Error: 740\nElevated permissions are required to run DISM."));
        Assert.Null(CleanupCommandRules.ParseAnalyzeComponentStore(null));
    }

    [Theory]
    [InlineData("8.27 GB", 8_879_844_884L)]
    [InlineData("8,27 GB", 8_879_844_884L)]
    [InlineData("512 MB", 536_870_912L)]
    [InlineData("1,5 Go", 1_610_612_736L)]
    [InlineData("1,234.50 MB", 1_294_467_072L)]
    [InlineData("0 Bytes", 0L)]
    [InlineData("12 КБ", 12_288L)]
    public void Sizes_parse_with_either_decimal_separator_and_localised_units(string text, long expected)
    {
        Assert.Equal(expected, CleanupCommandRules.ParseSize(text));
    }

    [Fact]
    public void Unreadable_sizes_are_null()
    {
        Assert.Null(CleanupCommandRules.ParseSize("n/a"));
        Assert.Null(CleanupCommandRules.ParseSize(""));
    }

    [Fact]
    public void Disk_cleanup_flags_only_handlers_this_build_has()
    {
        Assert.Equal("StateFlags7316", CleanupCommandRules.StateFlagsValueName(CleanupCommandRules.SageRunNumber));
        Assert.Equal("StateFlags0042", CleanupCommandRules.StateFlagsValueName(42));
        Assert.Throws<ArgumentOutOfRangeException>(() => CleanupCommandRules.StateFlagsValueName(10000));

        var run = CleanupCommandRules.HandlersToRun(
            ["temporary setup files", "Windows ESD installation files", "Device Driver Packages", "Not On This Build"],
            ["Temporary Setup Files", "Device Driver Packages", "Windows ESD installation files", "BranchCache"]);
        Assert.Equal(["Temporary Setup Files", "Windows ESD installation files"], run);
    }

    [Fact]
    public void Restore_point_trimming_always_keeps_the_newest()
    {
        Assert.Equal(0, CleanupCommandRules.RestorePointDeletionsNeeded(0));
        Assert.Equal(0, CleanupCommandRules.RestorePointDeletionsNeeded(1));
        Assert.Equal(3, CleanupCommandRules.RestorePointDeletionsNeeded(4));
        Assert.Equal(7_500, CleanupCommandRules.RestorePointEstimate(10_000, 4));
        Assert.Equal(0, CleanupCommandRules.RestorePointEstimate(10_000, 1));

        Assert.Empty(CleanupCommandRules.RestorePointsToRemove([]));
        Assert.Empty(CleanupCommandRules.RestorePointsToRemove([42]));
        // Oldest first, the newest (highest sequence number) always stays, duplicates are ignored.
        Assert.Equal([12u, 15u, 40u], CleanupCommandRules.RestorePointsToRemove([40, 12, 57, 15, 12]));
    }

    [Fact]
    public void Restore_points_are_removed_through_system_restore_not_vssadmin()
    {
        var source = File.ReadAllText(Path.Combine(CleanupCatalogCoverageTests.RepoRoot(), "src", "PcChecker.Core", "Cleanup", "WindowsCleanupCommandService.cs"));
        Assert.DoesNotContain("vssadmin.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Win32_ShadowCopy", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FROM SystemRestore", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_logs_never_touch_the_windows_update_database()
    {
        var logs = CleanupCatalog.Find("windows.logs")!;
        Assert.DoesNotContain(logs.Paths, p => p.Template.Contains("DataStore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(CleanupCatalog.All.SelectMany(c => c.Paths), p => p.Template.Contains(@"SoftwareDistribution\DataStore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Privacy_and_office_upload_cache_never_reach_other_accounts()
    {
        Assert.All(CleanupCatalog.All.Where(c => c.Group == CleanupGroup.Privacy), c => Assert.True(c.CurrentUserOnly, c.Id));
        Assert.True(CleanupCatalog.Find("app.office.filecache")!.CurrentUserOnly);

        string[] others = [@"C:\Users\other"];
        var recent = CleanupPathResolver.Resolve(CleanupCatalog.Find("privacy.recent.items")!, CleanupCatalogCoverageTests.Env, others, CleanupCatalogCoverageTests.List);
        Assert.Empty(recent.UsersScanned);
        Assert.DoesNotContain(recent.Roots, r => r.StartsWith(@"C:\Users\other", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(@"C:\Users\me\AppData\Roaming\Microsoft\Windows\Recent", recent.Roots, StringComparer.OrdinalIgnoreCase);

        // An ordinary cache still covers every account when elevated.
        var temp = CleanupPathResolver.Resolve(CleanupCatalog.Find("user.temp")!, CleanupCatalogCoverageTests.Env, others, CleanupCatalogCoverageTests.List);
        Assert.Contains(@"C:\Users\other\AppData\Local\Temp", temp.Roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Office_upload_cache_is_advanced_and_never_scheduled()
    {
        var office = CleanupCatalog.Find("app.office.filecache")!;
        Assert.Equal(RiskLevel.Advanced, office.Risk);
        Assert.True(office.NeverScheduled);
        Assert.False(CleanupRules.IsSchedulable(office));
        Assert.NotNull(office.ConfirmWarning);
    }

    [Fact]
    public void Disk_cleanup_items_are_never_scheduled()
    {
        var handlers = CleanupCatalog.All.Where(c => c.Command == CleanupCommand.DiskCleanupHandlers).ToList();
        Assert.NotEmpty(handlers);
        Assert.All(handlers, c => Assert.True(c.NeverScheduled, c.Id));
        var unattended = ScheduleRules.UnattendedCleanupCategories(CleanupCatalog.All, CleanupCatalog.All.Select(c => c.Id).ToList());
        Assert.DoesNotContain(unattended, id => CleanupCatalog.Find(id)!.Kind == CleanupKind.Command);
    }

    [Fact]
    public void Upgrade_leftovers_only_measure_what_disk_cleanup_removes()
    {
        var leftovers = CleanupCatalog.Find("windows.upgrade.leftovers")!;
        Assert.DoesNotContain("Temporary Windows installation files", leftovers.DiskCleanupHandlers);
        Assert.DoesNotContain(leftovers.PathTemplates, t => t.Contains("WinREAgent", StringComparison.OrdinalIgnoreCase) || t.Contains("Windows10Upgrade", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Font_cache_needs_administrator_rights()
    {
        Assert.True(CleanupCatalog.Find("font.cache")!.Requires.HasFlag(CleanupRequirement.Elevated));
        Assert.NotNull(CleanupRules.BlockedReason(CleanupCatalog.Find("font.cache")!, CleanupConditions.Clear with { IsElevated = false }));
    }

    [Fact]
    public void Command_freed_space_comes_from_the_drive_not_the_estimate()
    {
        Assert.Equal(5_000, CleanupRules.FreedByCommand(10_000, 15_000));
        Assert.Equal(0, CleanupRules.FreedByCommand(10_000, 9_000));
        Assert.Equal(0, CleanupRules.FreedByCommand(null, 15_000));
        Assert.Equal(0, CleanupRules.FreedByCommand(10_000, null));
    }

    [Fact]
    public void Amd_software_cache_waits_for_ryzen_master_too()
    {
        Assert.NotNull(CleanupRules.AppRunningReason("amd.software.cache", [new RunningProcess("AMDRyzenMaster")]));
        Assert.NotNull(CleanupRules.AppRunningReason("amd.software.cache", [new RunningProcess("RadeonSoftware")]));
        Assert.Null(CleanupRules.AppRunningReason("amd.software.cache", [new RunningProcess("notepad")]));
    }

    [Fact]
    public async Task Demo_scan_shows_command_sizes_and_a_running_app()
    {
        var scan = await new DemoTempFileScanner().ScanAsync(["windows.component.store", "restore.points", "privacy.clipboard", "app.discord.cache", "driver.leftovers.amd"], null, CancellationToken.None);
        var dism = scan.Categories.Single(c => c.Category.Id == "windows.component.store");
        Assert.True(dism.TotalBytes > 0);
        Assert.True(dism.SizeKnown);
        Assert.False(scan.Categories.Single(c => c.Category.Id == "privacy.clipboard").SizeKnown);
        Assert.NotNull(scan.Categories.Single(c => c.Category.Id == "app.discord.cache").BlockedReason);
        Assert.True(scan.Categories.Single(c => c.Category.Id == "driver.leftovers.amd").TotalBytes > 1_000_000_000);

        var run = await new DemoTempFileCleaner().CleanAsync(scan, ["windows.component.store", "app.discord.cache"], false, null, CancellationToken.None);
        Assert.Contains(run.Messages, m => m.Contains("DISM", StringComparison.Ordinal));
        Assert.Contains(run.Skipped, s => s.Contains("Discord", StringComparison.Ordinal));
    }
}
