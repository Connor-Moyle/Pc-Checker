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
