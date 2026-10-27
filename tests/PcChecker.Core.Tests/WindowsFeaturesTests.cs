using PcChecker.Core.Optimization;
using PcChecker.Core.WindowsFeatures;
using Xunit;

namespace PcChecker.Core.Tests;

public class InboxAppCatalogTests
{
    [Fact]
    public void Catalog_has_about_forty_unique_entries()
    {
        Assert.InRange(InboxAppCatalog.All.Count, 38, 80);
        Assert.Equal(InboxAppCatalog.All.Count, InboxAppCatalog.All.Select(e => e.PackageFamilyPrefix).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(InboxAppCatalog.All, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.FriendlyName));
            Assert.False(string.IsNullOrWhiteSpace(e.Note));
        });
    }

    [Fact]
    public void Store_is_advanced_and_weather_is_safe()
    {
        Assert.Equal(RiskLevel.Advanced, InboxAppCatalog.Find("Microsoft.WindowsStore_22312.1401.4.0_x64__8wekyb3d8bbwe")!.Risk);
        Assert.Equal(RiskLevel.Safe, InboxAppCatalog.Find("Microsoft.BingWeather_4.53.52331.0_x64__8wekyb3d8bbwe")!.Risk);
        Assert.Equal(RiskLevel.Moderate, InboxAppCatalog.Find("Microsoft.XboxApp")!.Risk);
        Assert.Equal(RiskLevel.Moderate, InboxAppCatalog.Find("Microsoft.Windows.Photos")!.Risk);
    }

    [Fact]
    public void Friendly_name_falls_back_to_last_segment()
    {
        Assert.Equal("Weather", InboxAppCatalog.FriendlyName("Microsoft.BingWeather_1.0_x64__8wekyb3d8bbwe"));
        Assert.Equal("SomethingNew", InboxAppCatalog.FriendlyName("Contoso.SomethingNew_1.0_x64__abc"));
    }

    [Fact]
    public void Unknown_packages_are_moderate()
    {
        Assert.Equal(RiskLevel.Moderate, InboxAppCatalog.RiskFor("Contoso.Mystery"));
        Assert.Null(InboxAppCatalog.Find(null));
        Assert.Null(InboxAppCatalog.Find("   "));
    }

    [Fact]
    public void Lookup_ignores_case_and_matches_family_names()
    {
        Assert.Equal("Weather", InboxAppCatalog.Find("microsoft.bingweather_8wekyb3d8bbwe")!.FriendlyName);
        Assert.Equal("Maps", InboxAppCatalog.FriendlyName("Microsoft.WindowsMaps"));
        Assert.Equal("Cortana", InboxAppCatalog.FriendlyName("Microsoft.549981C3F5F10_4.2204.13303.0_x64__8wekyb3d8bbwe"));
    }

    [Fact]
    public void Advanced_entries_are_advanced_only_and_carry_do_not_remove_style_notes()
    {
        var advanced = InboxAppCatalog.All.Where(e => e.Risk == RiskLevel.Advanced).ToList();
        Assert.NotEmpty(advanced);
        Assert.All(advanced, e => Assert.True(e.AdvancedOnly, e.PackageFamilyPrefix));
        Assert.All(InboxAppCatalog.All.Where(e => e.Risk != RiskLevel.Advanced), e => Assert.False(e.AdvancedOnly, e.PackageFamilyPrefix));
        Assert.Contains(advanced, e => e.PackageFamilyPrefix == "Microsoft.DesktopAppInstaller");
        Assert.Contains(advanced, e => e.PackageFamilyPrefix == "Microsoft.SecHealthUI");
    }

    [Fact]
    public void Safe_entries_say_they_are_safe_to_remove()
    {
        var safe = InboxAppCatalog.All.Where(e => e.Risk == RiskLevel.Safe).ToList();
        Assert.True(safe.Count >= 20);
        Assert.All(safe, e => Assert.Contains("safe to remove", e.Note, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Prefixes_do_not_shadow_each_other()
    {
        // A shorter prefix listed before a longer one would hide the longer entry from Find().
        foreach (var entry in InboxAppCatalog.All)
        {
            Assert.Same(entry, InboxAppCatalog.Find(entry.PackageFamilyPrefix + "_1.0.0.0_x64__8wekyb3d8bbwe"));
        }
    }

    [Fact]
    public void Gaming_components_are_never_marked_safe()
    {
        foreach (var name in new[] { "Microsoft.XboxApp", "Microsoft.GamingApp", "Microsoft.XboxGamingOverlay", "Microsoft.XboxIdentityProvider" })
        {
            Assert.NotEqual(RiskLevel.Safe, InboxAppCatalog.RiskFor(name));
        }
    }
}

public class InboxAppRemovalScriptTests
{
    [Fact]
    public void Removing_for_this_account_fails_when_the_app_is_not_installed_for_it()
    {
        var script = WindowsFeatureService.BuildRemoveAppScript("Microsoft.BingNews", "News", allUsers: false);
        Assert.Contains("$p = @(Get-AppxPackage -Name 'Microsoft.BingNews')", script);
        Assert.Contains("if ($p.Count -eq 0) { throw (", script);
        Assert.Contains("News is not installed for the account PC Checker runs as", script);
        Assert.Contains("$p | Remove-AppxPackage;", script);
        Assert.DoesNotContain("-AllUsers", script);
        Assert.True(script.IndexOf("throw", StringComparison.Ordinal) < script.IndexOf("Removed = $true", StringComparison.Ordinal));
    }

    [Fact]
    public void Removing_for_all_users_fails_only_when_no_account_and_no_provisioned_copy_has_the_app()
    {
        var script = WindowsFeatureService.BuildRemoveAppScript("Microsoft.BingNews", "News", allUsers: true);
        Assert.Contains("$p = @(Get-AppxPackage -AllUsers -Name 'Microsoft.BingNews')", script);
        Assert.Contains("$_.DisplayName -eq 'Microsoft.BingNews'", script);
        Assert.Contains("if ($p.Count -eq 0 -and $v.Count -eq 0) { throw 'News is not installed for any account", script);
        Assert.Contains("$p | Remove-AppxPackage -AllUsers;", script);
        Assert.Contains("$v | Remove-AppxProvisionedPackage -Online | Out-Null;", script);
    }

    [Fact]
    public void Names_with_quotes_are_escaped_for_powershell()
    {
        var script = WindowsFeatureService.BuildRemoveAppScript("Vendor.App'X", "Bob's app", allUsers: false);
        Assert.Contains("-Name 'Vendor.App''X'", script);
        Assert.Contains("'Bob''s app is not installed", script);
    }
}
