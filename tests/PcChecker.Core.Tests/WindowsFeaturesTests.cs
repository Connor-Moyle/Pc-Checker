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
    }
}
