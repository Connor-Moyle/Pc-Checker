using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;
using Xunit;

namespace PcChecker.Core.Tests;

public class DriverRulesTests
{
    private static DriverInfo Driver(bool signed = true, DateTime? date = null, int problem = 0, bool inbox = false, string inf = "oem3.inf") =>
        new("Device", "PCI\\VEN", "Display", "Vendor", "1.0", date ?? DateTime.Today.AddDays(-30), inf, signed, "Vendor", inbox, [], "OK", problem, null);

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
        Assert.False(DriverRules.IsOld(Driver(date: DateTime.Today.AddYears(-6), inbox: true)));
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
        Assert.Empty(DriverRules.VendorLinksFor(null));
        Assert.Empty(DriverRules.VendorLinksFor(new HardwareInventory()));
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
        Assert.False(DriverRules.IsOld(Driver(date: DateTime.Today.AddDays(-DriverRules.OldDriverDays + 1))));
        var flag = Assert.Single(DriverRules.Evaluate(Driver(date: DateTime.Today.AddDays(-DriverRules.OldDriverDays))));
        Assert.Equal("Old driver", flag.Title);
        Assert.Contains("3 years", flag.Detail);
        Assert.True(DriverRules.IsOld(Driver(date: DateTime.Today.AddDays(-DriverRules.OldDriverDays))));
    }

    [Fact]
    public void Driver_without_date_is_not_flagged_as_old()
    {
        var driver = new DriverInfo("d", "id", "c", null, null, null, null, true, null, false, [], null, 0, null);
        Assert.Empty(DriverRules.Evaluate(driver));
        Assert.False(DriverRules.IsOld(driver));
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

    [Fact]
    public void Vendor_links_for_the_demo_inventory_cover_gpu_cpu_board_and_storage()
    {
        var links = DriverRules.VendorLinksFor(DemoHardwareInventoryService.Build());
        Assert.Contains(links, l => l.Vendor == "NVIDIA");
        Assert.Contains(links, l => l.Vendor == "Intel");
        Assert.Contains(links, l => l.Vendor == "ASUS");
        Assert.Contains(links, l => l.Vendor == "Samsung");
        Assert.Contains(links, l => l.Vendor == "Western Digital");
        Assert.Equal(links.Count, links.Select(l => l.Vendor + "|" + l.Component).Distinct().Count());
    }

    [Fact]
    public void Only_third_party_oem_packages_can_be_rolled_back()
    {
        Assert.True(DriverRules.CanRollBack(Driver(inf: "oem12.inf")));
        Assert.False(DriverRules.CanRollBack(Driver(inf: "stornvme.inf", inbox: true)));
        Assert.False(DriverRules.CanRollBack(Driver(inf: "oem12.inf", inbox: true)));
        Assert.False(DriverRules.CanRollBack(new DriverInfo("d", "id", "c", null, null, null, null, true, null, false, [], null, 0, null)));
    }

    [Fact]
    public void Deep_links_are_defined()
    {
        Assert.StartsWith("ms-settings:", DriverRules.OptionalUpdatesUri);
        Assert.EndsWith(".msc", DriverRules.DeviceManagerCommand);
    }
}

public class DriverInfoBuilderTests
{
    private static Dictionary<string, object?> Bag(params (string Key, object? Value)[] values)
    {
        var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values)
        {
            bag[k] = v;
        }

        return bag;
    }

    [Fact]
    public void Dmtf_driver_date_is_parsed()
    {
        Assert.True(WmiPropertyMapper.TryParseDmtfDate("20240315000000.000000+000", out var date));
        Assert.Equal(new DateTime(2024, 3, 15), date.Date);
        Assert.True(WmiPropertyMapper.TryParseDmtfDate("20230801120000.000000-060", out var withOffset));
        Assert.Equal(2023, withOffset.Year);
        Assert.False(WmiPropertyMapper.TryParseDmtfDate("not a date", out _));
        Assert.False(WmiPropertyMapper.TryParseDmtfDate(null, out _));
    }

    [Fact]
    public void Signed_driver_bag_becomes_a_driver_row()
    {
        var bag = Bag(("DeviceName", "NVIDIA GeForce RTX 4070"), ("DeviceID", @"PCI\VEN_10DE&DEV_2786"), ("DeviceClass", "DISPLAY"),
            ("DriverProviderName", "NVIDIA"), ("DriverVersion", "32.0.15.6094"), ("DriverDate", "20250210000000.000000+000"),
            ("InfName", "oem12.inf"), ("IsSigned", true), ("Signer", "NVIDIA Corporation"), ("HardWareID", @"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043"),
            ("CompatID", new[] { @"PCI\VEN_10DE&CC_0300", @"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043" }), ("Location", "PCI bus 1"));
        var d = DriverInfoBuilder.FromProperties(bag, new DriverInfoBuilder.EntityFacts(0, "OK", true));
        Assert.Equal("NVIDIA GeForce RTX 4070", d.DeviceName);
        Assert.Equal("DISPLAY", d.DeviceClass);
        Assert.Equal("NVIDIA", d.Provider);
        Assert.Equal(new DateTime(2025, 2, 10), d.Date!.Value.Date);
        Assert.True(d.IsSigned);
        Assert.False(d.IsInbox);
        Assert.Equal(2, d.HardwareIds.Count);
        Assert.Equal("OK", d.Status);
        Assert.Equal(0, d.ProblemCode);
        Assert.Equal("PCI bus 1", d.Location);
    }

    [Fact]
    public void Problem_code_and_status_come_from_the_pnp_entity()
    {
        var bag = Bag(("DeviceName", "ASUS ACPI"), ("DeviceID", @"ACPI\ASUS2021"), ("DeviceClass", "Unknown"));
        var d = DriverInfoBuilder.FromProperties(bag, new DriverInfoBuilder.EntityFacts(28, "Error", true));
        Assert.Equal(28, d.ProblemCode);
        Assert.True(d.HasProblem);
        Assert.Equal("Error", d.Status);
        Assert.False(d.IsSigned);
        Assert.Null(d.Date);
    }

    [Fact]
    public void Disconnected_devices_are_marked_not_present_and_are_not_problems()
    {
        var bag = Bag(("DeviceName", "USB stick"), ("DeviceID", @"USBSTOR\X"), ("DeviceClass", "DiskDrive"));
        var phantom = DriverInfoBuilder.FromProperties(bag, new DriverInfoBuilder.EntityFacts(0, null, false));
        Assert.False(phantom.IsPresent);
        Assert.Equal(0, phantom.ProblemCode);
        Assert.False(phantom.HasProblem);

        // A stale problem code on a device that is not connected is not a live problem either.
        var stale = DriverInfoBuilder.FromProperties(bag, new DriverInfoBuilder.EntityFacts(45, "Unknown", false));
        Assert.False(stale.HasProblem);
        Assert.DoesNotContain(DriverRules.Evaluate(stale), f => f.Title.StartsWith("Device problem", StringComparison.Ordinal));

        // Unknown presence (no Win32_PnPEntity row) counts as connected.
        var unknown = DriverInfoBuilder.FromProperties(bag, null);
        Assert.True(unknown.IsPresent);
        Assert.Equal(0, unknown.ProblemCode);
    }

    [Fact]
    public void Missing_name_falls_back_to_friendly_name_description_or_id()
    {
        Assert.Equal("Friendly", DriverInfoBuilder.FromProperties(Bag(("FriendlyName", "Friendly"), ("DeviceID", "X"))).DeviceName);
        Assert.Equal("Desc", DriverInfoBuilder.FromProperties(Bag(("Description", "Desc"), ("DeviceID", "X"))).DeviceName);
        Assert.Equal("X", DriverInfoBuilder.FromProperties(Bag(("DeviceID", "X"))).DeviceName);
        Assert.Equal("Unknown device", DriverInfoBuilder.FromProperties(Bag()).DeviceName);
        Assert.Equal("Unknown", DriverInfoBuilder.FromProperties(Bag()).DeviceClass);
    }

    [Theory]
    [InlineData("oem12.inf", "NVIDIA", false)]
    [InlineData("OEM0.INF", "Microsoft", false)]
    [InlineData("stornvme.inf", "Microsoft", true)]
    [InlineData("nv_dispi.inf", "NVIDIA", true)]
    [InlineData(null, "Microsoft Windows", true)]
    [InlineData(null, "Realtek", false)]
    [InlineData("", null, false)]
    public void Inbox_classification_uses_inf_name_then_provider(string? inf, string? provider, bool expectedInbox)
    {
        Assert.Equal(expectedInbox, DriverInfoBuilder.IsInboxDriver(inf, provider));
    }

    [Theory]
    [InlineData("oem12.inf", true)]
    [InlineData(" oem0.inf ", true)]
    [InlineData("oem.inf", false)]
    [InlineData("oemX.inf", false)]
    [InlineData("stornvme.inf", false)]
    [InlineData("oem12.sys", false)]
    [InlineData(null, false)]
    public void Oem_inf_names_are_recognised(string? inf, bool expected)
    {
        Assert.Equal(expected, DriverInfoBuilder.IsOemInf(inf));
    }

    [Fact]
    public void Duplicate_device_ids_keep_the_newest_and_richest_row()
    {
        var older = new DriverInfo("Dev", @"PCI\A", "Net", "Intel", "1.0", new DateTime(2022, 1, 1), "oem1.inf", true, null, false, [], "OK", 0, null);
        var newer = new DriverInfo("Dev", @"pci\a", "Net", "Intel", "2.0", new DateTime(2024, 1, 1), "oem2.inf", true, null, false, [@"PCI\A"], "OK", 0, null);
        var other = new DriverInfo("Other", @"PCI\B", "Net", null, null, null, null, false, null, false, [], null, 0, null);
        var result = DriverInfoBuilder.Deduplicate([older, newer, other]);
        Assert.Equal(2, result.Count);
        Assert.Equal("2.0", result[0].Version);
        Assert.Equal("Other", result[1].DeviceName);
    }

    [Fact]
    public void Sort_puts_problem_devices_first_disconnected_last_then_class_and_name()
    {
        var a = new DriverInfo("Zeta", "1", "Net", null, null, null, null, true, null, false, [], null, 0, null);
        var b = new DriverInfo("Alpha", "2", "Net", null, null, null, null, true, null, false, [], null, 0, null);
        var c = new DriverInfo("Broken", "3", "USB", null, null, null, null, true, null, false, [], null, 10, null);
        var d = new DriverInfo("Display", "4", "Display", null, null, null, null, true, null, false, [], null, 0, null);
        var e = new DriverInfo("Absent", "5", "DiskDrive", null, null, null, null, true, null, false, [], null, 0, null) { IsPresent = false };
        var sorted = DriverInfoBuilder.Sort([e, a, b, c, d]);
        Assert.Equal(["Broken", "Display", "Alpha", "Zeta", "Absent"], sorted.Select(x => x.DeviceName).ToArray());
    }

    [Fact]
    public void Summary_counts_match_the_rules_and_skip_disconnected_devices_by_default()
    {
        var all = DemoDriverService.SampleDrivers();
        var connected = all.Where(d => d.IsPresent).ToList();
        Assert.True(connected.Count < all.Count);

        var summary = DriverInfoBuilder.Summarize(all);
        Assert.Equal(connected.Count, summary.Total);
        Assert.Equal(connected.Count(d => !d.IsInbox), summary.ThirdParty);
        Assert.Equal(connected.Count(d => d.HasProblem), summary.WithProblems);
        Assert.Equal(connected.Count(DriverRules.IsOld), summary.Old);
        Assert.Equal(connected.Count(d => !d.IsSigned), summary.Unsigned);
        Assert.Equal(all.Count - connected.Count, summary.Disconnected);
        Assert.True(summary.WithProblems >= 1);
        Assert.True(summary.Old >= 2);
        Assert.True(summary.Unsigned >= 1);

        var everything = DriverInfoBuilder.Summarize(all, includeDisconnected: true);
        Assert.Equal(all.Count, everything.Total);
        Assert.Equal(summary.Disconnected, everything.Disconnected);
        Assert.Equal(summary.WithProblems, everything.WithProblems);
    }
}

public class DriverListExportTests
{
    [Fact]
    public void Csv_has_header_and_one_quoted_row_per_driver()
    {
        var drivers = new List<DriverInfo>
        {
            new("Realtek(R) Audio, HD", @"HDAUDIO\X", "MEDIA", "Realtek \"Semi\"", "6.0", new DateTime(2024, 1, 2), "oem1.inf", true, null, false, [], "OK", 0, null),
            new("Plain", "ID", "Net", null, null, null, null, false, null, true, [], null, 28, null),
        };
        var csv = DriverListExport.BuildCsv(drivers);
        var lines = csv.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal(DriverListExport.Header, lines[0]);
        Assert.StartsWith("\"Realtek(R) Audio, HD\",MEDIA,\"Realtek \"\"Semi\"\"\",6.0,2024-01-02,", lines[1]);
        Assert.Equal("Plain,Net,,,,,No,Windows,,28,,ID", lines[2]);
        Assert.Equal(string.Empty, DriverListExport.Escape(null));
        Assert.Equal("a", DriverListExport.Escape("a"));
    }
}

public class PnpUtilParserTests
{
    private const string SampleEnum = """
        Microsoft PnP Utility

        Published Name:     oem10.inf
        Original Name:      nvhda.inf
        Provider Name:      NVIDIA Corporation
        Class Name:         Sound, video and game controllers
        Class GUID:         {4d36e96c-e325-11ce-bfc1-08002be10318}
        Driver Version:     03/22/2023 1.4.1.0
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher

        Published Name:     oem12.inf
        Original Name:      nv_dispi.inf
        Provider Name:      NVIDIA Corporation
        Class Name:         Display adapters
        Class GUID:         {4d36e968-e325-11ce-bfc1-08002be10318}
        Class Version:      1.0
        Driver Version:     02/10/2025 32.0.15.6094
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher

        Published Name:     oem3.inf
        Original Name:      unsigned.inf
        Provider Name:      Example
        Class Name:         System devices
        Driver Version:     01/05/2021 1.0

        """;

    [Fact]
    public void Enum_drivers_output_is_split_into_packages()
    {
        var packages = PnpUtilParser.ParseEnumDrivers(SampleEnum);
        Assert.Equal(3, packages.Count);
        var nv = packages[1];
        Assert.Equal("oem12.inf", nv.PublishedName);
        Assert.Equal("nv_dispi.inf", nv.OriginalName);
        Assert.Equal("NVIDIA Corporation", nv.Provider);
        Assert.Equal("Display adapters", nv.ClassName);
        Assert.Equal("{4d36e968-e325-11ce-bfc1-08002be10318}", nv.ClassGuid);
        Assert.Equal("32.0.15.6094", nv.Version);
        Assert.Equal(new DateTime(2025, 2, 10), nv.Date);
        Assert.Equal("Microsoft Windows Hardware Compatibility Publisher", nv.Signer);
        Assert.Null(packages[2].Signer);
        Assert.Null(packages[2].ClassGuid);
        Assert.Equal(new DateTime(2021, 1, 5), packages[2].Date);
    }

    [Fact]
    public void Enum_drivers_tolerates_empty_output_and_windows_line_endings()
    {
        Assert.Empty(PnpUtilParser.ParseEnumDrivers(null));
        Assert.Empty(PnpUtilParser.ParseEnumDrivers("Microsoft PnP Utility\r\n\r\n"));
        var crlf = PnpUtilParser.ParseEnumDrivers("Published Name:     oem1.inf\r\nOriginal Name:      a.inf\r\n\r\nPublished Name:     oem2.inf\r\n");
        Assert.Equal(2, crlf.Count);
        Assert.Equal("a.inf", crlf[0].OriginalName);
    }

    [Theory]
    [InlineData("03/22/2023 1.4.1.0", 2023, 3, 22, "1.4.1.0")]
    [InlineData("2024-11-05 2.1.4.3", 2024, 11, 5, "2.1.4.3")]
    [InlineData("1.4.1.0", null, null, null, "1.4.1.0")]
    [InlineData("", null, null, null, null)]
    public void Driver_version_field_is_split_into_date_and_version(string field, int? y, int? m, int? d, string? version)
    {
        var (date, ver) = PnpUtilParser.SplitVersionField(field);
        Assert.Equal(version, ver);
        if (y is null)
        {
            Assert.Null(date);
        }
        else
        {
            Assert.Equal(new DateTime(y.Value, m!.Value, d!.Value), date);
        }
    }

    [Theory]
    [InlineData("en-GB", "03/04/2023", 2023, 4, 3)]
    [InlineData("en-AU", "03/04/2023", 2023, 4, 3)]
    [InlineData("fr-FR", "03/04/2023", 2023, 4, 3)]
    [InlineData("en-US", "03/04/2023", 2023, 3, 4)]
    [InlineData("de-DE", "22.03.2023", 2023, 3, 22)]
    [InlineData("en-GB", "03/22/2023", 2023, 3, 22)] // not a valid en-GB date, so the invariant fallback applies
    [InlineData("fr-FR", "2024-11-05", 2024, 11, 5)] // ISO is unambiguous everywhere
    public void Driver_dates_follow_the_machine_culture_before_the_invariant_formats(string cultureName, string text, int y, int m, int d)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(cultureName);
        Assert.True(PnpUtilParser.TryParseDate(text, culture, out var date));
        Assert.Equal(new DateTime(y, m, d), date);
    }

    [Fact]
    public void Driver_dates_use_the_current_culture_by_default()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
            Assert.True(PnpUtilParser.TryParseDate("03/04/2023", out var date));
            Assert.Equal(new DateTime(2023, 4, 3), date);
            var (split, version) = PnpUtilParser.SplitVersionField("03/04/2023 31.0.15.3598");
            Assert.Equal(new DateTime(2023, 4, 3), split);
            Assert.Equal("31.0.15.3598", version);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Export_progress_lines_and_summary_are_recognised()
    {
        string[] lines =
        [
            "Microsoft PnP Utility",
            "",
            "Exporting driver package: oem0.inf",
            "Driver package exported successfully.",
            "Exporting driver package: oem1.inf",
            "Driver package exported successfully.",
            "",
            "Total driver packages:  2",
            "Exported driver packages:  2",
        ];
        Assert.Equal(2, PnpUtilParser.CountExportLines(lines));
        Assert.True(PnpUtilParser.IsExportLine("Exporting driver package: oem7.inf"));
        Assert.False(PnpUtilParser.IsExportLine("Driver package exported successfully."));
        var (total, exported) = PnpUtilParser.ParseExportSummary(string.Join("\r\n", lines));
        Assert.Equal(2, total);
        Assert.Equal(2, exported);
        Assert.Equal((null, null), PnpUtilParser.ParseExportSummary(string.Empty));
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(259, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(1641, true, true)]
    [InlineData(5, false, false)]
    [InlineData(1168, false, false)]
    [InlineData(12345, false, false)]
    public void Exit_codes_map_to_success_and_reboot_flags(int code, bool success, bool reboot)
    {
        var (ok, rb, text) = PnpUtilParser.InterpretExitCode(code);
        Assert.Equal(success, ok);
        Assert.Equal(reboot, rb);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}

public class WuaResultCodesTests
{
    [Theory]
    [InlineData(0, "Not started")]
    [InlineData(1, "In progress")]
    [InlineData(2, "Succeeded")]
    [InlineData(3, "Succeeded with errors")]
    [InlineData(4, "Failed")]
    [InlineData(5, "Aborted")]
    public void Result_codes_have_text(int code, string expected)
    {
        Assert.Equal(expected, WuaResultCodes.Describe(code));
    }

    [Fact]
    public void Unknown_result_code_is_described_with_its_number()
    {
        Assert.Contains("42", WuaResultCodes.Describe(42));
        Assert.True(WuaResultCodes.IsSuccess(2));
        Assert.True(WuaResultCodes.IsSuccess(3));
        Assert.False(WuaResultCodes.IsSuccess(4));
    }

    [Fact]
    public void Known_hresults_get_an_explanation_and_unknown_ones_the_hex()
    {
        var known = WuaResultCodes.DescribeHResult(unchecked((int)0x80240022));
        Assert.StartsWith("0x80240022", known);
        Assert.Equal(unchecked((int)0x80240032), WuaResultCodes.InvalidCriteria);
        Assert.Contains("criteria", WuaResultCodes.DescribeHResult(WuaResultCodes.InvalidCriteria));
        Assert.Contains("failed", known, StringComparison.OrdinalIgnoreCase);
        Assert.True(WuaResultCodes.IsKnown(unchecked((int)0x80070005)));
        Assert.Equal("0x8024ABCD", WuaResultCodes.DescribeHResult(unchecked((int)0x8024ABCD)));
        Assert.False(WuaResultCodes.IsKnown(unchecked((int)0x8024ABCD)));
    }

    [Fact]
    public void Summary_only_includes_the_hresult_when_the_operation_failed()
    {
        Assert.Equal("Succeeded", WuaResultCodes.Summarize(2, unchecked((int)0x80240022)));
        Assert.Contains("0x80240022", WuaResultCodes.Summarize(4, unchecked((int)0x80240022)));
        Assert.Equal("Failed", WuaResultCodes.Summarize(4, 0));
    }

    [Fact]
    public void Service_id_is_the_public_windows_update_catalog()
    {
        Assert.Equal("7971f918-a847-4430-9279-4a52d1efe18d", WuaResultCodes.WindowsUpdateServiceId);
    }
}

public class DemoDriverServiceTests
{
    [Fact]
    public async Task Demo_scan_is_sorted_with_problems_first_and_has_variety()
    {
        var svc = new DemoDriverService();
        var scan = await svc.ScanInstalledAsync(null, CancellationToken.None);
        Assert.True(scan.Drivers.Count >= 20);
        Assert.True(scan.Drivers[0].HasProblem);
        Assert.False(scan.Drivers[^1].IsPresent);
        Assert.Contains(scan.Drivers, d => d.IsInbox);
        Assert.Contains(scan.Drivers, d => !d.IsInbox);
        Assert.Contains(scan.Drivers, d => !d.IsSigned);
        Assert.Contains(scan.Drivers, DriverRules.IsOld);
        Assert.Equal(scan.Drivers.Count, scan.Drivers.Select(d => d.DeviceId.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public async Task Demo_updates_links_and_store_are_populated()
    {
        var svc = new DemoDriverService();
        var check = await svc.CheckForUpdatesAsync(null, CancellationToken.None);
        Assert.Equal(2, check.Updates.Count);
        Assert.All(check.Updates, u => Assert.Equal(DriverUpdateSource.WindowsUpdate, u.Source));
        Assert.All(check.Updates, u => Assert.False(string.IsNullOrWhiteSpace(u.Provider)));
        Assert.NotEmpty(check.VendorLinks);
        var store = await svc.GetDriverStoreAsync(CancellationToken.None);
        Assert.True(store.Packages.Count >= 10);
        Assert.All(store.Packages, p => Assert.True(DriverInfoBuilder.IsOemInf(p.PublishedName)));
    }

    [Fact]
    public async Task Demo_install_and_rollback_report_reboot_when_appropriate()
    {
        var svc = new DemoDriverService();
        var check = await svc.CheckForUpdatesAsync(null, CancellationToken.None);
        var download = await svc.DownloadAsync(check.Updates, null, CancellationToken.None);
        Assert.True(download.Success);
        var downloaded = await ((IDriverUpdateStateProvider)svc).GetDownloadedUpdateIdsAsync(check.Updates.Select(u => u.Id).Append("not-an-update"), CancellationToken.None);
        Assert.Equal(check.Updates.Count, downloaded.Count);
        Assert.DoesNotContain("not-an-update", downloaded);
        var install = await svc.InstallAsync(check.Updates, null, CancellationToken.None);
        Assert.True(install.Success);
        Assert.True(install.RebootRequired);
        Assert.Equal(2, install.Results.Count);

        var inbox = DemoDriverService.SampleDrivers().First(d => d.IsInbox);
        Assert.False((await svc.RollbackAsync(inbox.DeviceId, CancellationToken.None)).Success);
        var third = DemoDriverService.SampleDrivers().First(DriverRules.CanRollBack);
        var rollback = await svc.RollbackAsync(third.DeviceId, CancellationToken.None);
        Assert.True(rollback.Success);
        Assert.True(rollback.RebootRequired);
        Assert.False((await svc.RollbackAsync("nope", CancellationToken.None)).Success);
    }

    [Fact]
    public async Task Demo_download_reports_progress_and_rejects_empty_selection()
    {
        var svc = new DemoDriverService();
        var updates = (await svc.CheckForUpdatesAsync(null, CancellationToken.None)).Updates;
        var seen = new List<ProgressUpdate>();
        var progress = new SynchronousProgress(seen.Add);
        var result = await svc.DownloadAsync(updates, progress, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains(seen, p => p.Percent == 100);
        Assert.False((await svc.DownloadAsync([], null, CancellationToken.None)).Success);
    }

    private sealed class SynchronousProgress(Action<ProgressUpdate> handler) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value) => handler(value);
    }
}
