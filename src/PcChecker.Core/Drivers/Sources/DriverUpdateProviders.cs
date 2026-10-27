using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>Windows Update's driver catalog through the existing <see cref="IDriverService"/> (Windows Update Agent).</summary>
public sealed class WindowsUpdateDriverProvider : IDriverUpdateProvider
{
    private readonly IDriverService _drivers;

    public WindowsUpdateDriverProvider(IDriverService drivers)
    {
        _drivers = drivers;
    }

    public DriverSourceKind Source => DriverSourceKind.WindowsUpdate;

    public string Name => "Windows Update";

    public async Task<DriverProviderResult> FindAsync(DriverSourceContext context, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = await _drivers.CheckForUpdatesAsync(progress, cancellationToken).ConfigureAwait(false);
        var offers = BuildOffers(result.Updates, context.InstalledDrivers);
        var status = result.Updates.Count == 0 ? "No driver updates" : $"{result.Updates.Count} update(s)";
        return new DriverProviderResult(offers, result.Warnings, status, true) { VendorLinks = result.VendorLinks };
    }

    /// <summary>One offer per Windows Update driver, attached to the installed device it is for when that can be found.</summary>
    public static IReadOnlyList<DeviceOffer> BuildOffers(IReadOnlyList<DriverUpdateInfo> updates, IReadOnlyList<DriverInfo> installed)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(installed);
        var offers = new List<DeviceOffer>();
        foreach (var update in updates.Where(u => !u.IsInstalled))
        {
            var driver = DriverOfferMerger.FindDevice(update, installed);
            var device = driver is not null
                ? DriverDeviceMapper.ToDeviceRef(driver)
                : new DriverDeviceRef("wu:" + update.Id, DeviceNameFrom(update), null, null, null);
            if (driver is not null && DriverDeviceMapper.Parse(driver) is { VendorId: DriverDeviceMapper.NvidiaVendorId } && DriverDeviceMapper.IsDisplayAdapter(driver))
            {
                device = device with { InstalledVendorVersion = DriverVersionMath.NvidiaReleaseFromWindows(driver.Version), InstalledVendorScheme = DriverVersionScheme.NvidiaRelease };
            }

            offers.Add(new DeviceOffer(device, new DriverOffer("wu:" + update.Id, DriverSourceKind.WindowsUpdate, update.Title, update.Version,
                DriverVersionScheme.Windows, update.ReleaseDate, DriverOfferAction.WindowsUpdate)
            {
                SizeBytes = update.SizeBytes > 0 ? update.SizeBytes : null,
                WindowsUpdate = update,
                Note = update.VendorWarning ?? (update.RebootRequired ? "Windows will need a restart after this one." : null),
                FallbackPageUrl = DriverRules.OptionalUpdatesUri,
            }));
        }

        return offers;
    }

    /// <summary>The device name Windows Update puts before the first " · " in the description, else the title.</summary>
    private static string DeviceNameFrom(DriverUpdateInfo update)
    {
        var first = update.Description?.Split('\n')[0];
        var name = first?.Split(" · ")[0].Trim();
        return string.IsNullOrWhiteSpace(name) ? update.Title : name;
    }
}

/// <summary>NVIDIA's driver service, looked up per GPU by PCI device id (GeForce, RTX/Quadro, desktop and notebook).</summary>
public sealed class NvidiaDriverProvider : IDriverUpdateProvider
{
    private readonly Func<string, CancellationToken, Task<string>> _fetch;
    private readonly ILogger _logger;

    public NvidiaDriverProvider(Func<string, CancellationToken, Task<string>> fetch, ILogger logger)
    {
        _fetch = fetch;
        _logger = logger;
    }

    public DriverSourceKind Source => DriverSourceKind.Nvidia;

    public string Name => "NVIDIA";

    public async Task<DriverProviderResult> FindAsync(DriverSourceContext context, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var gpus = DriverDeviceMapper.DisplayAdapters(context.InstalledDrivers, DriverDeviceMapper.NvidiaVendorId).ToList();
        if (gpus.Count == 0)
        {
            return DriverProviderResult.Skipped("No NVIDIA graphics card");
        }

        progress?.Report(new ProgressUpdate("Asking NVIDIA for the newest driver"));
        var offers = new List<DeviceOffer>();
        var warnings = new List<string>();
        var answers = new Dictionary<string, NvidiaDriverRelease?>(StringComparer.OrdinalIgnoreCase);
        bool anyFound = false;
        foreach (var gpu in gpus)
        {
            var lookupId = DriverDeviceMapper.NvidiaLookupId(gpu);
            NvidiaDriverRelease? release = null;
            string? problem = null;
            if (lookupId is null)
            {
                problem = $"{gpu.DeviceName}: the device id could not be read, so NVIDIA could not be asked.";
            }
            else if (!answers.TryGetValue(lookupId, out release))
            {
                try
                {
                    var json = await _fetch(NvidiaDriverLookup.BuildUrl(lookupId, context.System.IsLaptop, context.Settings.NvidiaBranch), cancellationToken).ConfigureAwait(false);
                    release = NvidiaDriverLookup.Parse(json);
                    answers[lookupId] = release;
                    if (release is null)
                    {
                        problem = $"NVIDIA's driver service has no driver listed for {gpu.DeviceName}. Use NVIDIA's driver search instead.";
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
                {
                    _logger.LogWarning(ex, "NVIDIA driver lookup failed for {Device}.", lookupId);
                    problem = "NVIDIA's driver service could not be reached (" + ex.Message + "). Use NVIDIA's driver search instead.";
                    answers[lookupId] = null;
                }
            }

            var device = DeviceRef(gpu);
            if (release is null)
            {
                if (problem is not null)
                {
                    warnings.Add(problem);
                }

                offers.Add(new DeviceOffer(device, PageOffer(gpu)));
                continue;
            }

            anyFound = true;
            offers.Add(new DeviceOffer(device, BuildOffer(lookupId!, release, context.Settings)));
        }

        var status = anyFound ? offers.Count(o => o.Offer.Action != DriverOfferAction.OpenPage) + " driver(s) found" : "Not found";
        return new DriverProviderResult(offers, warnings.Distinct().ToList(), status, anyFound || warnings.Count == 0);
    }

    /// <summary>The device with its installed version in NVIDIA's numbering (32.0.15.6094 → 560.94).</summary>
    public static DriverDeviceRef DeviceRef(DriverInfo gpu) =>
        DriverDeviceMapper.ToDeviceRef(gpu) with
        {
            InstalledVendorVersion = DriverVersionMath.NvidiaReleaseFromWindows(gpu.Version),
            InstalledVendorScheme = DriverVersionScheme.NvidiaRelease,
        };

    /// <summary>The offer for a found release: a vendor package when downloads are allowed and the URL is on NVIDIA's download servers, else the page.</summary>
    public static DriverOffer BuildOffer(string lookupId, NvidiaDriverRelease release, DriverSourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(settings);
        bool download = settings.VendorDownloads == VendorDownloadMode.AskEachTime
            && VendorDownloadRules.IsAllowed(release.DownloadUrl, VendorDownloadRules.AllowedHosts(DriverSourceKind.Nvidia))
            && VendorDownloadRules.InstallerFileName(release.DownloadUrl) is not null;
        var page = release.DetailsUrl ?? NvidiaDriverLookup.DriverSearchPage;
        var branch = release.IsStudio ? "Studio" : release.Name.Contains("Game Ready", StringComparison.OrdinalIgnoreCase) ? "Game Ready" : null;
        var kind = release.IsNotebook ? "notebook" : "desktop";
        return new DriverOffer($"nvidia:{lookupId}:{release.Version}", DriverSourceKind.Nvidia, release.Name, release.Version,
            DriverVersionScheme.NvidiaRelease, release.ReleaseDate, download ? DriverOfferAction.VendorPackage : DriverOfferAction.OpenPage)
        {
            Url = download ? release.DownloadUrl : page,
            DetailsUrl = page,
            FallbackPageUrl = page,
            Note = string.Join(" · ", new[] { branch, release.IsWhql ? "WHQL" : null, kind + " package" }.Where(p => p is not null)),
        };
    }

    /// <summary>"Open the vendor page" when the lookup could not help.</summary>
    public static DriverOffer PageOffer(DriverInfo gpu) =>
        new("nvidia-page:" + gpu.DeviceId, DriverSourceKind.Nvidia, "NVIDIA driver search", null, DriverVersionScheme.None, null, DriverOfferAction.OpenPage)
        {
            Url = NvidiaDriverLookup.DriverSearchPage,
            FallbackPageUrl = NvidiaDriverLookup.DriverSearchPage,
            Note = "Pick your card on NVIDIA's site to see the newest driver.",
        };
}

/// <summary>AMD Software: Adrenalin Edition from AMD's official driver page, compared by the installed Adrenalin version.</summary>
public sealed class AmdDriverProvider : IDriverUpdateProvider
{
    private readonly Func<string, CancellationToken, Task<string>> _fetch;
    private readonly Func<IReadOnlyList<(string? MatchingDeviceId, string? RadeonSoftwareVersion)>> _registry;
    private readonly ILogger _logger;

    public AmdDriverProvider(Func<string, CancellationToken, Task<string>> fetch,
        Func<IReadOnlyList<(string? MatchingDeviceId, string? RadeonSoftwareVersion)>> registry, ILogger logger)
    {
        _fetch = fetch;
        _registry = registry;
        _logger = logger;
    }

    public DriverSourceKind Source => DriverSourceKind.Amd;

    public string Name => "AMD";

    public async Task<DriverProviderResult> FindAsync(DriverSourceContext context, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var gpus = DriverDeviceMapper.DisplayAdapters(context.InstalledDrivers, DriverDeviceMapper.AmdVendorId).ToList();
        if (gpus.Count == 0)
        {
            return DriverProviderResult.Skipped("No AMD Radeon graphics");
        }

        progress?.Report(new ProgressUpdate("Asking AMD for the newest Adrenalin release"));
        AmdDriverRelease? release = null;
        string? problem = null;
        try
        {
            release = AmdDriverLookup.ParseDriversPage(await _fetch(AmdDriverLookup.DriversPage, cancellationToken).ConfigureAwait(false));
            if (release is null)
            {
                problem = "AMD's driver page did not list an Adrenalin installer in the expected form (the page may have changed). Open AMD's page to check.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            _logger.LogWarning(ex, "AMD driver page could not be read.");
            problem = "AMD's driver page could not be reached (" + ex.Message + "). Open AMD's page to check.";
        }

        IReadOnlyList<(string?, string?)> entries = [];
        try
        {
            entries = _registry();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Radeon software version could not be read from the registry.");
        }

        var offers = gpus.Select(gpu =>
        {
            var device = DriverDeviceMapper.ToDeviceRef(gpu);
            var installed = AmdDriverLookup.InstalledAdrenalinVersion(entries, gpu);
            if (installed is not null)
            {
                device = device with { InstalledVendorVersion = installed, InstalledVendorScheme = DriverVersionScheme.Adrenalin };
            }

            return new DeviceOffer(device, release is null ? PageOffer() : BuildOffer(release, installed is not null, context.Settings));
        }).ToList();
        return new DriverProviderResult(offers, problem is null ? [] : [problem], release is null ? "Not found" : "Adrenalin " + release.Version, release is not null);
    }

    /// <summary>The Adrenalin offer: the auto-detect installer when downloads are allowed, else AMD's page.</summary>
    public static DriverOffer BuildOffer(AmdDriverRelease release, bool installedKnown, DriverSourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(settings);
        bool download = settings.VendorDownloads == VendorDownloadMode.AskEachTime
            && VendorDownloadRules.IsAllowed(release.DownloadUrl, VendorDownloadRules.AllowedHosts(DriverSourceKind.Amd))
            && VendorDownloadRules.InstallerFileName(release.DownloadUrl) is not null;
        var note = "Auto-detect installer: it picks the driver for your Radeon GPU while it runs.";
        if (!installedKnown)
        {
            note += " Your Adrenalin version could not be read; AMD Software shows it under Settings → System.";
        }

        return new DriverOffer("amd:" + release.Version, DriverSourceKind.Amd, "AMD Software: Adrenalin Edition", release.Version,
            DriverVersionScheme.Adrenalin, release.ReleaseDate, download ? DriverOfferAction.VendorPackage : DriverOfferAction.OpenPage)
        {
            Url = download ? release.DownloadUrl : AmdDriverLookup.DriversPage,
            DetailsUrl = release.ReleaseNotesUrl,
            FallbackPageUrl = AmdDriverLookup.DriversPage,
            ReferrerUrl = AmdDriverLookup.DriversPage,
            Note = note,
        };
    }

    /// <summary>"Open the vendor page" when AMD's page could not be read.</summary>
    public static DriverOffer PageOffer() =>
        new("amd-page", DriverSourceKind.Amd, "AMD driver downloads", null, DriverVersionScheme.None, null, DriverOfferAction.OpenPage)
        {
            Url = AmdDriverLookup.DriversPage,
            FallbackPageUrl = AmdDriverLookup.DriversPage,
            Note = "AMD's page offers the auto-detect installer and drivers per product.",
        };
}

/// <summary>Intel graphics, Wi-Fi and Bluetooth: the exact product download page, plus Intel's Driver &amp; Support Assistant.</summary>
public sealed class IntelDriverProvider : IDriverUpdateProvider
{
    private readonly Func<IReadOnlyList<OemTool>, CancellationToken, Task<IReadOnlyList<OemToolInstall>>> _detect;
    private readonly Func<CancellationToken, Task<bool>> _wingetAvailable;

    public IntelDriverProvider(Func<IReadOnlyList<OemTool>, CancellationToken, Task<IReadOnlyList<OemToolInstall>>> detect, Func<CancellationToken, Task<bool>> wingetAvailable)
    {
        _detect = detect;
        _wingetAvailable = wingetAvailable;
    }

    public DriverSourceKind Source => DriverSourceKind.Intel;

    public string Name => "Intel";

    public async Task<DriverProviderResult> FindAsync(DriverSourceContext context, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var offers = BuildPageOffers(context.InstalledDrivers);
        if (offers.Count == 0)
        {
            return DriverProviderResult.Skipped("No Intel graphics, Wi-Fi or Bluetooth");
        }

        var dsa = OemToolCatalog.Tools.First(t => t.Id == "intel-dsa");
        var installed = await _detect([dsa], cancellationToken).ConfigureAwait(false);
        bool winget = installed.Count == 0 && await _wingetAvailable(cancellationToken).ConfigureAwait(false);
        var system = SystemDevice(context.System);
        var all = offers.Concat(PcMakerToolProvider.ToolOffers(system, [dsa], installed, winget)).ToList();
        if (installed.Count == 0 && !winget)
        {
            all.Add(new DeviceOffer(system, new DriverOffer("intel-dsa-page", DriverSourceKind.Intel, dsa.Name, null, DriverVersionScheme.None, null, DriverOfferAction.OpenPage)
            {
                Url = IntelDriverCatalog.SupportAssistantPage,
                FallbackPageUrl = IntelDriverCatalog.SupportAssistantPage,
                Note = "Intel's own tool checks every Intel driver on this PC.",
            }));
        }

        return new DriverProviderResult(all, [], $"{offers.Count} device(s): links to Intel's pages", true);
    }

    /// <summary>One "Intel download page" offer per mapped Intel device, with the installed version in the note.</summary>
    public static IReadOnlyList<DeviceOffer> BuildPageOffers(IReadOnlyList<DriverInfo> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var offers = new List<DeviceOffer>();
        foreach (var driver in installed)
        {
            if (IntelDriverCatalog.PageFor(DriverDeviceMapper.IntelProductFor(driver)) is not { } page)
            {
                continue;
            }

            offers.Add(new DeviceOffer(DriverDeviceMapper.ToDeviceRef(driver),
                new DriverOffer("intel:" + driver.DeviceId, DriverSourceKind.Intel, page.Title, null, DriverVersionScheme.None, null, DriverOfferAction.OpenPage)
                {
                    Url = page.Url,
                    DetailsUrl = page.Url,
                    FallbackPageUrl = page.Url,
                    Note = "Intel's page lists the newest version" + (driver.Version is null ? "." : $"; you have {driver.Version}."),
                }));
        }

        return offers;
    }

    internal static DriverDeviceRef SystemDevice(DriverSystemFacts facts) => PcMakerToolProvider.SystemDevice(OemToolCatalog.Resolve(facts));
}

/// <summary>PC maker tools (Dell, HP, Lenovo, ASUS, MSI, GIGABYTE): open the installed tool, install it with winget, or open the model's support page.</summary>
public sealed class PcMakerToolProvider : IDriverUpdateProvider
{
    private readonly Func<IReadOnlyList<OemTool>, CancellationToken, Task<IReadOnlyList<OemToolInstall>>> _detect;
    private readonly Func<CancellationToken, Task<bool>> _wingetAvailable;

    public PcMakerToolProvider(Func<IReadOnlyList<OemTool>, CancellationToken, Task<IReadOnlyList<OemToolInstall>>> detect, Func<CancellationToken, Task<bool>> wingetAvailable)
    {
        _detect = detect;
        _wingetAvailable = wingetAvailable;
    }

    public DriverSourceKind Source => DriverSourceKind.PcMaker;

    public string Name => "PC maker";

    public async Task<DriverProviderResult> FindAsync(DriverSourceContext context, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var identity = OemToolCatalog.Resolve(context.System);
        if (identity is null)
        {
            return DriverProviderResult.Skipped("PC maker not recognised");
        }

        progress?.Report(new ProgressUpdate($"Looking for {identity.VendorName} tools"));
        var tools = OemToolCatalog.ToolsFor(identity.VendorKey, context.System.IsLaptop);
        var installed = await _detect(tools, cancellationToken).ConfigureAwait(false);
        bool winget = tools.Any(t => t.WingetId is not null && installed.All(i => i.Tool.Id != t.Id))
            && await _wingetAvailable(cancellationToken).ConfigureAwait(false);
        var device = SystemDevice(identity);
        var offers = ToolOffers(device, tools, installed, winget).ToList();
        offers.Add(new DeviceOffer(device, SupportPageOffer(identity)));
        var status = installed.Count > 0 ? string.Join(", ", installed.Select(i => i.Tool.Name)) + " installed" : $"No {identity.VendorName} tool installed";
        return new DriverProviderResult(offers, [], status, true);
    }

    /// <summary>The group for whole-PC tools, named after the maker and model.</summary>
    public static DriverDeviceRef SystemDevice(OemIdentity? identity)
    {
        var name = identity is null ? "This PC"
            : identity.Model is null ? $"This PC ({identity.VendorName})"
            : identity.IsBoard ? $"{identity.VendorName} {identity.Model} (motherboard)"
            : identity.Model.StartsWith(identity.VendorName, StringComparison.OrdinalIgnoreCase) ? identity.Model : $"{identity.VendorName} {identity.Model}";
        return new DriverDeviceRef(DriverOfferMerger.SystemKey, name, "System", null, null);
    }

    /// <summary>For each tool: "Open" when installed (and startable safely), else "Install with winget" when winget can, else nothing.</summary>
    public static IEnumerable<DeviceOffer> ToolOffers(DriverDeviceRef device, IReadOnlyList<OemTool> tools, IReadOnlyList<OemToolInstall> installed, bool wingetAvailable)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(installed);
        foreach (var tool in tools)
        {
            var install = installed.FirstOrDefault(i => i.Tool.Id == tool.Id);
            if (install is not null)
            {
                var target = tool.OpenUrl ?? (install.AppUserModelId is { } aumid ? @"shell:AppsFolder\" + aumid : install.ExecutablePath);
                yield return new DeviceOffer(device, new DriverOffer("tool:" + tool.Id, DriverSourceKind.PcMaker, tool.Name, null, DriverVersionScheme.None, null,
                    target is null ? DriverOfferAction.OpenPage : DriverOfferAction.OpenTool)
                {
                    Url = target,
                    Note = target is null
                        ? "Installed. Start it from the Start menu."
                        : "Installed. It checks the drivers, BIOS and firmware made for this PC.",
                });
            }
            else if (tool.WingetId is { } id && wingetAvailable && OemToolCatalog.IsValidWingetId(id))
            {
                yield return new DeviceOffer(device, new DriverOffer("winget:" + tool.Id, DriverSourceKind.PcMaker, tool.Name, null, DriverVersionScheme.None, null, DriverOfferAction.InstallTool)
                {
                    WingetId = id,
                    Note = "Not installed. Installs with: " + OemToolCatalog.WingetCommandText(id),
                });
            }
        }
    }

    /// <summary>The maker's support page for the exact model.</summary>
    public static DriverOffer SupportPageOffer(OemIdentity identity)
    {
        var url = OemToolCatalog.SupportUrl(identity);
        return new DriverOffer("support:" + identity.VendorKey, DriverSourceKind.PcMaker, $"{identity.VendorName} support page", null, DriverVersionScheme.None, null, DriverOfferAction.OpenPage)
        {
            Url = url,
            FallbackPageUrl = url,
            Note = identity.Model is null ? "Enter your model on the page." : $"Drivers and BIOS for {identity.Model}.",
        };
    }
}
