using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using Xunit;

namespace PcChecker.Core.Tests;

public class DriverRulesTests
{
    private static DriverInfo Driver(bool signed = true, DateTime? date = null, int problem = 0, bool inbox = false) =>
        new("Device", "PCI\\VEN", "Display", "Vendor", "1.0", date ?? DateTime.Today.AddDays(-30), "x.inf", signed, "Vendor", inbox, [], "OK", problem, null);

    [Fact]
    public void Fresh_signed_driver_has_no_flags()
    {
        Assert.Empty(DriverRules.Evaluate(Driver()));
        Assert.False(DriverRules.NeedsAttention(Driver()));
    }

    [Fact]
    public void Unsigned_and_old_drivers_are_flagged()
    {
        var flags = DriverRules.Evaluate(Driver(signed: false, date: DateTime.Today.AddYears(-4)));
        Assert.Contains(flags, f => f.Title == "Unsigned driver" && f.Severity == Severity.Medium);
        Assert.Contains(flags, f => f.Title == "Old driver" && f.Severity == Severity.Low);
    }

    [Fact]
    public void Old_inbox_drivers_are_not_flagged_for_age()
    {
        Assert.DoesNotContain(DriverRules.Evaluate(Driver(date: DateTime.Today.AddYears(-6), inbox: true)), f => f.Title == "Old driver");
    }

    [Fact]
    public void Problem_codes_use_device_descriptions()
    {
        var flags = DriverRules.Evaluate(Driver(problem: 43));
        var f = Assert.Single(flags);
        Assert.Equal(Severity.High, f.Severity);
        Assert.Contains("code 43", f.Title);
        Assert.Equal(Severity.Low, Assert.Single(DriverRules.Evaluate(Driver(problem: 22))).Severity);
    }

    [Fact]
    public void Age_days_is_computed()
    {
        Assert.Equal(30, Driver().AgeDays);
        Assert.Null(new DriverInfo("d", "id", "c", null, null, null, null, true, null, false, [], null, 0, null).AgeDays);
    }

    [Fact]
    public void Vendor_links_are_deduplicated_and_matched_by_keyword()
    {
        var links = DriverRules.BuildVendorLinks(["NVIDIA GeForce RTX 4070", "Intel(R) Core(TM) i7", "ASUSTeK COMPUTER INC.", "NVIDIA Corporation", null, ""]);
        Assert.Equal(3, links.Count);
        Assert.Contains(links, l => l.Vendor == "NVIDIA");
        Assert.Contains(links, l => l.Vendor == "Intel");
        Assert.Contains(links, l => l.Vendor == "ASUS");
        Assert.All(links, l => Assert.StartsWith("https://", l.Url));
    }

    [Fact]
    public void No_manufacturers_means_no_links()
    {
        Assert.Empty(DriverRules.BuildVendorLinks([]));
        Assert.Empty(DriverRules.BuildVendorLinks([null, "", "Unknown Vendor Ltd"]));
    }

    [Fact]
    public void Vendor_table_is_consistent()
    {
        Assert.Equal(DriverRules.VendorTable.Count, DriverRules.VendorTable.Select(v => v.Keyword.ToLowerInvariant()).Distinct().Count());
        Assert.All(DriverRules.VendorTable, v =>
        {
            Assert.True(Uri.TryCreate(v.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https", v.Url);
            Assert.False(string.IsNullOrWhiteSpace(v.Note));
        });
    }

    [Fact]
    public void Old_driver_threshold_is_three_years()
    {
        Assert.DoesNotContain(DriverRules.Evaluate(Driver(date: DateTime.Today.AddDays(-DriverRules.OldDriverDays + 1))), f => f.Title == "Old driver");
        var flag = Assert.Single(DriverRules.Evaluate(Driver(date: DateTime.Today.AddDays(-DriverRules.OldDriverDays))));
        Assert.Equal("Old driver", flag.Title);
        Assert.Contains("3 years", flag.Detail);
    }

    [Fact]
    public void Driver_without_date_is_not_flagged_as_old()
    {
        var driver = new DriverInfo("d", "id", "c", null, null, null, null, true, null, false, [], null, 0, null);
        Assert.Empty(DriverRules.Evaluate(driver));
    }

    [Fact]
    public void Multiple_issues_are_all_reported_and_need_attention()
    {
        var driver = Driver(signed: false, date: DateTime.Today.AddYears(-5), problem: 28);
        var flags = DriverRules.Evaluate(driver);
        Assert.Equal(3, flags.Count);
        Assert.True(DriverRules.NeedsAttention(driver));
        Assert.Contains(flags, f => f.Title.Contains("code 28") && f.Severity == Severity.High);
    }
}
