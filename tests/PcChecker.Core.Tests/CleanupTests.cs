using PcChecker.Core.Cleanup;
using PcChecker.Core.Optimization;
using Xunit;

namespace PcChecker.Core.Tests;

public class CleanupSafetyTests
{
    private static readonly string[] Roots = [@"C:\Users\me\AppData\Local\Temp", @"C:\Windows\Temp"];

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\file.tmp", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\sub\deeper\file.tmp", true)]
    [InlineData(@"c:\windows\temp\x.log", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp", false)]
    [InlineData(@"C:\Users\me\AppData\Local\TempFiles\x", false)]
    [InlineData(@"C:\Users\me\Documents\important.docx", false)]
    [InlineData(@"D:\Users\me\AppData\Local\Temp\file.tmp", false)]
    [InlineData(@"D:\Windows\Temp\x.log", false)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\..\..\Documents\x", false)]
    [InlineData("", false)]
    public void IsPathAllowed_only_inside_roots(string path, bool expected)
    {
        Assert.Equal(expected, CleanupSafety.IsPathAllowed(path, Roots));
    }

    [Fact]
    public void IsPathAllowed_refuses_drive_roots()
    {
        Assert.False(CleanupSafety.IsPathAllowed(@"C:\anything.txt", [@"C:\"]));
        Assert.False(CleanupSafety.IsPathAllowed(@"C:\anything.txt", ["C:"]));
    }

    [Fact]
    public void ShouldSkip_recent_and_special_files()
    {
        var now = new DateTime(2025, 1, 1, 12, 0, 0);
        Assert.True(CleanupSafety.ShouldSkip(new CleanupFile(@"C:\t\fresh.tmp", 1, now.AddMinutes(-5)), now, 60));
        Assert.False(CleanupSafety.ShouldSkip(new CleanupFile(@"C:\t\old.tmp", 1, now.AddHours(-2)), now, 60));
        Assert.True(CleanupSafety.ShouldSkip(new CleanupFile(@"C:\t\desktop.ini", 1, now.AddDays(-9)), now, 60));
        Assert.True(CleanupSafety.ShouldSkip(new CleanupFile(@"C:\t\Thumbs.db", 1, now.AddDays(-9)), now, 60));
        Assert.False(CleanupSafety.ShouldSkip(new CleanupFile(@"C:\t\fresh.tmp", 1, now.AddMinutes(-5)), now, 0));
    }

    [Theory]
    [InlineData("thumbcache_256.db", "thumbcache_*.db", true)]
    [InlineData("iconcache_16.db", "thumbcache_*.db", false)]
    [InlineData("MEMORY.DMP", "*.dmp", true)]
    [InlineData("report.log", "*.log", true)]
    [InlineData("report.log", "*", true)]
    [InlineData("a.pf", "?.pf", true)]
    [InlineData("ab.pf", "?.pf", false)]
    public void MatchesPattern_supports_wildcards(string file, string pattern, bool expected)
    {
        Assert.Equal(expected, CleanupSafety.MatchesPattern(file, pattern));
    }

    [Fact]
    public void Reparse_points_are_detected()
    {
        Assert.True(CleanupSafety.IsReparsePoint(FileAttributes.Directory | FileAttributes.ReparsePoint));
        Assert.False(CleanupSafety.IsReparsePoint(FileAttributes.Directory));
    }
}

public class CleanupCatalogTests
{
    private static string? Env(string name) => name switch
    {
        "TEMP" => @"C:\Users\me\AppData\Local\Temp\",
        "WINDIR" => @"C:\Windows",
        "LOCALAPPDATA" => @"C:\Users\me\AppData\Local",
        "PROGRAMDATA" => @"C:\ProgramData",
        "SYSTEMDRIVE" => "C:",
        _ => null,
    };

    [Fact]
    public void Ids_are_unique_and_every_category_has_paths()
    {
        Assert.Equal(CleanupCatalog.All.Count, CleanupCatalog.All.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(CleanupCatalog.All, c =>
        {
            Assert.NotEmpty(c.PathTemplates);
            Assert.NotEmpty(c.IncludePatterns);
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.False(string.IsNullOrWhiteSpace(c.Description));
        });
    }

    [Fact]
    public void Expected_categories_exist_with_sensible_risk()
    {
        Assert.Equal(RiskLevel.Safe, CleanupCatalog.Find("user.temp")!.Risk);
        Assert.Equal(RiskLevel.Moderate, CleanupCatalog.Find("prefetch")!.Risk);
        Assert.Equal(RiskLevel.Moderate, CleanupCatalog.Find("crash.dumps")!.Risk);
        Assert.Equal(RiskLevel.Advanced, CleanupCatalog.Find("windows.old")!.Risk);
        Assert.False(CleanupCatalog.Find("windows.old")!.EnabledByDefault);
        Assert.True(CleanupCatalog.Find("windows.update.cache")!.EnabledByDefault);
        Assert.Null(CleanupCatalog.Find("does.not.exist"));
    }

    [Fact]
    public void Templates_expand_with_known_variables()
    {
        Assert.Equal(@"C:\Users\me\AppData\Local\Temp", CleanupCatalog.ExpandTemplate("%TEMP%", Env));
        Assert.Equal(@"C:\Windows\SoftwareDistribution\Download", CleanupCatalog.ExpandTemplate(@"%WINDIR%\SoftwareDistribution\Download", Env));
        Assert.Equal(@"C:\$Recycle.Bin", CleanupCatalog.ExpandTemplate(@"%SYSTEMDRIVE%\$Recycle.Bin", Env));
    }

    [Fact]
    public void Unknown_variables_are_left_in_place()
    {
        Assert.Equal(@"%NOPE%\x", CleanupCatalog.ExpandTemplate(@"%NOPE%\x", Env));
    }

    [Fact]
    public void Every_template_in_catalog_expands_fully()
    {
        foreach (var category in CleanupCatalog.All)
        {
            foreach (var root in CleanupCatalog.ExpandRoots(category, Env))
            {
                Assert.DoesNotContain("%", root);
                Assert.StartsWith("C:", root);
            }
        }
    }

    [Fact]
    public void Default_ids_are_the_enabled_by_default_ones()
    {
        Assert.Contains("user.temp", CleanupCatalog.DefaultIds);
        Assert.DoesNotContain("windows.old", CleanupCatalog.DefaultIds);
    }
}

public class CleanupPathResolverTests
{
    private static string? Env(string name) => name.ToUpperInvariant() switch
    {
        "TEMP" => @"C:\Users\me\AppData\Local\Temp",
        "WINDIR" => @"C:\Windows",
        "LOCALAPPDATA" => @"C:\Users\me\AppData\Local",
        "APPDATA" => @"C:\Users\me\AppData\Roaming",
        "USERPROFILE" => @"C:\Users\me",
        "PROGRAMDATA" => @"C:\ProgramData",
        "SYSTEMDRIVE" => "C:",
        _ => null,
    };

    private static readonly Dictionary<string, string[]> FakeTree = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\Users\me\AppData\Local\Google\Chrome\User Data"] = ["Default", "Profile 1", "System Profile", "Crashpad"],
        [@"C:\Users\me\AppData\Local\Mozilla\Firefox\Profiles"] = ["abcd1234.default-release", "x9y8.dev-edition"],
        [@"C:\Users\bob\AppData\Local\Google\Chrome\User Data"] = ["Default"],
    };

    private static IEnumerable<string> ListDirs(string parent) => FakeTree.TryGetValue(parent, out var names) ? names : [];

    [Fact]
    public void Appdata_and_userprofile_variables_expand()
    {
        Assert.Equal(@"C:\Users\me\AppData\Roaming\Cache", CleanupCatalog.ExpandTemplate(@"%APPDATA%\Cache", Env));
        Assert.Equal(@"C:\Users\me\Downloads", CleanupCatalog.ExpandTemplate(@"%USERPROFILE%\Downloads", Env));
    }

    [Theory]
    [InlineData("%TEMP%", true)]
    [InlineData(@"%LOCALAPPDATA%\D3DSCache", true)]
    [InlineData(@"%APPDATA%\x", true)]
    [InlineData(@"%USERPROFILE%\x", true)]
    [InlineData(@"%WINDIR%\Temp", false)]
    [InlineData(@"%PROGRAMDATA%\x", false)]
    public void Per_user_templates_are_recognised(string template, bool expected)
    {
        Assert.Equal(expected, CleanupPathResolver.IsPerUserTemplate(template));
    }

    [Fact]
    public void Environment_for_another_profile_retargets_user_variables_only()
    {
        var env = CleanupPathResolver.EnvironmentForProfile(@"C:\Users\bob\", Env);
        Assert.Equal(@"C:\Users\bob", env("USERPROFILE"));
        Assert.Equal(@"C:\Users\bob\AppData\Local", env("LOCALAPPDATA"));
        Assert.Equal(@"C:\Users\bob\AppData\Roaming", env("APPDATA"));
        Assert.Equal(@"C:\Users\bob\AppData\Local\Temp", env("TEMP"));
        Assert.Equal(@"C:\Windows", env("WINDIR"));
    }

    [Fact]
    public void Wildcard_segments_match_existing_folders_only()
    {
        var roots = CleanupPathResolver.ExpandWildcards(@"C:\Users\me\AppData\Local\Google\Chrome\User Data\*\Cache\Cache_Data", ListDirs);
        Assert.Equal(4, roots.Count);
        Assert.Contains(@"C:\Users\me\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data", roots);
        Assert.Contains(@"C:\Users\me\AppData\Local\Google\Chrome\User Data\Profile 1\Cache\Cache_Data", roots);
        Assert.Empty(CleanupPathResolver.ExpandWildcards(@"C:\Nowhere\*\cache2", ListDirs));
        Assert.Equal([@"C:\Windows\Temp"], CleanupPathResolver.ExpandWildcards(@"C:\Windows\Temp", ListDirs));
    }

    [Fact]
    public void Firefox_profiles_expand_to_each_cache2_folder()
    {
        var category = CleanupCatalog.Find("browser.firefox.cache")!;
        var resolved = CleanupPathResolver.Resolve(category, Env, [], ListDirs);
        Assert.Equal(2, resolved.Roots.Count);
        Assert.All(resolved.Roots, r => Assert.EndsWith(@"\cache2", r));
        Assert.Empty(resolved.UsersScanned);
    }

    [Fact]
    public void Other_user_profiles_are_included_and_reported_when_supplied()
    {
        var category = CleanupCatalog.Find("browser.chrome.cache")!;
        var resolved = CleanupPathResolver.Resolve(category, Env, [@"C:\Users\bob", @"C:\Users\Public", @"C:\Users\Default"], ListDirs);
        Assert.Contains(@"C:\Users\bob\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data", resolved.Roots);
        Assert.Equal(["bob"], resolved.UsersScanned);
        Assert.DoesNotContain(resolved.Roots, r => r.Contains(@"\Public\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Machine_wide_categories_ignore_other_users()
    {
        var category = CleanupCatalog.Find("windows.temp")!;
        var resolved = CleanupPathResolver.Resolve(category, Env, [@"C:\Users\bob"], ListDirs);
        Assert.Equal([@"C:\Windows\Temp"], resolved.Roots);
        Assert.Empty(resolved.UsersScanned);
    }

    [Fact]
    public void Unknown_variables_never_become_roots()
    {
        var category = new CleanupCategory("x", "X", "x", RiskLevel.Safe, [@"%NOPE%\cache", @"%WINDIR%\Temp"], ["*"], true, true);
        var resolved = CleanupPathResolver.Resolve(category, Env, [], ListDirs);
        Assert.Equal([@"C:\Windows\Temp"], resolved.Roots);
    }
}

public class CleanupRulesTests
{
    [Fact]
    public void Browser_running_skip_decision()
    {
        Assert.Null(CleanupRules.BrowserRunningReason("browser.chrome.cache", ["explorer", "steam"]));
        var reason = CleanupRules.BrowserRunningReason("browser.chrome.cache", ["Chrome"]);
        Assert.NotNull(reason);
        Assert.Contains("Google Chrome", reason);
        Assert.NotNull(CleanupRules.BrowserRunningReason("browser.edge.cache", ["msedge.exe"]));
        Assert.NotNull(CleanupRules.BrowserRunningReason("browser.firefox.cache", ["firefox"]));
        Assert.Null(CleanupRules.BrowserRunningReason("user.temp", ["chrome", "msedge", "firefox"]));
    }

    [Fact]
    public void Directories_are_pruned_deepest_first()
    {
        var ordered = CleanupRules.OrderForPruning([@"C:\t\a", @"C:\t\a\b\c", @"C:\t\a\b", @"C:\t\z", @"C:\t\a\b\c"]);
        Assert.Equal([@"C:\t\a\b\c", @"C:\t\a\b", @"C:\t\z", @"C:\t\a"], ordered);
    }

    [Fact]
    public void Parent_directories_stop_at_the_root()
    {
        var parents = CleanupRules.ParentDirectoriesInsideRoots(@"C:\Temp\a\b\file.tmp", [@"C:\Temp"]).ToList();
        Assert.Equal([@"C:\Temp\a\b", @"C:\Temp\a"], parents);
        Assert.Empty(CleanupRules.ParentDirectoriesInsideRoots(@"C:\Temp\file.tmp", [@"C:\Temp"]));
        Assert.Empty(CleanupRules.ParentDirectoriesInsideRoots(@"D:\Other\x\file.tmp", [@"C:\Temp"]));
    }

    [Fact]
    public void Sizes_aggregate_and_largest_files_are_picked()
    {
        var files = new[]
        {
            new CleanupFile(@"C:\t\small.tmp", 10, DateTime.Now),
            new CleanupFile(@"C:\t\big.tmp", 5000, DateTime.Now),
            new CleanupFile(@"C:\t\mid.tmp", 300, DateTime.Now),
            new CleanupFile(@"C:\t\negative.tmp", -5, DateTime.Now),
        };
        var (count, bytes) = CleanupRules.Aggregate(files);
        Assert.Equal(4, count);
        Assert.Equal(5310, bytes);
        var top = CleanupRules.Largest(files, 2);
        Assert.Equal([@"C:\t\big.tmp", @"C:\t\mid.tmp"], top.Select(f => f.Path));
    }

    [Fact]
    public void Own_log_folder_is_detected_case_insensitively()
    {
        Assert.True(CleanupRules.IsUnderDirectory(@"C:\Users\me\AppData\Local\PcChecker\logs\app.log", @"c:\users\me\appdata\local\pcchecker\LOGS"));
        Assert.True(CleanupRules.IsUnderDirectory(@"C:\Logs", @"C:\Logs\"));
        Assert.False(CleanupRules.IsUnderDirectory(@"C:\Logs2\x", @"C:\Logs"));
        Assert.False(CleanupRules.IsUnderDirectory(null, @"C:\Logs"));
    }

    [Fact]
    public void Notes_are_distinguished_from_errors()
    {
        var note = CleanupRules.Note("Scanned bob");
        Assert.True(CleanupRules.IsNote(note));
        Assert.Equal("Scanned bob", CleanupRules.NoteText(note));
        Assert.False(CleanupRules.IsNote("Access denied"));
        Assert.Equal("Access denied", CleanupRules.NoteText("Access denied"));
    }

    [Fact]
    public void Update_services_are_paused_only_for_update_categories()
    {
        Assert.Empty(CleanupRules.ServicesToStop(["user.temp", "thumbnails"]));
        Assert.Equal(["wuauserv", "bits"], CleanupRules.ServicesToStop(["windows.update.cache"]));
        Assert.Contains("DoSvc", CleanupRules.ServicesToStop(["delivery.optimization"]));
    }

    [Fact]
    public void Windows_old_is_report_only_and_recycle_bin_is_special()
    {
        Assert.True(CleanupRules.IsReportOnly("windows.old"));
        Assert.False(CleanupRules.IsReportOnly("user.temp"));
        Assert.True(CleanupRules.IsRecycleBin("recycle.bin"));
    }

    [Fact]
    public void Selection_falls_back_to_defaults_and_hides_advanced()
    {
        var defaults = CleanupRules.ResolveSelection(null, CleanupCatalog.All, false);
        Assert.Contains("user.temp", defaults);
        Assert.DoesNotContain("prefetch", defaults);
        Assert.DoesNotContain("windows.old", defaults);

        var saved = CleanupRules.ResolveSelection(["prefetch", "windows.old", "does.not.exist"], CleanupCatalog.All, false);
        Assert.Equal(["prefetch"], saved);
        Assert.Contains("windows.old", CleanupRules.ResolveSelection(["prefetch", "windows.old"], CleanupCatalog.All, true));
    }

    [Fact]
    public void Safe_only_selection_needs_no_restore_point()
    {
        Assert.True(CleanupRules.IsSafeOnly([CleanupCatalog.Find("user.temp")!, CleanupCatalog.Find("thumbnails")!]));
        Assert.False(CleanupRules.IsSafeOnly([CleanupCatalog.Find("user.temp")!, CleanupCatalog.Find("prefetch")!]));
    }

    [Fact]
    public void Run_summary_is_plain_english()
    {
        var real = CleanupRules.Summarize(new CleanupRunResult(412, 1_380_000_000, [@"C:\a (in use)", @"C:\b (in use)"], [], false));
        Assert.StartsWith("Freed ", real);
        Assert.Contains("412 files", real);
        Assert.Contains("2 items were skipped", real);
        Assert.DoesNotContain("error", real);

        var dry = CleanupRules.Summarize(new CleanupRunResult(1, 2048, [], ["x"], true));
        Assert.StartsWith("Dry run:", dry);
        Assert.Contains("1 file ", dry);
        Assert.Contains("1 error.", dry);
    }

    [Fact]
    public void Categories_sort_by_size_keeping_catalog_order_for_ties()
    {
        var a = new CategoryScanResult(CleanupCatalog.All[0], 1, 10, [], []);
        var b = new CategoryScanResult(CleanupCatalog.All[1], 1, 500, [], []);
        var c = new CategoryScanResult(CleanupCatalog.All[2], 1, 10, [], []);
        var sorted = CleanupRules.SortBySize([a, b, c]);
        Assert.Equal([b, a, c], sorted);
    }

    [Fact]
    public void Catalog_covers_every_gpu_vendor_shader_cache_and_browsers()
    {
        Assert.NotNull(CleanupCatalog.Find("nvidia.shader.cache"));
        Assert.NotNull(CleanupCatalog.Find("amd.shader.cache"));
        Assert.NotNull(CleanupCatalog.Find("intel.shader.cache"));
        Assert.NotNull(CleanupCatalog.Find("browser.brave.cache"));
        Assert.All(CleanupRules.BrowserProcesses.Keys, id => Assert.NotNull(CleanupCatalog.Find(id)));
        Assert.All(CleanupCatalog.Find("browser.chrome.cache")!.PathTemplates, t => Assert.Contains(@"\*\", t));
    }
}

public class DemoCleanupTests
{
    [Fact]
    public async Task Demo_scanner_returns_sized_categories_biggest_first_with_files_and_notes()
    {
        var scanner = new DemoTempFileScanner();
        var scan = await scanner.ScanAsync([], null, CancellationToken.None);
        Assert.Equal(CleanupCatalog.All.Count, scan.Categories.Count);
        Assert.True(scan.TotalBytes > 10_000_000_000);
        Assert.True(scan.Categories[0].TotalBytes >= scan.Categories[^1].TotalBytes);

        var temp = scan.Categories.Single(c => c.Category.Id == "user.temp");
        Assert.Equal(412, temp.FileCount);
        Assert.Equal(temp.TotalBytes, temp.Files.Sum(f => f.SizeBytes));
        Assert.All(temp.Files, f => Assert.StartsWith(@"C:\Users\", f.Path));
        Assert.True(temp.Files[0].SizeBytes >= temp.Files[^1].SizeBytes);

        var edge = scan.Categories.Single(c => c.Category.Id == "browser.edge.cache");
        Assert.Contains(edge.Errors, CleanupRules.IsNote);
        var dumps = scan.Categories.Single(c => c.Category.Id == "crash.dumps");
        Assert.Contains(dumps.Files, f => f.Path == @"C:\Windows\MEMORY.DMP");
    }

    [Fact]
    public async Task Demo_scanner_honours_requested_ids()
    {
        var scan = await new DemoTempFileScanner().ScanAsync(["thumbnails", "recycle.bin"], null, CancellationToken.None);
        Assert.Equal(2, scan.Categories.Count);
    }

    [Fact]
    public async Task Demo_cleaner_dry_run_and_real_run_report_sensibly()
    {
        var scan = await new DemoTempFileScanner().ScanAsync([], null, CancellationToken.None);
        var cleaner = new DemoTempFileCleaner();

        var dry = await cleaner.CleanAsync(scan, ["user.temp", "windows.old"], true, null, CancellationToken.None);
        Assert.True(dry.DryRun);
        Assert.Equal(412, dry.DeletedFiles);
        Assert.Single(dry.Skipped);

        var real = await cleaner.CleanAsync(scan, ["user.temp", "browser.chrome.cache"], false, null, CancellationToken.None);
        Assert.False(real.DryRun);
        Assert.True(real.DeletedFiles > 0);
        Assert.True(real.FreedBytes > 0);
        Assert.NotEmpty(real.Skipped);
        Assert.Empty(real.Errors);

        var none = await cleaner.CleanAsync(scan, ["nope"], false, null, CancellationToken.None);
        Assert.Equal(0, none.DeletedFiles);
        Assert.NotEmpty(none.Errors);
        Assert.True((await cleaner.EmptyRecycleBinAsync(CancellationToken.None)).Success);
    }
}
