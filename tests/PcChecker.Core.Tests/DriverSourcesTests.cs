using PcChecker.Core.Drivers;
using PcChecker.Core.Drivers.Sources;
using PcChecker.Core.Processes;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Recorded answers from the vendors (trimmed), used by the parser tests.</summary>
internal static class DriverSourceSamples
{
    /// <summary>NVIDIA getDispDrvrByDevid for 2786_10DE (RTX 4070), Game Ready, recorded 2026-10-27.</summary>
    public const string NvidiaGameReady = """
        {
         "MethodCalled": "<!-- methodcalled name=\"getDispDrvrByDevid\" date=\"2026-10-27T11:01:17+0000\" Array ResponseStatus=\"Success 200\" -->",
         "criteria": {
          "overallState": "true",
          "IsSupported": { "state": "true", "IsPrimary": 0, "DeviceID": "2786", "Vendor": "10DE", "SubSystemID": "null", "SubVendorID": "null", "SystemIsLaptopFlag": 0 },
          "IsDispDriverNewer": { "state": "true", "installedDispDriverVersion": null, "latestDispDriverVersion": "621.01" },
          "IsDeviceOsBlackListed": []
         },
         "DriverAttributes": {
          "ID": 282412,
          "Name": "GeForce Game Ready Driver",
          "ReleaseDateTime": "2026-10-21T00:00:00+0000",
          "Version": "621.01",
          "IsBeta": 0,
          "IsWHQL": 1,
          "IsDC": 1,
          "IsCRD": 0,
          "DetailsURL": "https://www.nvidia.com/en-us/drivers/details/282412/",
          "DownloadURL": "https://international-gfe.download.nvidia.com/Windows/621.01/621.01-desktop-win10-win11-64bit-international-dch-whql-g.exe",
          "DownloadURLAdmin": "https://us.download.nvidia.com/Windows/621.01/621.01-desktop-win10-win11-64bit-international-dch-whql.exe"
         },
         "DriverAttributesBeta": []
        }
        """;

    /// <summary>NVIDIA's answer for a device id it does not know (FFF0_10DE).</summary>
    public const string NvidiaUnknownDevice = """
        {
         "criteria": {
          "overallState": "false",
          "IsSupported": { "state": "true", "IsPrimary": 0, "DeviceID": "null", "Vendor": "null", "SystemIsLaptopFlag": 0 },
          "IsDispDriverNewer": { "state": "true", "installedDispDriverVersion": null, "latestDispDriverVersion": "null" },
          "IsDeviceOsBlackListed": []
         },
         "DriverAttributes": [],
         "DriverAttributesBeta": []
        }
        """;

    /// <summary>Studio branch for 2786_10DE.</summary>
    public const string NvidiaStudio = """
        {"criteria": {"overallState": "true", "IsSupported": {"SystemIsLaptopFlag": 0}}, "DriverAttributes": {"ID": 280101, "Name": "NVIDIA Studio Driver", "ReleaseDateTime": "2026-10-06T00:00:00+0000", "Version": "617.42", "IsBeta": 0, "IsWHQL": 0, "IsDC": 1, "IsCRD": 1, "DetailsURL": "https://www.nvidia.com/en-us/drivers/details/280101/", "DownloadURL": "https://international-gfe.download.nvidia.com/Windows/617.42/617.42-desktop-win10-win11-64bit-international-nsd-dch-whql-g.exe", "DownloadURLAdmin": "https://us.download.nvidia.com/Windows/617.42/617.42-desktop-win10-win11-64bit-international-nsd-dch-whql.exe"}}
        """;

    /// <summary>Notebook GPU (2860_10DE) with the laptop flag.</summary>
    public const string NvidiaNotebook = """
        {"criteria": {"overallState": "true", "IsSupported": {"SystemIsLaptopFlag": 1}}, "DriverAttributes": {"ID": 282413, "Name": "GeForce Game Ready Driver", "ReleaseDateTime": "2026-10-21T00:00:00+0000", "Version": "621.01", "IsBeta": 0, "IsWHQL": 1, "IsDC": 1, "IsCRD": 0, "DetailsURL": "https://www.nvidia.com/en-us/drivers/details/282413/", "DownloadURL": "https://international-gfe.download.nvidia.com/Windows/621.01/621.01-notebook-win10-win11-64bit-international-dch-whql-g.exe", "DownloadURLAdmin": "https://us.download.nvidia.com/Windows/621.01/621.01-notebook-win10-win11-64bit-international-dch-whql.exe"}}
        """;

    /// <summary>The part of AMD's driver page (amd.com/en/support/download/drivers.html) with the installer button, recorded 2026-10-27.</summary>
    public const string AmdDriversPage = """
        <div id="windows-linux-drivers" class="cmp-text">
            <h2>Windows® 10/11 Drivers</h2>
        <p><span class="subheadline">Auto-Detect and Install Driver Updates for AMD Radeon™ Series Graphics and Ryzen™ Chipsets</span></p>
        <p>For use with systems running Windows® 11 / Windows® 10 64-bit version 1809 and later. <a href="/en/resources/support-articles/faqs/GPU-131.html">Learn more</a></p>
        </div>
        <div class="button btn btn-Primary-Black aem-GridColumn aem-GridColumn--default--12">
        <a id="button-8dbca2589a" target="_blank" href="https://drivers.amd.com/drivers/installer/26.20/whql/amd-software-adrenalin-edition-26.10.1-minimalsetup-261018_web.exe" data-bs-yt-play-label="Play">
        	<span class="">Download Windows Drivers</span>
        </a>
        </div>
        <a href="https://drivers.amd.com/relnotes/command-line-installation.pdf">Command line installation</a>
        """;

    public static DriverInfo Gpu(string name, string id, string version, string vendorName = "NVIDIA") =>
        new(name, id + @"\4&1A2B3C4D&0&0008", "Display", vendorName, version, new DateTime(2025, 2, 10), "oem12.inf", true, vendorName, false, [id], "OK", 0, null);

    public static DriverInfo Device(string name, string id, string cls, string version, string provider = "Intel") =>
        new(name, id + @"\5&ABC&0&00E0", cls, provider, version, new DateTime(2024, 9, 18), "oem9.inf", true, provider, false, [id], "OK", 0, null);
}

public class DriverVersionMathTests
{
    [Theory]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("32.0.16.2101", "621.01")]
    [InlineData("31.0.15.5222", "552.22")]
    [InlineData("30.0.14.7212", "472.12")]
    [InlineData("32.0.15.6614", "566.14")]
    public void Nvidia_release_is_the_last_five_digits_of_the_windows_version(string windows, string expected) =>
        Assert.Equal(expected, DriverVersionMath.NvidiaReleaseFromWindows(windows));

    [Theory]
    [InlineData(null)]
    [InlineData("560.94")]
    [InlineData("32.0.15")]
    [InlineData("32.0.x.6094")]
    [InlineData("32.0.15.60945")]
    public void Nvidia_release_needs_a_four_part_windows_version(string? windows) =>
        Assert.Null(DriverVersionMath.NvidiaReleaseFromWindows(windows));

    [Fact]
    public void Nvidia_comparison_crosses_schemes()
    {
        var device = new DriverDeviceRef("gpu", "RTX 4070", "Display", "32.0.15.6094", null);
        Assert.Equal(DriverVersionComparison.Newer, DriverVersionMath.Classify("621.01", DriverVersionScheme.NvidiaRelease, device));
        Assert.Equal(DriverVersionComparison.Same, DriverVersionMath.Classify("560.94", DriverVersionScheme.NvidiaRelease, device));
        Assert.Equal(DriverVersionComparison.Older, DriverVersionMath.Classify("552.22", DriverVersionScheme.NvidiaRelease, device));
        Assert.True(DriverVersionMath.NvidiaKey("621.10") > DriverVersionMath.NvidiaKey("621.01"));
        Assert.Null(DriverVersionMath.NvidiaKey("32.0.15.6094"));
    }

    [Fact]
    public void Amd_windows_version_is_never_compared_with_an_adrenalin_version()
    {
        var unknown = new DriverDeviceRef("gpu", "Radeon RX 7800 XT", "Display", "32.0.21013.1000", null);
        Assert.Equal(DriverVersionComparison.Unknown, DriverVersionMath.Classify("24.12.1", DriverVersionScheme.Adrenalin, unknown));

        var known = unknown with { InstalledVendorVersion = "24.12.1", InstalledVendorScheme = DriverVersionScheme.Adrenalin };
        Assert.Equal(DriverVersionComparison.Same, DriverVersionMath.Classify("24.12.1", DriverVersionScheme.Adrenalin, known));
        Assert.Equal(DriverVersionComparison.Newer, DriverVersionMath.Classify("25.1.1", DriverVersionScheme.Adrenalin, known));
        Assert.Equal(DriverVersionComparison.Older, DriverVersionMath.Classify("24.10.1", DriverVersionScheme.Adrenalin, known));

        Assert.True(DriverVersionMath.IsAdrenalinVersion("24.12.1"));
        Assert.True(DriverVersionMath.IsAdrenalinVersion("26.10.1"));
        Assert.False(DriverVersionMath.IsAdrenalinVersion("32.0.21013.1000"));
        Assert.False(DriverVersionMath.IsAdrenalinVersion("24.13.1"));
    }

    [Fact]
    public void Windows_scheme_compares_four_part_versions()
    {
        var device = new DriverDeviceRef("wifi", "Intel Wi-Fi 6E AX211", "Net", "23.70.1.3", null);
        Assert.Equal(DriverVersionComparison.Newer, DriverVersionMath.Classify("23.110.0.5", DriverVersionScheme.Windows, device));
        Assert.Equal(DriverVersionComparison.Unknown, DriverVersionMath.Classify(null, DriverVersionScheme.Windows, device));
        Assert.Equal(DriverVersionComparison.Unknown, DriverVersionMath.Classify("1.0", DriverVersionScheme.None, device));
    }
}

public class DriverDeviceMapperTests
{
    [Fact]
    public void Pci_and_usb_ids_are_parsed()
    {
        var p = DriverDeviceMapper.Parse(@"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043&REV_A1\4&1A2B3C4D&0&0008");
        Assert.NotNull(p);
        Assert.Equal(("PCI", "10DE", "2786", "88AB1043"), (p!.Bus, p.VendorId, p.DeviceId, p.SubsystemId));
        Assert.Equal(("USB", "8087", "0033"), (DriverDeviceMapper.Parse(@"USB\VID_8087&PID_0033")!.Bus, DriverDeviceMapper.Parse(@"USB\VID_8087&PID_0033")!.VendorId, DriverDeviceMapper.Parse(@"USB\VID_8087&PID_0033")!.DeviceId));
        Assert.Null(DriverDeviceMapper.Parse(@"ACPI\MSFT0101\1"));
        Assert.Null(DriverDeviceMapper.Parse((string?)null));
    }

    [Fact]
    public void Nvidia_lookup_id_comes_from_the_pci_device_id()
    {
        Assert.Equal("2786_10DE", DriverDeviceMapper.NvidiaLookupId(DriverSourceSamples.Gpu("NVIDIA GeForce RTX 4070", @"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043", "32.0.15.6094")));
        Assert.Null(DriverDeviceMapper.NvidiaLookupId(DriverSourceSamples.Gpu("Radeon", @"PCI\VEN_1002&DEV_747E", "32.0.21013.1000", "AMD")));
    }

    [Theory]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", @"PCI\VEN_8086&DEV_56A0", "Display", IntelProduct.ArcAndIrisXeGraphics)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", @"PCI\VEN_8086&DEV_9A49", "Display", IntelProduct.ArcAndIrisXeGraphics)]
    [InlineData("Intel(R) UHD Graphics 770", @"PCI\VEN_8086&DEV_A780", "Display", IntelProduct.ArcAndIrisXeGraphics)]
    [InlineData("Intel(R) UHD Graphics 630", @"PCI\VEN_8086&DEV_3E92", "Display", IntelProduct.LegacyGraphics)]
    [InlineData("Intel(R) HD Graphics 520", @"PCI\VEN_8086&DEV_1916", "Display", IntelProduct.LegacyGraphics)]
    [InlineData("Intel(R) Wi-Fi 6E AX211 160MHz", @"PCI\VEN_8086&DEV_51F0", "Net", IntelProduct.WiFi)]
    [InlineData("Intel(R) Wireless Bluetooth(R)", @"USB\VID_8087&PID_0033", "Bluetooth", IntelProduct.Bluetooth)]
    [InlineData("Intel(R) Ethernet Controller I226-V", @"PCI\VEN_8086&DEV_125C", "Net", IntelProduct.None)]
    [InlineData("Realtek(R) Audio", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_4080", "MEDIA", IntelProduct.None)]
    public void Intel_devices_map_to_their_product_download(string name, string id, string cls, IntelProduct expected) =>
        Assert.Equal(expected, DriverDeviceMapper.IntelProductFor(DriverSourceSamples.Device(name, id, cls, "1.0.0.0")));

    [Theory]
    [InlineData("Mobile", null, true)]
    [InlineData("2", null, true)]
    [InlineData("Desktop", "Tower", false)]
    [InlineData("1", "3", false)]
    [InlineData(null, "10", true)]
    [InlineData("Desktop", "Convertible", true)]
    [InlineData(null, null, false)]
    public void Laptops_are_detected_from_pc_type_or_chassis(string? pcType, string? chassis, bool expected) =>
        Assert.Equal(expected, DriverDeviceMapper.IsLaptop(pcType, chassis));
}

public class VendorLookupParserTests
{
    [Fact]
    public void Nvidia_answer_is_parsed()
    {
        var r = NvidiaDriverLookup.Parse(DriverSourceSamples.NvidiaGameReady);
        Assert.NotNull(r);
        Assert.Equal("GeForce Game Ready Driver", r!.Name);
        Assert.Equal("621.01", r.Version);
        Assert.Equal(new DateTime(2026, 10, 21), r.ReleaseDate);
        Assert.Equal("https://us.download.nvidia.com/Windows/621.01/621.01-desktop-win10-win11-64bit-international-dch-whql.exe", r.DownloadUrl);
        Assert.False(r.IsStudio);
        Assert.False(r.IsNotebook);
        Assert.True(r.IsWhql);
    }

    [Fact]
    public void Nvidia_studio_and_notebook_answers_are_recognised()
    {
        var studio = NvidiaDriverLookup.Parse(DriverSourceSamples.NvidiaStudio);
        Assert.True(studio!.IsStudio);
        Assert.Equal("617.42", studio.Version);
        var notebook = NvidiaDriverLookup.Parse(DriverSourceSamples.NvidiaNotebook);
        Assert.True(notebook!.IsNotebook);
        Assert.Contains("notebook", notebook.DownloadUrl);
    }

    [Theory]
    [InlineData(DriverSourceSamples.NvidiaUnknownDevice)]
    [InlineData("")]
    [InlineData("<html>Service unavailable</html>")]
    [InlineData("{\"DriverAttributes\":{\"Version\":\"latest\",\"DownloadURLAdmin\":\"https://us.download.nvidia.com/x.exe\"}}")]
    [InlineData("[]")]
    public void Nvidia_unknown_devices_and_changed_answers_give_null(string json) =>
        Assert.Null(NvidiaDriverLookup.Parse(json));

    [Fact]
    public void Nvidia_lookup_url_carries_only_the_device_id_and_options()
    {
        var url = NvidiaDriverLookup.BuildUrl("2786_10de", isNotebook: true, NvidiaDriverBranch.Studio);
        Assert.StartsWith(NvidiaDriverLookup.ServiceUrl, url);
        var query = Uri.UnescapeDataString(url[NvidiaDriverLookup.ServiceUrl.Length..]);
        Assert.Contains("\"dIDa\":[\"2786_10DE\"]", query);
        Assert.Contains("\"iLp\":\"1\"", query);
        Assert.Contains("\"isCRD\":\"1\"", query);
        Assert.Contains("\"upCRD\":\"1\"", query);
        Assert.Contains("\"gcV\":\"\"", query);
        Assert.Contains("\"isCRD\":\"0\"", Uri.UnescapeDataString(NvidiaDriverLookup.BuildUrl("2786_10DE", false, NvidiaDriverBranch.GameReady)));
    }

    [Fact]
    public void Amd_drivers_page_gives_version_date_and_installer()
    {
        var r = AmdDriverLookup.ParseDriversPage(DriverSourceSamples.AmdDriversPage);
        Assert.NotNull(r);
        Assert.Equal("26.10.1", r!.Version);
        Assert.Equal(new DateTime(2026, 10, 18), r.ReleaseDate);
        Assert.Equal("https://drivers.amd.com/drivers/installer/26.20/whql/amd-software-adrenalin-edition-26.10.1-minimalsetup-261018_web.exe", r.DownloadUrl);
        Assert.EndsWith("RN-RAD-WIN-26-10-1.html", r.ReleaseNotesUrl);
        Assert.Null(AmdDriverLookup.ParseDriversPage("<html><a href=\"https://drivers.amd.com/relnotes/x.pdf\">x</a></html>"));
        Assert.Null(AmdDriverLookup.ParseBuildDate("261399"));
    }

    [Fact]
    public void Amd_installed_adrenalin_version_comes_from_the_matching_registry_entry()
    {
        var gpu = DriverSourceSamples.Gpu("AMD Radeon RX 7900 XTX", @"PCI\VEN_1002&DEV_744C&SUBSYS_0E3B1002", "32.0.12033.1030", "AMD");
        var entries = new List<(string?, string?)>
        {
            (@"pci\ven_10de&dev_2786", "24.1.1"),
            (@"pci\ven_1002&dev_744c", "24.12.1"),
            (@"pci\ven_1002&dev_164e", "garbage"),
        };
        Assert.Equal("24.12.1", AmdDriverLookup.InstalledAdrenalinVersion(entries, gpu));
        Assert.Null(AmdDriverLookup.InstalledAdrenalinVersion([(@"pci\ven_1002&dev_73bf", "24.12.1")], gpu));
    }
}

public class VendorDownloadRulesTests
{
    private static readonly IReadOnlyList<string> Nvidia = VendorDownloadRules.AllowedHosts(DriverSourceKind.Nvidia);
    private static readonly IReadOnlyList<string> Amd = VendorDownloadRules.AllowedHosts(DriverSourceKind.Amd);

    [Theory]
    [InlineData("https://us.download.nvidia.com/Windows/621.01/621.01-desktop.exe", true)]
    [InlineData("https://international-gfe.download.nvidia.com/Windows/621.01/x.exe", true)]
    [InlineData("https://download.nvidia.com/x.exe", true)]
    [InlineData("http://us.download.nvidia.com/x.exe", false)]
    [InlineData("https://us.download.nvidia.com.example.org/x.exe", false)]
    [InlineData("https://evildownload.nvidia.com/x.exe", false)]
    [InlineData("https://www.nvidia.com/x.exe", false)]
    [InlineData("https://user@us.download.nvidia.com/x.exe", false)]
    [InlineData("https://us.download.nvidia.com:8443/x.exe", false)]
    [InlineData("https://23.45.67.89/x.exe", false)]
    [InlineData("ftp://us.download.nvidia.com/x.exe", false)]
    [InlineData("not a url", false)]
    public void Only_https_downloads_from_the_vendors_servers_are_allowed(string url, bool allowed) =>
        Assert.Equal(allowed, VendorDownloadRules.IsAllowed(url, Nvidia));

    [Fact]
    public void Each_vendor_has_its_own_allow_list()
    {
        const string amdUrl = "https://drivers.amd.com/drivers/installer/26.20/whql/amd-software-adrenalin-edition-26.10.1-minimalsetup-261018_web.exe";
        Assert.True(VendorDownloadRules.IsAllowed(amdUrl, Amd));
        Assert.False(VendorDownloadRules.IsAllowed(amdUrl, Nvidia));
        Assert.Empty(VendorDownloadRules.AllowedHosts(DriverSourceKind.WindowsUpdate));
        Assert.False(VendorDownloadRules.IsAllowed(amdUrl, VendorDownloadRules.AllowedHosts(DriverSourceKind.PcMaker)));
    }

    [Theory]
    [InlineData("https://us.download.nvidia.com/Windows/621.01/621.01-desktop-win10-win11-64bit-international-dch-whql.exe", "621.01-desktop-win10-win11-64bit-international-dch-whql.exe")]
    [InlineData("https://drivers.amd.com/relnotes/command-line-installation.pdf", null)]
    [InlineData("https://us.download.nvidia.com/Windows/", null)]
    [InlineData("https://us.download.nvidia.com/a/.exe", null)]
    public void Installer_file_name_must_be_a_plain_exe(string url, string? expected) =>
        Assert.Equal(expected, VendorDownloadRules.InstallerFileName(url));

    [Theory]
    [InlineData(DriverSourceKind.Nvidia, "CN=NVIDIA Corporation, O=NVIDIA Corporation, L=Santa Clara, S=California, C=US", true)]
    [InlineData(DriverSourceKind.Nvidia, "CN=nvidia corporation, O=Other", true)]
    [InlineData(DriverSourceKind.Nvidia, "CN=NVIDIA Corporation Tools Ltd, O=NVIDIA Corporation Tools Ltd", false)]
    [InlineData(DriverSourceKind.Nvidia, "CN=Advanced Micro Devices, Inc.", false)]
    [InlineData(DriverSourceKind.Amd, "CN=\"Advanced Micro Devices, Inc.\", O=\"Advanced Micro Devices, Inc.\", L=Santa Clara, S=California, C=US", true)]
    [InlineData(DriverSourceKind.Amd, "CN=Advanced Micro Devices INC., O=Advanced Micro Devices INC.", true)]
    [InlineData(DriverSourceKind.Amd, "CN=Advanced Micro Devices Fan Club", false)]
    [InlineData(DriverSourceKind.Intel, "CN=Intel Corporation, O=Intel Corporation, L=Santa Clara", true)]
    [InlineData(DriverSourceKind.Intel, "CN=Intel Corporation Evil, O=Evil", false)]
    [InlineData(DriverSourceKind.PcMaker, "CN=Intel Corporation", false)]
    [InlineData(DriverSourceKind.Nvidia, "", false)]
    public void Signer_must_be_exactly_the_vendor(DriverSourceKind source, string subject, bool expected) =>
        Assert.Equal(expected, VendorDownloadRules.SignerMatches(source, subject));

    [Fact]
    public void Only_a_valid_signature_from_the_vendor_is_trusted()
    {
        const string nvidia = "CN=NVIDIA Corporation, O=NVIDIA Corporation";
        Assert.True(VendorDownloadRules.Evaluate(DriverSourceKind.Nvidia, SignatureRules.Success, nvidia).Trusted);
        Assert.False(VendorDownloadRules.Evaluate(DriverSourceKind.Nvidia, SignatureRules.TrustENoSignature, null).Trusted);
        Assert.Contains("changed after it was signed", VendorDownloadRules.Evaluate(DriverSourceKind.Nvidia, SignatureRules.TrustEBadDigest, nvidia).Message);
        Assert.Contains("revoked", VendorDownloadRules.Evaluate(DriverSourceKind.Nvidia, SignatureRules.CertERevoked, nvidia).Message);
        var wrong = VendorDownloadRules.Evaluate(DriverSourceKind.Amd, SignatureRules.Success, nvidia);
        Assert.False(wrong.Trusted);
        Assert.Contains("NVIDIA Corporation", wrong.Message);
    }

    [Fact]
    public void Installer_switches_are_only_nvidias_documented_ones()
    {
        Assert.Empty(VendorDownloadRules.InstallerArguments(DriverSourceKind.Nvidia, NvidiaInstallMode.Interactive));
        Assert.Equal(["-s", "-noreboot"], VendorDownloadRules.InstallerArguments(DriverSourceKind.Nvidia, NvidiaInstallMode.Silent));
        Assert.Equal(["-s", "-clean", "-noreboot"], VendorDownloadRules.InstallerArguments(DriverSourceKind.Nvidia, NvidiaInstallMode.SilentClean));
        Assert.Empty(VendorDownloadRules.InstallerArguments(DriverSourceKind.Amd, NvidiaInstallMode.SilentClean));
        Assert.Empty(VendorDownloadRules.InstallerArguments(DriverSourceKind.Intel, NvidiaInstallMode.Silent));
    }

    [Fact]
    public void Installer_exit_codes_are_read()
    {
        Assert.Equal((true, false), Take(VendorDownloadRules.InterpretExitCode(0, true)));
        Assert.Equal((true, true), Take(VendorDownloadRules.InterpretExitCode(3010, false)));
        Assert.Equal((false, false), Take(VendorDownloadRules.InterpretExitCode(1223, true)));
        Assert.Contains("cancelled", VendorDownloadRules.InterpretExitCode(1223, true).Text);

        static (bool, bool) Take((bool Success, bool Reboot, string Text) r) => (r.Success, r.Reboot);
    }
}

public class OemToolCatalogTests
{
    [Fact]
    public void Real_model_wins_and_placeholders_fall_back_to_the_board()
    {
        var dell = OemToolCatalog.Resolve(new DriverSystemFacts("Dell Inc.", "XPS 15 9530", "Dell Inc.", "0RH1JY", true));
        Assert.Equal(("dell", "XPS 15 9530", false), (dell!.VendorKey, dell.Model, dell.IsBoard));

        var diy = OemToolCatalog.Resolve(new DriverSystemFacts("System manufacturer", "System Product Name", "ASUSTeK COMPUTER INC.", "ROG STRIX Z790-E GAMING WIFI", false));
        Assert.Equal(("asus", "ROG STRIX Z790-E GAMING WIFI", true), (diy!.VendorKey, diy.Model, diy.IsBoard));

        var demo = OemToolCatalog.Resolve(new DriverSystemFacts("ASUS", "System Product Name", "ASUSTeK COMPUTER INC.", "ROG STRIX Z790-E GAMING WIFI", false));
        Assert.True(demo!.IsBoard);

        Assert.Equal("msi", OemToolCatalog.Resolve(new DriverSystemFacts("To Be Filled By O.E.M.", "To Be Filled By O.E.M.", "Micro-Star International Co., Ltd.", "MAG B650 TOMAHAWK WIFI (MS-7D75)", false))!.VendorKey);
        Assert.Null(OemToolCatalog.Resolve(new DriverSystemFacts("System manufacturer", "System Product Name", "ASRock", "B650M Pro RS", false)));
        Assert.Equal("hp", OemToolCatalog.VendorKey("HP"));
        Assert.Equal("hp", OemToolCatalog.VendorKey("Hewlett-Packard"));
        Assert.Null(OemToolCatalog.VendorKey("Hpe Something"));
    }

    [Fact]
    public void Support_pages_point_at_the_exact_model()
    {
        Assert.Equal("https://www.dell.com/support/home/en-us/product-support/product/xps-15-9530/drivers",
            OemToolCatalog.SupportUrl(new OemIdentity("dell", "Dell", "XPS 15 9530", false)));
        Assert.Contains("ThinkPad%20X1%20Carbon", OemToolCatalog.SupportUrl(new OemIdentity("lenovo", "Lenovo", "ThinkPad X1 Carbon Gen 11", false)));
        Assert.StartsWith("https://www.asus.com/", OemToolCatalog.SupportUrl(new OemIdentity("asus", "ASUS", "ROG STRIX Z790-E GAMING WIFI", true)));
        Assert.Equal("https://support.hp.com/us-en/drivers", OemToolCatalog.SupportUrl(new OemIdentity("hp", "HP", null, false)));
        Assert.All(OemToolCatalog.Tools.Select(t => t.Vendor).Distinct(), v =>
            Assert.StartsWith("https://", OemToolCatalog.SupportUrl(new OemIdentity(v, OemToolCatalog.VendorName(v), "Model 1", false))));
    }

    [Fact]
    public void Winget_command_is_built_from_separate_arguments()
    {
        Assert.Equal("winget install --id Dell.CommandUpdate --exact --source winget --accept-package-agreements --accept-source-agreements",
            OemToolCatalog.WingetCommandText("Dell.CommandUpdate"));
        Assert.Throws<ArgumentException>(() => OemToolCatalog.WingetInstallArguments("Dell.CommandUpdate & calc"));
        Assert.False(OemToolCatalog.IsValidWingetId("\"x\""));
        Assert.All(OemToolCatalog.Tools.Where(t => t.WingetId is not null), t => Assert.True(OemToolCatalog.IsValidWingetId(t.WingetId)));
        Assert.True(OemToolCatalog.InterpretWingetExitCode(0).Success);
        Assert.Contains("could not find", OemToolCatalog.InterpretWingetExitCode(unchecked((int)0x8A150014)).Text);
    }

    [Fact]
    public void Tools_only_start_from_protected_folders()
    {
        string[] roots = [@"C:\Program Files", @"C:\Program Files (x86)"];
        Assert.True(OemToolCatalog.IsInProtectedFolder(@"C:\Program Files\Dell\CommandUpdate\DellCommandUpdate.exe", roots));
        Assert.True(OemToolCatalog.IsInProtectedFolder(@"c:\program files (x86)\Lenovo\System Update\tvsu.exe", roots));
        Assert.False(OemToolCatalog.IsInProtectedFolder(@"C:\SWSetup\HPImageAssistant\HPImageAssistant.exe", roots));
        Assert.False(OemToolCatalog.IsInProtectedFolder(@"C:\Program Files\..\Users\Public\evil.exe", roots));
        Assert.False(OemToolCatalog.IsInProtectedFolder(@"C:\Program FilesEvil\x.exe", roots));
        Assert.False(OemToolCatalog.IsInProtectedFolder(@"\\server\share\x.exe", roots));
        Assert.Equal(@"C:\Program Files\Dell\CommandUpdate\DellCommandUpdate.exe", OemToolCatalog.ExecutableFromDisplayIcon("\"C:\\Program Files\\Dell\\CommandUpdate\\DellCommandUpdate.exe\",0"));
        Assert.Equal(@"C:\Program Files (x86)\HP\HP Support Framework\HPSF.exe", OemToolCatalog.ExecutableFromDisplayIcon(@"C:\Program Files (x86)\HP\HP Support Framework\HPSF.exe,0"));
        Assert.Null(OemToolCatalog.ExecutableFromDisplayIcon(@"C:\Program Files\Dell\icon.ico"));
    }

    [Fact]
    public void Installed_tools_are_found_by_store_package_path_or_uninstall_entry()
    {
        string[] roots = [@"C:\Program Files", @"C:\Program Files (x86)"];
        var tools = OemToolCatalog.Tools;
        var found = OemToolDetector.Resolve(
            tools,
            [
                new InstalledProgramHint("Dell Command | Update for Windows Universal", "\"C:\\Program Files\\Dell\\CommandUpdate\\DellCommandUpdate.exe\",0", null),
                new InstalledProgramHint("HP Image Assistant", @"C:\SWSetup\HPImageAssistant\HPImageAssistant.exe,0", null),
                new InstalledProgramHint("Intel® Driver & Support Assistant", null, null),
            ],
            [new StorePackageHint("B9ECED6F.ArmouryCrate", "B9ECED6F.ArmouryCrate_qmba6cd70vzyy", @"C:\Program Files\WindowsApps\x", ["App"])],
            roots,
            path => path.Contains("DellCommandUpdate.exe", StringComparison.Ordinal) || path.Contains("tvsu.exe", StringComparison.Ordinal));

        var byId = found.ToDictionary(f => f.Tool.Id);
        Assert.Equal(@"C:\Program Files\Dell\CommandUpdate\DellCommandUpdate.exe", byId["dell-command-update"].ExecutablePath);
        Assert.Equal("B9ECED6F.ArmouryCrate_qmba6cd70vzyy!App", byId["armoury-crate"].AppUserModelId);
        Assert.Null(byId["hp-image-assistant"].ExecutablePath); // installed, but outside Program Files: never started elevated
        Assert.True(byId.ContainsKey("intel-dsa"));
        Assert.True(byId.ContainsKey("lenovo-system-update")); // found by its program path alone
        Assert.False(byId.ContainsKey("msi-center"));
    }
}

public class DriverOfferMergerTests
{
    private static DriverOffer Offer(string id, DriverSourceKind source, string? version, DriverVersionScheme scheme, DateTime? date, DriverOfferAction action) =>
        new(id, source, id, version, scheme, date, action);

    [Fact]
    public void Offers_are_grouped_ranked_and_the_newest_marked()
    {
        var gpu = new DriverDeviceRef("gpu", "NVIDIA GeForce RTX 4070", "Display", "32.0.15.6094", new DateTime(2025, 2, 10))
        {
            InstalledVendorVersion = "560.94",
            InstalledVendorScheme = DriverVersionScheme.NvidiaRelease,
        };
        var wifi = new DriverDeviceRef("wifi", "Intel Wi-Fi 6E AX211", "Net", "23.70.1.3", null);
        var system = new DriverDeviceRef(DriverOfferMerger.SystemKey, "ASUS ROG STRIX", "System", null, null);
        var offers = new List<DeviceOffer>
        {
            new(system, Offer("support", DriverSourceKind.PcMaker, null, DriverVersionScheme.None, null, DriverOfferAction.OpenPage)),
            new(system, Offer("tool", DriverSourceKind.PcMaker, null, DriverVersionScheme.None, null, DriverOfferAction.OpenTool)),
            new(gpu, Offer("wu", DriverSourceKind.WindowsUpdate, "32.0.15.6614", DriverVersionScheme.Windows, new DateTime(2024, 11, 19), DriverOfferAction.WindowsUpdate)),
            new(gpu, Offer("nv", DriverSourceKind.Nvidia, "621.01", DriverVersionScheme.NvidiaRelease, new DateTime(2026, 10, 21), DriverOfferAction.VendorPackage)),
            new(wifi, Offer("intel", DriverSourceKind.Intel, null, DriverVersionScheme.None, null, DriverOfferAction.OpenPage)),
        };

        var merged = DriverOfferMerger.Merge(offers);
        Assert.Equal(["gpu", "wifi", DriverOfferMerger.SystemKey], merged.Select(m => m.Device.Key));

        var g = merged[0];
        Assert.True(g.HasNewer);
        Assert.Equal(["nv", "wu"], g.Offers.Select(o => o.Id));
        Assert.True(g.Offers[0].IsNewest);
        Assert.False(g.Offers[1].IsNewest);
        Assert.All(g.Offers, o => Assert.Equal(DriverVersionComparison.Newer, o.Comparison)); // 621.01 and 566.14 both beat 560.94
        Assert.Equal("nv", g.Recommended!.Id);

        Assert.Equal(DriverVersionComparison.Unknown, merged[1].Offers[0].Comparison);
        Assert.Equal(["tool", "support"], merged[2].Offers.Select(o => o.Id));
        Assert.Equal("tool", merged[2].Recommended!.Id);
    }

    [Fact]
    public void Same_version_prefers_windows_update_and_dates_rank_incomparable_versions()
    {
        var gpu = new DriverDeviceRef("gpu", "GPU", "Display", "32.0.15.6094", null)
        {
            InstalledVendorVersion = "560.94",
            InstalledVendorScheme = DriverVersionScheme.NvidiaRelease,
        };
        var tie = DriverOfferMerger.Rank(gpu,
        [
            Offer("nv", DriverSourceKind.Nvidia, "566.14", DriverVersionScheme.NvidiaRelease, new DateTime(2024, 11, 19), DriverOfferAction.VendorPackage),
            Offer("wu", DriverSourceKind.WindowsUpdate, "32.0.15.6614", DriverVersionScheme.Windows, new DateTime(2024, 11, 19), DriverOfferAction.WindowsUpdate),
        ]);
        Assert.Equal("wu", tie.Recommended!.Id);
        Assert.All(tie.Offers, o => Assert.True(o.IsNewest));

        var radeon = new DriverDeviceRef("amd", "Radeon", "Display", "32.0.12033.1030", null);
        var amd = DriverOfferMerger.Rank(radeon,
        [
            Offer("wu", DriverSourceKind.WindowsUpdate, "32.0.12011.1036", DriverVersionScheme.Windows, new DateTime(2024, 9, 1), DriverOfferAction.WindowsUpdate),
            Offer("amd", DriverSourceKind.Amd, "26.10.1", DriverVersionScheme.Adrenalin, new DateTime(2026, 10, 18), DriverOfferAction.VendorPackage),
        ]);
        Assert.Equal("amd", amd.Offers[0].Id);
        Assert.Equal(DriverVersionComparison.Unknown, DriverOfferMerger.Classify(amd.Offers[0], radeon));
    }

    [Fact]
    public void Windows_update_drivers_attach_to_their_device()
    {
        var installed = DemoDriverService.SampleDrivers();
        var update = new DriverUpdateInfo("u1", "Intel - System", null, "30.100.2417.47", null, 0, DriverUpdateSource.WindowsUpdate, @"PCI\VEN_8086&DEV_7A4C", null, null, false, false, false);
        Assert.Equal("Intel(R) Serial IO I2C Host Controller", DriverOfferMerger.FindDevice(update, installed)!.DeviceName);
        Assert.Null(DriverOfferMerger.FindDevice(update with { DeviceId = @"PCI\VEN_8086&DEV_7A4" }, installed));
        Assert.Null(DriverOfferMerger.FindDevice(update with { DeviceId = null }, installed));

        var offers = WindowsUpdateDriverProvider.BuildOffers([update, update with { Id = "u2", DeviceId = @"PCI\VEN_FFFF&DEV_0001", Description = "Mystery device · Vendor · Class" }], installed);
        Assert.Equal(2, offers.Count);
        Assert.Equal("wu:u2", offers[1].Device.Key[..5]);
        Assert.Equal("Mystery device", offers[1].Device.Name);
        var merged = DriverOfferMerger.Merge(offers);
        Assert.All(merged, m => Assert.True(m.HasNewer)); // Windows Update only offers applicable drivers
    }

    [Fact]
    public void Download_setting_turns_vendor_packages_into_pages()
    {
        var release = NvidiaDriverLookup.Parse(DriverSourceSamples.NvidiaGameReady)!;
        var ask = NvidiaDriverProvider.BuildOffer("2786_10DE", release, new DriverSourceSettings());
        Assert.Equal(DriverOfferAction.VendorPackage, ask.Action);
        Assert.Equal(release.DownloadUrl, ask.Url);
        Assert.Contains("Game Ready", ask.Note);
        var never = NvidiaDriverProvider.BuildOffer("2786_10DE", release, new DriverSourceSettings { VendorDownloads = VendorDownloadMode.Never });
        Assert.Equal(DriverOfferAction.OpenPage, never.Action);
        Assert.Equal(release.DetailsUrl, never.Url);
        var offSite = NvidiaDriverProvider.BuildOffer("2786_10DE", release with { DownloadUrl = "https://example.org/621.01.exe" }, new DriverSourceSettings());
        Assert.Equal(DriverOfferAction.OpenPage, offSite.Action);
    }
}

public class DriverSourceSearchTests
{
    private sealed class FakeProvider(DriverSourceKind source, Func<DriverProviderResult> result) : IDriverUpdateProvider
    {
        public DriverSourceKind Source => source;
        public string Name => source.ToString();
        public int Calls { get; private set; }

        public Task<DriverProviderResult> FindAsync(DriverSourceContext context, IProgress<Common.ProgressUpdate>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result());
        }
    }

    [Fact]
    public async Task Disabled_and_failing_sources_degrade_gracefully()
    {
        var device = new DriverDeviceRef("gpu", "GPU", "Display", "1.0.0.0", null);
        var ok = new FakeProvider(DriverSourceKind.Intel, () => new DriverProviderResult(
            [new DeviceOffer(device, new DriverOffer("i", DriverSourceKind.Intel, "Intel page", null, DriverVersionScheme.None, null, DriverOfferAction.OpenPage))], [], "ok", true));
        var broken = new FakeProvider(DriverSourceKind.Nvidia, () => throw new InvalidOperationException("endpoint changed"));
        var off = new FakeProvider(DriverSourceKind.Amd, () => throw new InvalidOperationException("must not run"));
        var settings = new DriverSourceSettings { Amd = false };
        var context = new DriverSourceContext([], DriverSystemFacts.Unknown, settings);

        var result = await DriverSourceSearch.RunAsync([ok, broken, off], context, null, CancellationToken.None);

        Assert.Equal(0, off.Calls);
        Assert.Single(result.Devices);
        Assert.Contains(result.Warnings, w => w.Contains("endpoint changed", StringComparison.Ordinal));
        Assert.Contains(result.Sources, s => s.Source == DriverSourceKind.Amd && !s.Enabled);
        Assert.Contains(result.Sources, s => s.Source == DriverSourceKind.Nvidia && !s.Succeeded);
    }

    [Fact]
    public async Task Demo_search_shows_several_sources_for_the_sample_pc()
    {
        var drivers = new DemoDriverService();
        var service = new DemoDriverSourceService(drivers);
        var result = await service.SearchAsync([], null, CancellationToken.None);

        var gpu = result.Devices.First(d => d.Device.Name == Common.DemoData.GpuName);
        Assert.Contains(gpu.Offers, o => o.Source == DriverSourceKind.Nvidia && o.Action == DriverOfferAction.VendorPackage && o.IsNewest);
        Assert.Contains(gpu.Offers, o => o.Source == DriverSourceKind.WindowsUpdate && !o.IsNewest);
        Assert.Contains(result.Devices, d => d.Offers.Any(o => o.Source == DriverSourceKind.Intel));
        var system = result.Devices.Single(d => d.Device.Key == DriverOfferMerger.SystemKey);
        Assert.Contains(system.Offers, o => o.Action == DriverOfferAction.OpenTool && o.Title == "MSI Center");
        Assert.Contains(system.Offers, o => o.Action == DriverOfferAction.InstallTool && o.WingetId == "Intel.IntelDriverAndSupportAssistant");
        Assert.Contains(result.Sources, s => s.Source == DriverSourceKind.Amd && s.Message.Contains("No AMD", StringComparison.Ordinal));

        var nvidia = gpu.Offers.First(o => o.Source == DriverSourceKind.Nvidia);
        var inspect = await service.InspectPackageAsync(nvidia, CancellationToken.None);
        Assert.True(inspect.Success);
        Assert.Equal("NVIDIA Corporation", inspect.Value!.ExpectedSigner);
        Assert.True((await service.InstallPackageAsync(nvidia, null, CancellationToken.None)).Success);
        Assert.False((await service.InstallPackageAsync(nvidia with { Url = "https://example.org/x.exe" }, null, CancellationToken.None)).Success);
    }
}
