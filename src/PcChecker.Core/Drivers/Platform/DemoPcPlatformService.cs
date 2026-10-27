using PcChecker.Core.Common;
using PcChecker.Core.Drivers.Sources;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>
/// The demo PC for the "Your PC" card and the chipset check: a self-built desktop on an MSI PRO B650-P WIFI board
/// with a 2023 BIOS, a Ryzen 7 7800X3D and AMD Chipset Software one release behind AMD's page. The AMD answer is a
/// saved copy of the page's download entry run through the real parser. Nothing touches the network or the machine.
/// </summary>
public sealed class DemoPcPlatformService : PcPlatformServiceBase
{
    /// <summary>The chipset software the demo PC has installed.</summary>
    public const string InstalledChipset = "6.10.17.152";

    /// <summary>The newest chipset software on the demo AMD page.</summary>
    public const string LatestChipset = "7.01.08.129";

    private readonly ISettingsService? _settings;
    private readonly PcPlatformChecker _checker;

    public DemoPcPlatformService(ISettingsService? settings = null)
    {
        _settings = settings;
        _checker = new PcPlatformChecker(new PlatformLookupCache(), FetchAsync);
    }

    /// <summary>The demo PC's SMBIOS values.</summary>
    public static SmbiosFacts Facts { get; } = new()
    {
        SystemManufacturer = "Micro-Star International Co., Ltd.",
        SystemModel = "MS-7D78",
        SystemFamily = "To be filled by O.E.M.",
        ProductSerial = "To be filled by O.E.M.",
        BoardManufacturer = "Micro-Star International Co., Ltd.",
        BoardProduct = "PRO B650-P WIFI (MS-7D78)",
        BoardVersion = "1.0",
        BiosManufacturer = "American Megatrends International, LLC.",
        BiosVersion = "1.A0",
        BiosReleaseDate = new DateTime(2023, 9, 14),
        BiosSerial = "To be filled by O.E.M.",
        ChassisTypes = "3",
        PcSystemType = "1",
        CpuName = "AMD Ryzen 7 7800X3D 8-Core Processor",
        CpuManufacturer = "AuthenticAMD",
    };

    protected override DriverSourceSettings? CurrentSettings() => _settings?.Current.DriverSources;

    protected override async Task<PcPlatformReport> BuildAsync(DriverSourceSettings settings, bool online, bool forceRefresh, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 100, 300).ConfigureAwait(false);
        // The demo always "asks" AMD so the card shows a comparison straight away.
        return await _checker.BuildAsync(Facts, InstalledChipset, settings, online: true, forceRefresh, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Answers like AMD's AM5 chipset page does, in the same HTML shape.</summary>
    private static async Task<string> FetchAsync(string url, string? referrer, IReadOnlyList<string> hosts, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 200, 500).ConfigureAwait(false);
        if (!url.Contains("amd.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("Not available in the demo.");
        }

        return """
            <article class="container-fluid driver-download-details"><div class="row">
              <div class="col-12"><h4>AMD Chipset Drivers</h4></div>
              <div class="col-6 col-lg"><strong>Revision Number</strong><p>7.01.08.129</p></div>
              <div class="col-6 col-lg"><strong>File Size</strong><p>71 MB</p></div>
              <div class="col-6 col-lg"><strong>Release Date</strong><p> 2025-01-31 </p></div>
              <div class="col-6 col-lg"><div class="button btn btn-Primary-Black">
                <a href="https://drivers.amd.com/drivers/amd_chipset_software_7.01.08.129.exe" aria-label="Download AMD Chipset Drivers" role="button">Download</a>
              </div></div>
            </div></article>
            """;
    }
}
