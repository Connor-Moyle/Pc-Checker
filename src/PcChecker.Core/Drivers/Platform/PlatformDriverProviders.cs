using PcChecker.Core.Common;
using PcChecker.Core.Drivers.Sources;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>Gets the platform report for a provider: (ask the makers online, token) → report.</summary>
public delegate Task<PcPlatformReport> PlatformReportSource(bool online, CancellationToken cancellationToken);

/// <summary>
/// The chipset driver as a device in the update list: AMD Chipset Software compared with AMD's page (downloadable
/// from AMD's server through the usual signed-package flow), or Intel's chipset and Management Engine pages.
/// </summary>
public sealed class ChipsetDriverProvider : IDriverUpdateProvider
{
    public const string AmdKey = "chipset:amd";
    public const string IntelKey = "chipset:intel";

    private readonly PlatformReportSource _report;

    public ChipsetDriverProvider(PlatformReportSource report)
    {
        _report = report;
    }

    public DriverSourceKind Source => DriverSourceKind.Chipset;

    public string Name => "Chipset";

    public async Task<DriverProviderResult> FindAsync(DriverSourceContext context, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var report = await _report(true, cancellationToken).ConfigureAwait(false);
        return report.Chipset is { } chipset
            ? Build(chipset, context.InstalledDrivers, context.Settings)
            : DriverProviderResult.Skipped("Chipset maker not recognised");
    }

    /// <summary>The offers and status for a chipset status. Pure.</summary>
    public static DriverProviderResult Build(ChipsetStatus chipset, IReadOnlyList<DriverInfo> installed, DriverSourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(chipset);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(settings);
        if (chipset.Platform == ChipsetPlatform.Amd)
        {
            var device = new DriverDeviceRef(AmdKey, "AMD chipset driver", "System", chipset.InstalledVersion, null)
            {
                InstalledVendorVersion = chipset.InstalledVersion,
                InstalledVendorScheme = chipset.InstalledVersion is null ? DriverVersionScheme.None : DriverVersionScheme.Vendor,
            };
            var offer = AmdOffer(chipset, settings);
            IReadOnlyList<string> warnings = chipset.CheckedOnline || chipset.Message.Length == 0 ? [] : [chipset.Message];
            var status = chipset.LatestVersion is { } v ? "AMD " + v : "Not checked";
            return new DriverProviderResult([new DeviceOffer(device, offer)], warnings, status, chipset.CheckedOnline || !settings.Chipset);
        }

        var offers = new List<DeviceOffer>();
        var intel = new DriverDeviceRef(IntelKey, "Intel chipset (INF utility)", "System", null, null);
        offers.Add(new DeviceOffer(intel, PageOffer("intel-chipset", "Intel", chipset.Page, chipset.Message)));
        var me = installed.FirstOrDefault(d => d.IsPresent && d.DeviceName.Contains("Management Engine", StringComparison.OrdinalIgnoreCase));
        if (me is not null && chipset.MoreLinks.FirstOrDefault() is { } maker && maker.Url != IntelDriverCatalog.SupportAssistantPage)
        {
            offers.Add(new DeviceOffer(DriverDeviceMapper.ToDeviceRef(me),
                PageOffer("intel-me", "PC maker", maker,
                    maker.Note + (me.Version is null ? string.Empty : $" You have {me.Version}."))));
        }

        return new DriverProviderResult(offers, [], "Intel: links (Intel publishes no version list)", true);
    }

    /// <summary>The AMD Chipset Software offer: AMD's installer when it can be downloaded, else AMD's page.</summary>
    public static DriverOffer AmdOffer(ChipsetStatus chipset, DriverSourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(chipset);
        ArgumentNullException.ThrowIfNull(settings);
        if (chipset.LatestVersion is not { } version)
        {
            return PageOffer("amd-chipset-page", "AMD", chipset.Page, chipset.Message);
        }

        bool download = settings.VendorDownloads == VendorDownloadMode.AskEachTime
            && VendorDownloadRules.IsAllowed(chipset.DownloadUrl, VendorDownloadRules.AllowedHosts(DriverSourceKind.Amd))
            && VendorDownloadRules.InstallerFileName(chipset.DownloadUrl) is not null;
        return new DriverOffer("amd-chipset:" + version, DriverSourceKind.Amd, chipset.Name, version, DriverVersionScheme.Vendor, chipset.LatestDate,
            download ? DriverOfferAction.VendorPackage : DriverOfferAction.OpenPage)
        {
            Url = download ? chipset.DownloadUrl : chipset.Page.Url,
            DetailsUrl = chipset.Page.Url,
            FallbackPageUrl = chipset.Page.Url,
            ReferrerUrl = chipset.ReferrerUrl,
            Note = "Power plans, processor scheduling and other drivers for your AMD board, from AMD's chipset page. " + chipset.Message,
            SourceName = "AMD",
        };
    }

    internal static DriverOffer PageOffer(string id, string source, PlatformLink page, string note) =>
        new(id, DriverSourceKind.Chipset, page.Title, null, DriverVersionScheme.None, null, DriverOfferAction.OpenPage)
        {
            Url = page.Url,
            FallbackPageUrl = page.Url,
            Note = note,
            SourceName = source,
        };
}

/// <summary>Network, audio and Bluetooth devices linked to the right maker's page (see <see cref="DeviceMakerLinks"/>).</summary>
public sealed class DeviceMakerProvider : IDriverUpdateProvider
{
    private readonly PlatformReportSource _report;

    public DeviceMakerProvider(PlatformReportSource report)
    {
        _report = report;
    }

    public DriverSourceKind Source => DriverSourceKind.DeviceMakers;

    public string Name => "Network, audio and Bluetooth";

    public async Task<DriverProviderResult> FindAsync(DriverSourceContext context, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Only the support page is needed, which is read locally.
        var report = await _report(false, cancellationToken).ConfigureAwait(false);
        return Build(context.InstalledDrivers, report);
    }

    /// <summary>One link per network, audio or Bluetooth device that is not left to the Intel provider. Pure.</summary>
    public static DriverProviderResult Build(IReadOnlyList<DriverInfo> installed, PcPlatformReport report)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(report);
        string? maker = report.Info.SupportMaker is PcMaker.Unknown or PcMaker.Other ? null : SmbiosNormalizer.MakerName(report.Info.SupportMaker);
        var offers = new List<DeviceOffer>();
        foreach (var driver in installed)
        {
            if (DriverDeviceMapper.IntelProductFor(driver) != IntelProduct.None)
            {
                continue;
            }

            if (DeviceMakerLinks.OfferFor(driver, report.SupportPage, maker) is { } offer)
            {
                offers.Add(new DeviceOffer(DriverDeviceMapper.ToDeviceRef(driver), offer));
            }
        }

        return offers.Count == 0
            ? DriverProviderResult.Skipped("No network, audio or Bluetooth device to link")
            : new DriverProviderResult(offers, [], $"{offers.Count} device(s): links to the right maker", true);
    }
}

/// <summary>
/// The PC maker's public catalog (Lenovo): each catalog driver is matched to the installed device by hardware id
/// and compared with the installed version. Install them with the maker's tool or from the support page.
/// </summary>
public sealed class MakerCatalogProvider : IDriverUpdateProvider
{
    private readonly PlatformReportSource _report;

    public MakerCatalogProvider(PlatformReportSource report)
    {
        _report = report;
    }

    public DriverSourceKind Source => DriverSourceKind.MakerCatalog;

    public string Name => "PC maker catalog";

    public async Task<DriverProviderResult> FindAsync(DriverSourceContext context, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var report = await _report(true, cancellationToken).ConfigureAwait(false);
        return Build(context.InstalledDrivers, report);
    }

    /// <summary>The catalog offers for installed devices. Pure.</summary>
    public static DriverProviderResult Build(IReadOnlyList<DriverInfo> installed, PcPlatformReport report)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(report);
        if (report.CatalogEntries.Count == 0)
        {
            var maker = report.Info.SupportMaker is PcMaker.Unknown or PcMaker.Other ? "Your PC maker" : SmbiosNormalizer.MakerName(report.Info.SupportMaker);
            return DriverProviderResult.Skipped(report.Info.SystemMaker == PcMaker.Lenovo ? "No Lenovo catalog for this model" : $"{maker} publishes no catalog PC Checker can read");
        }

        var makerName = SmbiosNormalizer.MakerName(report.Info.SystemMaker);
        var page = report.SupportPage?.Url;
        var offers = new List<DeviceOffer>();
        foreach (var entry in report.CatalogEntries.Where(e => !e.IsBios && e.DriverVersion is not null && e.HardwareIds.Count > 0))
        {
            foreach (var driver in installed.Where(d => d.IsPresent && Matches(d, entry.HardwareIds)))
            {
                offers.Add(new DeviceOffer(DriverDeviceMapper.ToDeviceRef(driver),
                    new DriverOffer($"catalog:{driver.DeviceId}:{entry.DriverVersion}", DriverSourceKind.MakerCatalog, entry.Title, entry.DriverVersion,
                        DriverVersionScheme.Windows, entry.ReleaseDate, DriverOfferAction.OpenPage)
                    {
                        Url = page ?? entry.ReadmeUrl,
                        DetailsUrl = entry.ReadmeUrl,
                        FallbackPageUrl = page ?? entry.ReadmeUrl,
                        Note = $"From {makerName}'s catalog for your model. Install it with {makerName}'s update tool or from the support page.",
                        SourceName = makerName,
                    }));
            }
        }

        var status = offers.Count == 0
            ? $"{report.CatalogEntries.Count} package(s) read; none matched a device here"
            : $"{offers.Count} driver(s) matched";
        return new DriverProviderResult(offers, [], status, true);
    }

    /// <summary>
    /// True when one of the device's ids is a catalog id, or extends it at a field boundary
    /// ("PCI\VEN_8086&amp;DEV_51F0&amp;SUBSYS_00908086&amp;REV_01" matches "PCI\VEN_8086&amp;DEV_51F0&amp;SUBSYS_00908086").
    /// </summary>
    public static bool Matches(DriverInfo driver, IReadOnlyList<string> catalogIds)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(catalogIds);
        var ids = driver.HardwareIds.Append(driver.DeviceId).Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        return catalogIds.Any(c => ids.Any(i => string.Equals(i, c, StringComparison.OrdinalIgnoreCase)
            || i.StartsWith(c + "&", StringComparison.OrdinalIgnoreCase)
            || i.StartsWith(c + "\\", StringComparison.OrdinalIgnoreCase)));
    }
}
