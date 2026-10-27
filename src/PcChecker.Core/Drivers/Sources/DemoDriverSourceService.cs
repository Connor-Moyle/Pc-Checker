using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers.Platform;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>
/// Demo driver sources for the sample gaming desktop: Windows Update (the demo driver service's two updates plus an
/// older GeForce driver), NVIDIA's Game Ready or Studio release for the RTX 4070 through the real parser, Intel's
/// Wi-Fi and Bluetooth pages, Intel's assistant installable with winget, MSI Center for the MSI B650 board, AMD
/// Chipset Software one release behind, and the board maker's page for Realtek audio and Ethernet.
/// Nothing touches the network or the machine.
/// </summary>
public sealed class DemoDriverSourceService : IDriverSourceService
{
    private readonly IDriverService _drivers;
    private readonly ISettingsService? _settings;
    private readonly IPcPlatformService _platform;

    public DemoDriverSourceService(IDriverService drivers, ISettingsService? settings = null, IPcPlatformService? platform = null)
    {
        _drivers = drivers;
        _settings = settings;
        _platform = platform ?? new DemoPcPlatformService(settings);
    }

    /// <summary>The older GeForce driver Windows Update offers in the demo (566.14 in NVIDIA's numbering).</summary>
    public static DriverUpdateInfo DemoNvidiaWindowsUpdate { get; } =
        new("wu-nvidia-display", "NVIDIA - Display - 32.0.15.6614",
            DemoData.GpuName + " · NVIDIA · Display\nNVIDIA display driver update released in November 2024.",
            "32.0.15.6614", new DateTime(2024, 11, 19), 812_000_000, DriverUpdateSource.WindowsUpdate, @"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043",
            null, null, false, false, false)
        {
            Provider = "NVIDIA",
        };

    public async Task<DriverSourcesResult> SearchAsync(IReadOnlyList<DriverInfo> installed, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installed);
        if (installed.Count == 0)
        {
            installed = (await _drivers.ScanInstalledAsync(null, cancellationToken).ConfigureAwait(false)).Drivers;
        }

        var settings = (_settings?.Current.DriverSources ?? new DriverSourceSettings()).Clone();
        var facts = SmbiosNormalizer.ToDriverFacts(SmbiosNormalizer.Normalize(DemoPcPlatformService.Facts), DemoPcPlatformService.Facts);
        var context = new DriverSourceContext(installed, facts, settings);
        IReadOnlyList<OemToolInstall> msiCenter = OemToolCatalog.Tools.Where(t => t.Id == "msi-center")
            .Select(t => new OemToolInstall(t, null, "9426MICRO-STARINTERNATION.MSICenter_kzh8wxbdkxb8p!App")).ToList();
        Task<IReadOnlyList<OemToolInstall>> Detect(IReadOnlyList<OemTool> tools, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OemToolInstall>>(msiCenter.Where(a => tools.Any(t => t.Id == a.Tool.Id)).ToList());
        Task<bool> Winget(CancellationToken ct) => Task.FromResult(true);
        Task<PcPlatformReport> Report(bool online, CancellationToken ct) => _platform.CheckAsync(online, false, null, ct);

        IReadOnlyList<IDriverUpdateProvider> providers =
        [
            new DemoWindowsUpdateProvider(_drivers),
            new NvidiaDriverProvider(DemoNvidiaFetchAsync, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance),
            new AmdDriverProvider((_, _) => Task.FromResult(string.Empty), () => [], Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance),
            new IntelDriverProvider(Detect, Winget),
            new PcMakerToolProvider(Detect, Winget),
            new ChipsetDriverProvider(Report),
            new DeviceMakerProvider(Report),
            new MakerCatalogProvider(Report),
        ];
        return await DriverSourceSearch.RunAsync(providers, context, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Answers like NVIDIA's lookup does for the RTX 4070 (Game Ready 572.83 or Studio 572.60).</summary>
    private static async Task<string> DemoNvidiaFetchAsync(string url, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 700).ConfigureAwait(false);
        bool studio = Uri.UnescapeDataString(url).Contains("\"isCRD\":\"1\"", StringComparison.Ordinal);
        var (name, version, date, file) = studio
            ? ("NVIDIA Studio Driver", "572.60", "2025-02-27", "572.60-desktop-win10-win11-64bit-international-nsd-dch-whql.exe")
            : ("GeForce Game Ready Driver", "572.83", "2025-03-18", "572.83-desktop-win10-win11-64bit-international-dch-whql.exe");
        return "{\"criteria\":{\"overallState\":\"true\",\"IsSupported\":{\"state\":\"true\",\"DeviceID\":\"2786\",\"Vendor\":\"10DE\",\"SystemIsLaptopFlag\":0}},"
            + $"\"DriverAttributes\":{{\"Name\":\"{name}\",\"Version\":\"{version}\",\"ReleaseDateTime\":\"{date}T00:00:00+0000\",\"IsCRD\":{(studio ? 1 : 0)},\"IsWHQL\":1,"
            + "\"DetailsURL\":\"https://www.nvidia.com/en-us/geforce/drivers/\","
            + $"\"DownloadURLAdmin\":\"https://us.download.nvidia.com/Windows/{version}/{file}\"}}}}";
    }

    public async Task<OperationResult<VendorPackageInfo>> InspectPackageAsync(DriverOffer offer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        await DemoData.DelayAsync(cancellationToken, 100, 300).ConfigureAwait(false);
        var settings = _settings?.Current.DriverSources ?? new DriverSourceSettings();
        if (VendorPackageInstaller.Refusal(offer, settings) is { } refusal)
        {
            return OperationResult<VendorPackageInfo>.Fail(refusal);
        }

        long size = offer.Source == DriverSourceKind.Nvidia ? 1_037_190_000 : offer.Id.StartsWith("amd-chipset:", StringComparison.Ordinal) ? 74_400_000 : 2_900_000;
        return OperationResult<VendorPackageInfo>.Ok(new VendorPackageInfo(offer.Url!, VendorDownloadRules.InstallerFileName(offer.Url)!, size,
            VendorDownloadRules.ExpectedSignerText(offer.Source)));
    }

    public async Task<OperationResult> InstallPackageAsync(DriverOffer offer, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var inspect = await InspectPackageAsync(offer, cancellationToken).ConfigureAwait(false);
        if (!inspect.Success || inspect.Value is null)
        {
            return OperationResult.Fail(inspect.Message);
        }

        long total = inspect.Value.SizeBytes ?? 0;
        for (int step = 0; step <= 10; step++)
        {
            progress?.Report(new ProgressUpdate("Downloading " + inspect.Value.FileName, step * 10, ByteSize.Format(total * step / 10) + " of " + ByteSize.Format(total)));
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ProgressUpdate("Checking the signature", null, "Signed by " + inspect.Value.ExpectedSigner));
        await DemoData.DelayAsync(cancellationToken, 300, 500).ConfigureAwait(false);
        progress?.Report(new ProgressUpdate("Installing the driver", null, "Restore point created (demo)."));
        await DemoData.DelayAsync(cancellationToken, 600, 1000).ConfigureAwait(false);
        return OperationResult.Ok($"{offer.Title} {offer.Version} installed (demo). Signed by {inspect.Value.ExpectedSigner}.".Replace("  ", " ", StringComparison.Ordinal));
    }

    public OperationResult OpenTool(DriverOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return offer.Action == DriverOfferAction.OpenTool ? OperationResult.Ok($"Opened {offer.Title} (demo).") : OperationResult.Fail("There is nothing to open for this item.");
    }

    public Task<bool> IsWingetAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public async Task<OperationResult> InstallToolAsync(DriverOffer offer, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (offer.Action != DriverOfferAction.InstallTool || !OemToolCatalog.IsValidWingetId(offer.WingetId))
        {
            return OperationResult.Fail("This item cannot be installed with winget.");
        }

        progress?.Report(new ProgressUpdate("Installing " + offer.Title + " with winget", null, OemToolCatalog.WingetCommandText(offer.WingetId!)));
        await DemoData.DelayAsync(cancellationToken, 800, 1200).ConfigureAwait(false);
        return OperationResult.Ok($"{offer.Title} was installed (demo).");
    }

    /// <summary>Windows Update in the demo: the demo driver service's updates plus an older GeForce driver.</summary>
    private sealed class DemoWindowsUpdateProvider : IDriverUpdateProvider
    {
        private readonly IDriverService _drivers;

        public DemoWindowsUpdateProvider(IDriverService drivers)
        {
            _drivers = drivers;
        }

        public DriverSourceKind Source => DriverSourceKind.WindowsUpdate;

        public string Name => "Windows Update";

        public async Task<DriverProviderResult> FindAsync(DriverSourceContext context, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
        {
            var result = await _drivers.CheckForUpdatesAsync(progress, cancellationToken).ConfigureAwait(false);
            var updates = result.Updates.Append(DemoNvidiaWindowsUpdate).ToList();
            return new DriverProviderResult(WindowsUpdateDriverProvider.BuildOffers(updates, context.InstalledDrivers), result.Warnings,
                updates.Count.ToString(CultureInfo.InvariantCulture) + " update(s)", true) { VendorLinks = result.VendorLinks };
        }
    }
}
