using System.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>
/// Windows implementation of <see cref="IDriverSourceService"/>: Windows Update through <see cref="IDriverService"/>,
/// NVIDIA's driver service, AMD's driver page, Intel's product pages and the PC maker's tools. Vendor packages are
/// handled by <see cref="VendorPackageInstaller"/>; tools are started only from protected folders.
/// </summary>
public sealed class WindowsDriverSourceService : IDriverSourceService
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private readonly IDriverService _drivers;
    private readonly IHardwareInventoryService _hardware;
    private readonly ISettingsService _settings;
    private readonly ILogger<WindowsDriverSourceService> _logger;
    private readonly VendorPackageInstaller _installer;

    public WindowsDriverSourceService(IDriverService drivers, IHardwareInventoryService hardware, IChangeGuard changeGuard,
        ISettingsService settings, ILogger<WindowsDriverSourceService> logger)
    {
        _drivers = drivers;
        _hardware = hardware;
        _settings = settings;
        _logger = logger;
        _installer = new VendorPackageInstaller(changeGuard, logger);
    }

    public async Task<DriverSourcesResult> SearchAsync(IReadOnlyList<DriverInfo> installed, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var settings = (_settings.Current.DriverSources ?? new DriverSourceSettings()).Clone();
        if (installed.Count == 0)
        {
            progress?.Report(new ProgressUpdate("Reading installed drivers", 2));
            installed = (await _drivers.ScanInstalledAsync(null, cancellationToken).ConfigureAwait(false)).Drivers;
        }

        var facts = _hardware.Last is { } inventory
            ? DriverSourceSearch.FactsFrom(inventory)
            : await Task.Run(ReadSystemFacts, cancellationToken).ConfigureAwait(false);
        var detector = new OemToolDetector(_logger);
        var context = new DriverSourceContext(installed, facts, settings);
        var result = await DriverSourceSearch.RunAsync(CreateProviders(detector), context, progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Driver source search: {Devices} device(s), {Newer} with a newer driver; {Sources}.",
            result.Devices.Count, result.Devices.Count(d => d.HasNewer), string.Join("; ", result.Sources.Select(s => s.Name + ": " + s.Message)));
        return result;
    }

    /// <summary>The providers in display order.</summary>
    private IReadOnlyList<IDriverUpdateProvider> CreateProviders(OemToolDetector detector)
    {
        Task<string> Fetch(string url, CancellationToken ct) => VendorHttp.GetStringAsync(url, VendorDownloadRules.LookupHosts, ct);
        async Task<bool> Winget(CancellationToken ct) => await detector.FindWingetAsync(ct).ConfigureAwait(false) is not null;
        return
        [
            new WindowsUpdateDriverProvider(_drivers),
            new NvidiaDriverProvider(Fetch, _logger),
            new AmdDriverProvider(Fetch, ReadRadeonSoftwareVersions, _logger),
            new IntelDriverProvider(detector.DetectAsync, Winget),
            new PcMakerToolProvider(detector.DetectAsync, Winget),
        ];
    }

    public Task<OperationResult<VendorPackageInfo>> InspectPackageAsync(DriverOffer offer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return _installer.InspectAsync(offer, CurrentSettings(), cancellationToken);
    }

    public Task<OperationResult> InstallPackageAsync(DriverOffer offer, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Installing drivers is only available on Windows."));
        }

        return _installer.InstallAsync(offer, CurrentSettings(), progress, cancellationToken);
    }

    public OperationResult OpenTool(DriverOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var target = offer.Url;
        if (offer.Action != DriverOfferAction.OpenTool || string.IsNullOrWhiteSpace(target))
        {
            return OperationResult.Fail("There is nothing to open for this item.");
        }

        if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return ShellLauncher.Open(target);
        }

        if (target.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
        {
            var aumid = target[@"shell:AppsFolder\".Length..];
            return aumid.Length > 0 && aumid.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '!')
                ? ShellLauncher.Open(target)
                : OperationResult.Fail("The app id looks wrong, so it was not started.");
        }

        if (!OemToolCatalog.IsInProtectedFolder(target, OemToolDetector.ProtectedRoots()) || !File.Exists(target))
        {
            return OperationResult.Fail($"{offer.Title} is not in Program Files, so PC Checker (which runs as administrator) does not start it. Open it from the Start menu.");
        }

        return ShellLauncher.Open(target);
    }

    public async Task<bool> IsWingetAvailableAsync(CancellationToken cancellationToken) =>
        PlatformGuard.IsWindows && await new OemToolDetector(_logger).FindWingetAsync(cancellationToken).ConfigureAwait(false) is not null;

    public async Task<OperationResult> InstallToolAsync(DriverOffer offer, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("winget is only available on Windows.");
        }

        if (offer.Action != DriverOfferAction.InstallTool || !OemToolCatalog.IsValidWingetId(offer.WingetId))
        {
            return OperationResult.Fail("This item cannot be installed with winget.");
        }

        var winget = await new OemToolDetector(_logger).FindWingetAsync(cancellationToken).ConfigureAwait(false);
        if (winget is null)
        {
            return OperationResult.Fail("winget (App Installer) is not installed, so the tool was not installed. Get App Installer from the Microsoft Store, or use the maker's page.");
        }

        return await _installer.InstallWithWingetAsync(winget, offer, progress, cancellationToken).ConfigureAwait(false);
    }

    private DriverSourceSettings CurrentSettings() => (_settings.Current.DriverSources ?? new DriverSourceSettings()).Clone();

    /// <summary>MatchingDeviceId and RadeonSoftwareVersion of every display adapter driver key (AMD writes the Adrenalin version there).</summary>
    private IReadOnlyList<(string? MatchingDeviceId, string? RadeonSoftwareVersion)> ReadRadeonSoftwareVersions()
    {
        var entries = new List<(string?, string?)>();
        if (!OperatingSystem.IsWindows())
        {
            return entries;
        }

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClassKey, writable: false);
            if (root is null)
            {
                return entries;
            }

            foreach (var name in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)))
            {
                try
                {
                    using var key = root.OpenSubKey(name, writable: false);
                    var matching = key?.GetValue("MatchingDeviceId") as string;
                    var radeon = key?.GetValue("RadeonSoftwareVersion") as string;
                    if (matching is not null && radeon is not null)
                    {
                        entries.Add((matching, radeon));
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // Some adapter keys are locked; skip them.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Display class key could not be read.");
        }

        return entries;
    }

    /// <summary>Maker, model, board and laptop flag from WMI when no hardware scan has run yet.</summary>
    private DriverSystemFacts ReadSystemFacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return DriverSystemFacts.Unknown;
        }

        string? manufacturer = null, model = null, pcType = null, board = null, product = null, chassis = null;
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem"))
            {
                foreach (ManagementBaseObject o in searcher.Get())
                {
                    using (o)
                    {
                        manufacturer = o["Manufacturer"]?.ToString();
                        model = o["Model"]?.ToString();
                        pcType = o["PCSystemType"]?.ToString();
                    }
                }
            }

            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            {
                foreach (ManagementBaseObject o in searcher.Get())
                {
                    using (o)
                    {
                        board = o["Manufacturer"]?.ToString();
                        product = o["Product"]?.ToString();
                    }
                }
            }

            using (var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
            {
                foreach (ManagementBaseObject o in searcher.Get())
                {
                    using (o)
                    {
                        if (o["ChassisTypes"] is ushort[] types)
                        {
                            chassis = string.Join(",", types);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PC maker and model could not be read; PC maker tools are skipped.");
        }

        return new DriverSystemFacts(manufacturer, model, board, product, DriverDeviceMapper.IsLaptop(pcType, chassis));
    }
}
