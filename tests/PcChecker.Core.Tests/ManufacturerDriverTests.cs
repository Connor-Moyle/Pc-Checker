using System.Net;
using PcChecker.Core.Drivers;
using PcChecker.Core.Drivers.Platform;
using PcChecker.Core.Drivers.Sources;
using Xunit;
using static PcChecker.Core.Tests.ManufacturerDriverSamples;

namespace PcChecker.Core.Tests;

public class SmbiosNormalizerTests
{
    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", PcMaker.Asus)]
    [InlineData("ASUSTeK Computer Inc.", PcMaker.Asus)]
    [InlineData("Micro-Star International Co., Ltd.", PcMaker.Msi)]
    [InlineData("Micro-Star International Co.,Ltd", PcMaker.Msi)]
    [InlineData("MSI", PcMaker.Msi)]
    [InlineData("Gigabyte Technology Co., Ltd.", PcMaker.Gigabyte)]
    [InlineData("AORUS", PcMaker.Gigabyte)]
    [InlineData("ASRock", PcMaker.AsRock)]
    [InlineData("BIOSTAR Group", PcMaker.Biostar)]
    [InlineData("NZXT, Inc.", PcMaker.Nzxt)]
    [InlineData("Dell Inc.", PcMaker.Dell)]
    [InlineData("Alienware", PcMaker.Dell)]
    [InlineData("HP", PcMaker.Hp)]
    [InlineData("Hewlett-Packard", PcMaker.Hp)]
    [InlineData("LENOVO", PcMaker.Lenovo)]
    [InlineData("Acer", PcMaker.Acer)]
    [InlineData("Microsoft Corporation", PcMaker.Microsoft)]
    [InlineData("Framework", PcMaker.Framework)]
    [InlineData("System manufacturer", PcMaker.Unknown)]
    [InlineData("To Be Filled By O.E.M.", PcMaker.Unknown)]
    [InlineData("  ", PcMaker.Unknown)]
    [InlineData("Contoso Computers Ltd.", PcMaker.Other)]
    public void Maker_names_in_their_many_spellings_are_recognised(string raw, PcMaker expected) =>
        Assert.Equal(expected, SmbiosNormalizer.MakerOf(raw));

    [Fact]
    public void Self_built_msi_desktop_uses_the_board_and_finds_the_amd_socket()
    {
        var info = SmbiosNormalizer.Normalize(DemoPcPlatformService.Facts);

        Assert.True(info.IsSelfBuilt);
        Assert.Null(info.SystemModel); // "MS-7D78" is the board code, not a model
        Assert.Equal(PcMaker.Msi, info.SupportMaker);
        Assert.Equal("PRO B650-P WIFI", info.BoardProduct);
        Assert.Equal("1.0", info.BoardRevision);
        Assert.Equal("B650", info.ChipsetModel);
        Assert.Equal("AM5", info.Socket);
        Assert.Equal(ChipsetPlatform.Amd, info.Chipset);
        Assert.False(info.IsLaptop);
        Assert.Null(info.SerialNumber);
        Assert.Equal("MSI PRO B650-P WIFI", info.DisplayName);

        var facts = SmbiosNormalizer.ToDriverFacts(info, DemoPcPlatformService.Facts);
        Assert.Equal("msi", OemToolCatalog.Resolve(facts)!.VendorKey);
        Assert.True(OemToolCatalog.Resolve(facts)!.IsBoard);
    }

    [Fact]
    public void Lenovo_laptop_gets_machine_type_friendly_name_and_laptop_flag()
    {
        var info = SmbiosNormalizer.Normalize(LenovoFacts);

        Assert.False(info.IsSelfBuilt);
        Assert.Equal(PcMaker.Lenovo, info.SupportMaker);
        Assert.Equal("21AH", info.MachineType);
        Assert.Equal("ThinkPad T14 Gen 3", info.FriendlyModel);
        Assert.True(info.IsLaptop);
        Assert.Equal(ChipsetPlatform.Intel, info.Chipset);
        Assert.Equal("PF3ABCDE", info.SerialNumber);
        Assert.Equal("Lenovo ThinkPad T14 Gen 3 (21AH)", info.DisplayName);
    }

    [Theory]
    [InlineData("ABC1234", "ABC1234")]
    [InlineData(" abc1234 ", "ABC1234")]
    [InlineData("ABC123", null)]
    [InlineData("ABC-1234", null)]
    [InlineData("0000000", null)]
    [InlineData(null, null)]
    public void Dell_service_tag_is_seven_letters_and_digits(string? serial, string? expected) =>
        Assert.Equal(expected, SmbiosNormalizer.DellServiceTag(serial));

    [Theory]
    [InlineData("MAG B650 TOMAHAWK WIFI", "B650")]
    [InlineData("X670E AORUS MASTER", "X670E")]
    [InlineData("B650M Pro RS", "B650")]
    [InlineData("ROG STRIX Z790-E GAMING WIFI", "Z790")]
    [InlineData("TUF GAMING B550-PLUS", "B550")]
    [InlineData("TRX40 Creator", "TRX40")]
    [InlineData("LNVNB161216", null)]
    public void Chipset_is_read_from_the_board_name(string board, string? expected) =>
        Assert.Equal(expected, SmbiosNormalizer.ChipsetModel(board));

    [Theory]
    [InlineData("1.0", "1.0")]
    [InlineData("Rev 1.1", "1.1")]
    [InlineData("x.x", null)]
    [InlineData("Default string", null)]
    public void Board_revision_ignores_placeholders(string raw, string? expected) =>
        Assert.Equal(expected, SmbiosNormalizer.BoardRevision(raw));

    internal static SmbiosFacts LenovoFacts { get; } = new()
    {
        SystemManufacturer = "LENOVO",
        SystemModel = "21AHCTO1WW",
        SystemFamily = "ThinkPad T14 Gen 3",
        ProductVersion = "ThinkPad T14 Gen 3",
        ProductSerial = "PF3ABCDE",
        BoardManufacturer = "LENOVO",
        BoardProduct = "21AHCTO1WW",
        BiosManufacturer = "LENOVO",
        BiosVersion = "N3BET68W (1.46 )",
        BiosReleaseDate = new DateTime(2025, 11, 4),
        ChassisTypes = "10",
        PcSystemType = "2",
        CpuName = "12th Gen Intel(R) Core(TM) i7-1265U",
        CpuManufacturer = "GenuineIntel",
    };
}

public class SupportPageBuilderTests
{
    [Fact]
    public void Board_pages_have_the_exact_model_escaped()
    {
        var asus = SupportPageBuilder.ForBoard(PcMaker.Asus, "ROG STRIX B550-F GAMING (WI-FI)", null, ChipsetPlatform.Amd);
        Assert.Equal("https://www.asus.com/supportonly/ROG%20STRIX%20B550-F%20GAMING%20%28WI-FI%29/helpdesk_bios/", asus.Url);
        Assert.True(asus.Exact);

        var msi = SupportPageBuilder.ForBoard(PcMaker.Msi, "PRO B650-P WIFI (MS-7D78)", "1.0", ChipsetPlatform.Amd);
        Assert.Equal("https://www.msi.com/Motherboard/PRO-B650-P-WIFI/support", msi.Url);

        var asrock = SupportPageBuilder.ForBoard(PcMaker.AsRock, "B650M Pro RS", null, ChipsetPlatform.Amd);
        Assert.Equal("https://www.asrock.com/mb/AMD/B650M%20Pro%20RS/index.asp#BIOS", asrock.Url);

        var slash = SupportPageBuilder.ForBoard(PcMaker.AsRock, "X570 Phantom Gaming 4/ac", null, ChipsetPlatform.Amd);
        Assert.Contains("X570%20Phantom%20Gaming%204%2Fac/index.asp", slash.Url, StringComparison.Ordinal);
        Assert.All(new[] { asus, msi, asrock, slash }, l => Assert.StartsWith("https://", l.Url, StringComparison.Ordinal));
    }

    [Fact]
    public void Gigabyte_needs_the_revision_otherwise_it_searches()
    {
        var exact = SupportPageBuilder.ForBoard(PcMaker.Gigabyte, "B650 AORUS ELITE AX", "1.0", ChipsetPlatform.Amd);
        Assert.Equal("https://www.gigabyte.com/Motherboard/B650-AORUS-ELITE-AX-rev-10/support#support-dl-bios", exact.Url);
        Assert.True(exact.Exact);

        var search = SupportPageBuilder.ForBoard(PcMaker.Gigabyte, "B650 AORUS ELITE AX", "x.x", ChipsetPlatform.Amd);
        Assert.Equal("https://www.gigabyte.com/Search?kw=B650%20AORUS%20ELITE%20AX", search.Url);
        Assert.False(search.Exact);
        Assert.Contains("revision", search.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Brand_pcs_use_service_tag_machine_type_or_model()
    {
        var dell = SmbiosNormalizer.Normalize(new SmbiosFacts { SystemManufacturer = "Dell Inc.", SystemModel = "XPS 15 9530", BiosSerial = "ABC1234" });
        Assert.Equal("https://www.dell.com/support/home/en-us/product-support/servicetag/ABC1234/drivers", SupportPageBuilder.For(dell)!.Url);

        var dellNoTag = dell with { ServiceTag = null };
        Assert.Equal("https://www.dell.com/support/home/en-us/product-support/product/xps-15-9530/drivers", SupportPageBuilder.For(dellNoTag)!.Url);

        var lenovo = SmbiosNormalizer.Normalize(SmbiosNormalizerTests.LenovoFacts);
        Assert.Equal("https://pcsupport.lenovo.com/us/en/products/21AH", SupportPageBuilder.For(lenovo)!.Url);

        var hp = SmbiosNormalizer.Normalize(new SmbiosFacts { SystemManufacturer = "HP", SystemModel = "HP EliteBook 840 G9 Notebook PC" });
        Assert.Equal("https://support.hp.com/us-en/search?q=HP%20EliteBook%20840%20G9%20Notebook%20PC", SupportPageBuilder.For(hp)!.Url);

        var surface = SmbiosNormalizer.Normalize(new SmbiosFacts { SystemManufacturer = "Microsoft Corporation", SystemModel = "Surface Laptop 5" });
        Assert.Contains("support.microsoft.com", SupportPageBuilder.For(surface)!.Url, StringComparison.Ordinal);

        var framework = SmbiosNormalizer.Normalize(new SmbiosFacts { SystemManufacturer = "Framework", SystemModel = "Laptop 13 (AMD Ryzen 7040Series)" });
        Assert.Contains("frame.work", SupportPageBuilder.For(framework)!.Url, StringComparison.Ordinal);

        Assert.Null(SupportPageBuilder.For(SmbiosNormalizer.Normalize(new SmbiosFacts { SystemManufacturer = "System manufacturer", SystemModel = "System Product Name" })));
    }

    [Theory]
    [InlineData("PRO B650-P WIFI", true, "PRO-B650-P-WIFI")]
    [InlineData("B550M PRO-VDH/WIFI", true, "B550M-PRO-VDH-WIFI")]
    [InlineData("  MAG  B650 TOMAHAWK  ", true, "MAG-B650-TOMAHAWK")]
    [InlineData("B550 (WIFI)", true, "B550-WIFI")]
    [InlineData("XPS 15 9530", false, "xps-15-9530")]
    public void Slugs_turn_spaces_and_slashes_into_single_dashes(string model, bool keepCase, string expected) =>
        Assert.Equal(expected, SupportPageBuilder.Slug(model, keepCase));
}

public class AmdChipsetLookupTests
{
    private const string Page = "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am5/b650.html";

    [Fact]
    public void Saved_page_gives_version_date_and_amd_download()
    {
        var release = AmdChipsetLookup.ParsePage(AmdChipsetPage, Page);

        Assert.NotNull(release);
        Assert.Equal("8.09.28.1124", release!.Version);
        Assert.Equal(new DateTime(2026, 10, 21), release.ReleaseDate);
        Assert.Equal("https://drivers.amd.com/drivers/amd_chipset_software_8.09.28.1124.exe", release.DownloadUrl);
        Assert.True(VendorDownloadRules.IsAllowed(release.DownloadUrl, VendorDownloadRules.AllowedHosts(DriverSourceKind.Amd)));
    }

    [Fact]
    public void Page_without_the_link_still_gives_the_version_and_us_date()
    {
        var release = AmdChipsetLookup.ParsePage(AmdChipsetPageWithoutLink, Page);

        Assert.NotNull(release);
        Assert.Equal("7.01.08.129", release!.Version);
        Assert.Equal(new DateTime(2025, 1, 31), release.ReleaseDate);
        Assert.Null(release.DownloadUrl);
    }

    [Fact]
    public void Unrecognised_pages_give_nothing_and_the_newest_link_wins()
    {
        Assert.Null(AmdChipsetLookup.ParsePage("<html><body>Access denied</body></html>", Page));
        Assert.Null(AmdChipsetLookup.ParsePage(null, Page));

        var two = "<a href=\"https://drivers.amd.com/drivers/amd_chipset_software_6.10.17.152.exe\">old</a>"
            + "<a href=\"https://drivers.amd.com/drivers/amd_chipset_software_7.01.08.129.exe\">new</a>";
        Assert.Equal("7.01.08.129", AmdChipsetLookup.ParsePage(two, Page)!.Version);
    }

    [Fact]
    public void Socket_pages_and_installed_version()
    {
        Assert.EndsWith("/am5/b650.html", AmdChipsetLookup.PageFor("AM5"), StringComparison.Ordinal);
        Assert.EndsWith("/am4/b550.html", AmdChipsetLookup.PageFor("AM4"), StringComparison.Ordinal);
        Assert.EndsWith("/strx4/trx40.html", AmdChipsetLookup.PageFor("sTRX4"), StringComparison.Ordinal);
        Assert.Null(AmdChipsetLookup.PageFor("FP7"));

        var installed = AmdChipsetLookup.InstalledVersion(
        [
            ("AMD Chipset Software", "6.10.17.152"),
            ("AMD® Chipset Software", "7.01.08.129"),
            ("AMD Chipset Software Installer Helper", "9.0.0.0"),
            ("AMD Software", "24.12.1"),
        ]);
        Assert.Equal("7.01.08.129", installed);
        Assert.Null(AmdChipsetLookup.InstalledVersion([("Something else", "1.0")]));
    }
}

public class LenovoCatalogTests
{
    [Fact]
    public void Catalog_lists_https_lenovo_packages_and_picks_bios_first()
    {
        var packages = LenovoCatalog.ParseCatalog(LenovoCatalogXml);
        Assert.Equal(4, packages.Count); // the http:// entry is dropped
        Assert.All(packages, p => Assert.True(LenovoCatalog.IsLenovoUrl(p.Location)));

        var picked = LenovoCatalog.PickPackages(packages);
        Assert.Equal(3, picked.Count); // product firmware is not read
        Assert.Equal("BIOS UEFI", picked[0].Category);
        Assert.Equal("https://download.lenovo.com/catalog/21AH_Win11.xml", LenovoCatalog.CatalogUrl("21ah"));
        Assert.Null(LenovoCatalog.CatalogUrl("21A/"));
        Assert.Empty(LenovoCatalog.ParseCatalog("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><packages>&e;</packages>"));
    }

    [Fact]
    public void Bios_package_gives_version_date_ids_and_readme()
    {
        var bios = LenovoCatalog.ParsePackage(LenovoBiosPackage, "https://download.lenovo.com/pccbbs/mobiles/n3buj24w_2_.xml", "BIOS UEFI");

        Assert.NotNull(bios);
        Assert.True(bios!.IsBios);
        Assert.Equal("1.48", bios.Version);
        Assert.Equal(new DateTime(2026, 7, 6), bios.ReleaseDate);
        Assert.Equal("BIOS Update Utility - 10/11", bios.Title);
        Assert.Contains("N3BET", bios.BiosLevels);
        Assert.Contains("N3MET", bios.BiosLevels);
        Assert.Equal("https://download.lenovo.com/pccbbs/mobiles/n3buj24w.html", bios.ReadmeUrl);
        Assert.Same(bios, LenovoCatalog.BiosFor([bios], "N3BET"));
    }

    [Fact]
    public void Driver_package_gives_hardware_ids_and_driver_version()
    {
        var wlan = LenovoCatalog.ParsePackage(LenovoWlanPackage, "https://download.lenovo.com/pccbbs/mobiles/n3ciw20w_2_.xml", "Networking Wireless LAN");

        Assert.NotNull(wlan);
        Assert.False(wlan!.IsBios);
        Assert.Equal("24.30.1.1", wlan.DriverVersion);
        Assert.Equal(2, wlan.HardwareIds.Count);
        Assert.Equal(@"PCI\VEN_8086&DEV_51F0&SUBSYS_00908086", wlan.HardwareIds[0]);
        Assert.Empty(wlan.BiosLevels);
        Assert.Null(LenovoCatalog.ParsePackage("<packages />", "https://download.lenovo.com/x.xml", "Audio"));
    }

    [Theory]
    [InlineData("N3BET68W (1.46 )", "1.46", "N3BET")]
    [InlineData("R1MET59W(1.29 )", "1.29", "R1MET")]
    [InlineData("F23c", null, null)]
    public void Lenovo_bios_string_splits_into_version_and_id(string smbios, string? version, string? id)
    {
        Assert.Equal(version, BiosRules.LenovoVersion(smbios));
        Assert.Equal(id, BiosRules.LenovoBiosId(smbios));
    }
}

public class VendorVersionTextTests
{
    [Theory]
    [InlineData("7.01.08.129", "6.10.17.152", 1)]
    [InlineData("6.10.17.152", "6.10.17.152", 0)]
    [InlineData("8.09.28.1124", "8.09.28.733", 1)]
    [InlineData("7.01.08.129", "7.1.8.129", 0)]
    [InlineData("1.A0", "1.90", 1)]
    [InlineData("2.A0", "1.F0", 1)]
    [InlineData("F23c", "F23b", 1)]
    [InlineData("F23", "F23c", -1)]
    [InlineData("F10", "F9", 1)]
    [InlineData("1.40", "1.39", 1)]
    [InlineData("1.40", "1.40.0", 0)]
    [InlineData("2801", "3001", -1)]
    public void Vendor_formats_compare_the_way_makers_count(string a, string b, int expected) =>
        Assert.Equal(expected, VendorVersionText.Compare(a, b));

    [Theory]
    [InlineData("F23c", "2.A0")]
    [InlineData(null, "1.0")]
    [InlineData("", "1.0")]
    public void Different_formats_cannot_be_compared(string? a, string? b) =>
        Assert.Null(VendorVersionText.Compare(a, b));

    [Fact]
    public void Vendor_scheme_offers_are_classified_against_the_installed_vendor_version()
    {
        var device = new DriverDeviceRef("chipset:amd", "AMD chipset driver", "System", "6.10.17.152", null)
        {
            InstalledVendorVersion = "6.10.17.152",
            InstalledVendorScheme = DriverVersionScheme.Vendor,
        };
        Assert.Equal(DriverVersionComparison.Newer, DriverVersionMath.Classify("7.01.08.129", DriverVersionScheme.Vendor, device));
        Assert.Equal(DriverVersionComparison.Same, DriverVersionMath.Classify("6.10.17.152", DriverVersionScheme.Vendor, device));
        Assert.Equal(DriverVersionComparison.Unknown, DriverVersionMath.Classify("7.01.08.129", DriverVersionScheme.Vendor, device with { InstalledVendorVersion = null }));
    }
}

public class BiosRulesTests
{
    private static readonly DateTime Today = new(2026, 10, 27);

    [Fact]
    public void Age_counts_whole_years_and_old_means_over_two()
    {
        Assert.Equal(3, BiosRules.AgeYears(new DateTime(2023, 9, 14), Today));
        Assert.Equal(2, BiosRules.AgeYears(new DateTime(2024, 10, 27), Today));
        Assert.Equal(1, BiosRules.AgeYears(new DateTime(2024, 10, 28), Today));
        Assert.Null(BiosRules.AgeYears(null, Today));
        Assert.Null(BiosRules.AgeYears(new DateTime(2027, 1, 1), Today));

        Assert.True(BiosRules.IsOld(new DateTime(2024, 10, 26), Today));
        Assert.False(BiosRules.IsOld(new DateTime(2024, 10, 27), Today));
        Assert.False(BiosRules.IsOld(new DateTime(2025, 6, 1), Today));
        Assert.False(BiosRules.IsOld(null, Today));

        Assert.Contains("3 years old", BiosRules.AgeHint(new DateTime(2023, 9, 14), Today), StringComparison.Ordinal);
        Assert.Null(BiosRules.AgeHint(new DateTime(2025, 6, 1), Today));
    }

    [Fact]
    public void Installed_bios_is_compared_in_the_makers_own_form()
    {
        Assert.Equal("1.46", BiosRules.PublishedForm(PcMaker.Lenovo, "N3BET68W (1.46 )"));
        Assert.Equal("1.A0", BiosRules.PublishedForm(PcMaker.Msi, "1.A0"));
        Assert.Equal(DriverVersionComparison.Newer, BiosRules.Compare("1.46", "1.48"));
        Assert.Equal(DriverVersionComparison.Same, BiosRules.Compare("1.48", "1.48"));
        Assert.Equal(DriverVersionComparison.Unknown, BiosRules.Compare("F23c", "1.48"));
        Assert.Contains("never updates the BIOS", BiosRules.FlashWarning, StringComparison.Ordinal);
    }
}

public class DeviceMakerLinksTests
{
    private static DriverInfo Device(string name, string id, string cls, string provider = "Vendor", bool inbox = false) =>
        new(name, id, cls, provider, "1.0.0.0", new DateTime(2024, 1, 1), "oem1.inf", true, "Signer", inbox, [id], "OK", 0, null);

    [Fact]
    public void Network_audio_and_bluetooth_go_to_the_right_maker()
    {
        Assert.Equal(DeviceMakerKind.RealtekEthernet, DeviceMakerLinks.KindOf(Device("Realtek Gaming 2.5GbE Family Controller", @"PCI\VEN_10EC&DEV_8125", "Net")));
        Assert.Equal(DeviceMakerKind.IntelEthernet, DeviceMakerLinks.KindOf(Device("Intel(R) Ethernet Controller I226-V", @"PCI\VEN_8086&DEV_125C", "Net")));
        Assert.Equal(DeviceMakerKind.Killer, DeviceMakerLinks.KindOf(Device("Killer E3100G 2.5 Gigabit Ethernet Controller", @"PCI\VEN_10EC&DEV_3000", "Net")));
        Assert.Equal(DeviceMakerKind.WiFiFromPcMaker, DeviceMakerLinks.KindOf(Device("MediaTek Wi-Fi 6E MT7922 (RZ616) 160MHz Wireless LAN Card", @"PCI\VEN_14C3&DEV_0616", "Net")));
        Assert.Equal(DeviceMakerKind.WiFiFromPcMaker, DeviceMakerLinks.KindOf(Device("Qualcomm FastConnect 7800 Wi-Fi 7", @"PCI\VEN_17CB&DEV_1107", "Net")));
        Assert.Equal(DeviceMakerKind.BluetoothFromPcMaker, DeviceMakerLinks.KindOf(Device("Realtek Bluetooth Adapter", @"USB\VID_0BDA&PID_4853", "Bluetooth")));
        Assert.Equal(DeviceMakerKind.AudioFromPcMaker, DeviceMakerLinks.KindOf(Device("Realtek(R) Audio", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897", "MEDIA")));
        Assert.Equal(DeviceMakerKind.AudioFromPcMaker, DeviceMakerLinks.KindOf(Device("High Definition Audio Device", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897", "MEDIA", "Microsoft", inbox: true)));
        Assert.Equal(DeviceMakerKind.None, DeviceMakerLinks.KindOf(Device("NVIDIA High Definition Audio", @"HDAUDIO\FUNC_01&VEN_10DE&DEV_00A3", "MEDIA")));
        Assert.Equal(DeviceMakerKind.None, DeviceMakerLinks.KindOf(Device("Intel(R) Wi-Fi 6E AX211 160MHz", @"PCI\VEN_8086&DEV_51F0", "Net")));
        Assert.Equal(DeviceMakerKind.None, DeviceMakerLinks.KindOf(Device("Realtek Gaming 2.5GbE Family Controller", @"PCI\VEN_10EC&DEV_8125", "Net") with { IsPresent = false }));
    }

    [Fact]
    public void Onboard_audio_links_to_the_board_maker_never_a_generic_realtek_driver()
    {
        var audio = Device("Realtek(R) Audio", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_4080", "MEDIA");
        var page = new PlatformLink("MSI PRO B650-P WIFI support page", "https://www.msi.com/Motherboard/PRO-B650-P-WIFI/support", "", true);

        var offer = DeviceMakerLinks.OfferFor(audio, page, "MSI");

        Assert.NotNull(offer);
        Assert.Equal(page.Url, offer!.Url);
        Assert.Equal("MSI", offer.SourceName);
        Assert.Equal(DriverOfferAction.OpenPage, offer.Action);
        Assert.DoesNotContain("realtek.com", offer.Url!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("You have 1.0.0.0", offer.Note, StringComparison.Ordinal);
    }
}

public class PlatformLookupCacheTests
{
    [Fact]
    public async Task Answers_are_reused_for_twelve_hours_and_kept_for_offline_use()
    {
        var now = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
        var cache = new PlatformLookupCache(null, () => now);
        int calls = 0;
        Task<string> Fetch(CancellationToken ct)
        {
            calls++;
            return Task.FromResult("answer " + calls);
        }

        Task<string> Fail(CancellationToken ct) => throw new HttpRequestException("offline");

        Assert.Null(await cache.GetAsync("https://x/a", online: false, false, Fetch, CancellationToken.None));
        Assert.Equal("answer 1", (await cache.GetAsync("https://x/a", true, false, Fetch, CancellationToken.None))!.Text);

        now = now.AddHours(11);
        var reused = await cache.GetAsync("https://x/a", true, false, Fetch, CancellationToken.None);
        Assert.True(reused!.FromCache);
        Assert.Equal(1, calls);

        now = now.AddHours(2);
        var stale = await cache.GetAsync("https://x/a", true, false, Fail, CancellationToken.None);
        Assert.True(stale!.Stale);
        Assert.Equal("answer 1", stale.Text);

        var forced = await cache.GetAsync("https://x/a", true, forceRefresh: true, Fetch, CancellationToken.None);
        Assert.Equal("answer 2", forced!.Text);
        await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetAsync("https://x/new", true, false, Fail, CancellationToken.None));
    }

    [Fact]
    public async Task Saved_answers_survive_a_restart_in_the_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pcchecker-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new PlatformLookupCache(folder).GetAsync("https://x/a", true, false, _ => Task.FromResult("saved"), CancellationToken.None);
            var again = await new PlatformLookupCache(folder).GetAsync("https://x/a", false, false, _ => throw new InvalidOperationException(), CancellationToken.None);
            Assert.Equal("saved", again!.Text);
            Assert.DoesNotContain(Directory.GetFiles(folder), f => f.Contains("x", StringComparison.Ordinal) && Path.GetFileName(f).Contains('/'));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }
}

public class PcPlatformCheckerTests
{
    private static PcPlatformChecker Checker(Func<string, string> answer, List<string>? asked = null) =>
        new(new PlatformLookupCache(), (url, referrer, hosts, ct) =>
        {
            asked?.Add(url + "|" + referrer);
            Assert.True(VendorDownloadRules.IsAllowed(url, hosts), url);
            return Task.FromResult(answer(url));
        }, () => new DateTime(2026, 10, 27));

    [Fact]
    public async Task Lenovo_laptop_gets_the_catalog_bios_and_driver_versions()
    {
        var asked = new List<string>();
        var checker = Checker(url => url switch
        {
            _ when url.EndsWith("21AH_Win11.xml", StringComparison.Ordinal) => LenovoCatalogXml,
            _ when url.Contains("n3buj24w", StringComparison.Ordinal) => LenovoBiosPackage,
            _ when url.Contains("n3ciw20w", StringComparison.Ordinal) => LenovoWlanPackage,
            _ => throw new HttpRequestException("Not found", null, HttpStatusCode.NotFound),
        }, asked);

        var report = await checker.BuildAsync(SmbiosNormalizerTests.LenovoFacts, null, new DriverSourceSettings(), online: true, false, null, CancellationToken.None);

        Assert.Equal("1.46", report.Bios.InstalledVersion);
        Assert.Equal("1.48", report.Bios.LatestVersion);
        Assert.Equal(DriverVersionComparison.Newer, report.Bios.Comparison);
        Assert.Equal("Lenovo's catalog", report.Bios.LatestSource);
        Assert.False(report.Bios.IsOld);
        Assert.Equal(ChipsetPlatform.Intel, report.Chipset!.Platform);
        Assert.Contains(report.Notes, n => n.Contains("could not be read", StringComparison.Ordinal)); // the audio package was missing
        Assert.DoesNotContain(asked, a => a.Contains("insecure", StringComparison.Ordinal));

        var wifi = new DriverInfo("Intel(R) Wi-Fi 6E AX211 160MHz", @"PCI\VEN_8086&DEV_51F0&SUBSYS_00908086&REV_01\4&1", "Net", "Intel", "23.70.1.3",
            new DateTime(2024, 9, 18), "oem9.inf", true, "Intel", false, [@"PCI\VEN_8086&DEV_51F0&SUBSYS_00908086&REV_01", @"PCI\VEN_8086&DEV_51F0&SUBSYS_00908086"], "OK", 0, null);
        var result = MakerCatalogProvider.Build([wifi], report);
        var merged = DriverOfferMerger.Merge(result.Offers);
        var offer = Assert.Single(Assert.Single(merged).Offers);
        Assert.Equal("24.30.1.1", offer.Version);
        Assert.Equal(DriverVersionComparison.Newer, offer.Comparison);
        Assert.Equal("Lenovo", offer.SourceName);
        Assert.Equal("https://pcsupport.lenovo.com/us/en/products/21AH", offer.Url);
    }

    [Fact]
    public async Task Lenovo_model_without_a_catalog_says_so_and_offline_asks_nothing()
    {
        var checker = Checker(_ => throw new HttpRequestException("Not found", null, HttpStatusCode.NotFound));
        var report = await checker.BuildAsync(SmbiosNormalizerTests.LenovoFacts, null, new DriverSourceSettings(), true, false, null, CancellationToken.None);
        Assert.Contains(report.Notes, n => n.Contains("no public catalog", StringComparison.Ordinal));
        Assert.Null(report.Bios.LatestVersion);

        var asked = new List<string>();
        var offline = await Checker(_ => LenovoCatalogXml, asked).BuildAsync(SmbiosNormalizerTests.LenovoFacts, null, new DriverSourceSettings(), online: false, false, null, CancellationToken.None);
        Assert.Empty(asked);
        Assert.False(offline.CheckedOnline);
        Assert.Equal("1.46", offline.Bios.InstalledVersion);
    }

    [Fact]
    public async Task Amd_desktop_compares_the_chipset_with_amds_page_and_sends_the_referrer()
    {
        var asked = new List<string>();
        var checker = Checker(_ => AmdChipsetPage, asked);
        var report = await checker.BuildAsync(DemoPcPlatformService.Facts, "6.10.17.152", new DriverSourceSettings(), true, false, null, CancellationToken.None);

        var chipset = report.Chipset!;
        Assert.Equal(DriverVersionComparison.Newer, chipset.Comparison);
        Assert.Equal("8.09.28.1124", chipset.LatestVersion);
        Assert.True(chipset.CheckedOnline);
        Assert.Equal("https://www.amd.com/en/support/downloads/drivers.html/chipsets/am5/b650.html|" + AmdChipsetLookup.Referrer, Assert.Single(asked));
        Assert.True(report.Bios.IsOld);
        Assert.Equal(3, report.Bios.AgeYears);
        Assert.Equal("https://www.msi.com/Motherboard/PRO-B650-P-WIFI/support", report.SupportPage!.Url);
        Assert.Contains(report.Notes, n => n.Contains("MSI does not publish a BIOS list", StringComparison.Ordinal));

        var offers = ChipsetDriverProvider.Build(chipset, [], new DriverSourceSettings());
        var group = Assert.Single(DriverOfferMerger.Merge(offers.Offers));
        Assert.True(group.HasNewer);
        Assert.Equal(DriverOfferAction.VendorPackage, group.Offers[0].Action);
        Assert.Equal(DriverSourceKind.Amd, group.Offers[0].Source);
        Assert.Equal("amd_chipset_software_8.09.28.1124.exe", VendorDownloadRules.InstallerFileName(group.Offers[0].Url));
        Assert.Equal(DriverOfferAction.OpenPage, ChipsetDriverProvider.AmdOffer(chipset, new DriverSourceSettings { VendorDownloads = VendorDownloadMode.Never }).Action);
    }

    [Fact]
    public async Task Chipset_switched_off_or_unreachable_is_said_plainly()
    {
        var off = await Checker(_ => throw new InvalidOperationException("must not ask")).BuildAsync(DemoPcPlatformService.Facts, "6.10.17.152",
            new DriverSourceSettings { Chipset = false }, true, false, null, CancellationToken.None);
        Assert.Contains("off under Update sources", off.Chipset!.Message, StringComparison.Ordinal);
        Assert.Equal("6.10.17.152", off.Chipset.InstalledVersion);

        var down = await Checker(_ => throw new HttpRequestException("No such host")).BuildAsync(DemoPcPlatformService.Facts, null,
            new DriverSourceSettings(), true, false, null, CancellationToken.None);
        Assert.Contains("could not be reached", down.Chipset!.Message, StringComparison.Ordinal);
        Assert.Contains("not found in Apps", down.Chipset.Message, StringComparison.Ordinal);
        Assert.Equal(DriverOfferAction.OpenPage, ChipsetDriverProvider.Build(down.Chipset, [], new DriverSourceSettings()).Offers[0].Offer.Action);
    }

    [Fact]
    public async Task Demo_search_adds_chipset_board_maker_and_realtek_links()
    {
        var drivers = new DemoDriverService();
        var service = new DemoDriverSourceService(drivers);
        var result = await service.SearchAsync([], null, CancellationToken.None);

        var chipset = result.Devices.Single(d => d.Device.Key == ChipsetDriverProvider.AmdKey);
        Assert.True(chipset.HasNewer);
        Assert.Equal(DemoPcPlatformService.LatestChipset, chipset.Recommended!.Version);
        Assert.Equal(DriverOfferAction.VendorPackage, chipset.Recommended.Action);
        Assert.True((await service.InspectPackageAsync(chipset.Recommended, CancellationToken.None)).Success);

        var ethernet = result.Devices.Single(d => d.Device.Name.StartsWith("Realtek Gaming 2.5GbE", StringComparison.Ordinal));
        Assert.Contains(ethernet.Offers, o => o.Url == DeviceMakerLinks.RealtekEthernetPage);
        var audio = result.Devices.Single(d => d.Device.Name == "Realtek(R) Audio");
        Assert.Contains(audio.Offers, o => o.SourceName == "MSI" && o.Url!.StartsWith("https://www.msi.com/Motherboard/PRO-B650-P-WIFI", StringComparison.Ordinal));
        Assert.Contains(result.Devices, d => d.Offers.Any(o => o.Source == DriverSourceKind.Intel && o.Url == IntelDriverCatalog.WiFiPage));
        Assert.Contains(result.Sources, s => s.Source == DriverSourceKind.MakerCatalog && s.Message.Contains("MSI", StringComparison.Ordinal));
    }
}
