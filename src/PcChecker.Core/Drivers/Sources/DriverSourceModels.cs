namespace PcChecker.Core.Drivers.Sources;

/// <summary>Where a driver offer comes from.</summary>
public enum DriverSourceKind
{
    WindowsUpdate,
    Nvidia,
    Amd,
    Intel,
    PcMaker,
}

/// <summary>How a version string is written. Vendors number their packages differently from the Windows driver version.</summary>
public enum DriverVersionScheme
{
    /// <summary>The four-part Windows driver version (32.0.15.6094).</summary>
    Windows,

    /// <summary>NVIDIA's release number (560.94), derived from the last two parts of the Windows version.</summary>
    NvidiaRelease,

    /// <summary>AMD Software: Adrenalin Edition version (24.12.1). Not derivable from the Windows version.</summary>
    Adrenalin,

    /// <summary>No version (a page or a tool, not a package).</summary>
    None,
}

/// <summary>An offered version relative to the installed one.</summary>
public enum DriverVersionComparison
{
    Newer,
    Same,
    Older,
    Unknown,
}

/// <summary>What happens when the user picks an offer.</summary>
public enum DriverOfferAction
{
    /// <summary>Download and install through the Windows Update Agent (the existing Windows Update flow).</summary>
    WindowsUpdate,

    /// <summary>Download the vendor's installer from an allow-listed HTTPS host, check its signature, then run it.</summary>
    VendorPackage,

    /// <summary>Open a vendor web page in the browser.</summary>
    OpenPage,

    /// <summary>Start an installed PC maker or vendor tool.</summary>
    OpenTool,

    /// <summary>Install a PC maker or vendor tool with winget.</summary>
    InstallTool,
}

/// <summary>NVIDIA driver branch to look up.</summary>
public enum NvidiaDriverBranch
{
    /// <summary>Game Ready: first with game optimisations, released most often.</summary>
    GameReady,

    /// <summary>Studio: fewer releases, tested with creative apps.</summary>
    Studio,
}

/// <summary>How an NVIDIA package is run. AMD's and Intel's installers always run interactively.</summary>
public enum NvidiaInstallMode
{
    /// <summary>The NVIDIA installer opens normally and asks its own questions.</summary>
    Interactive,

    /// <summary>NVIDIA's documented silent switch (-s), no restart (-noreboot).</summary>
    Silent,

    /// <summary>Silent clean install (-s -clean -noreboot): resets NVIDIA settings to defaults.</summary>
    SilentClean,
}

/// <summary>Whether vendor packages may be downloaded from the app.</summary>
public enum VendorDownloadMode
{
    /// <summary>Offer the download and ask before every one (the default).</summary>
    AskEachTime,

    /// <summary>Never download; open the vendor's page instead.</summary>
    Never,
}

/// <summary>The device an offer belongs to. <see cref="Key"/> groups offers; the PC maker tools use <see cref="DriverOfferMerger.SystemKey"/>.</summary>
public sealed record DriverDeviceRef(
    string Key,
    string Name,
    string? DeviceClass,
    string? InstalledVersion,
    DateTime? InstalledDate)
{
    /// <summary>The installed version in the vendor's own numbering when known (NVIDIA 560.94, AMD 24.12.1).</summary>
    public string? InstalledVendorVersion { get; init; }

    /// <summary>The scheme of <see cref="InstalledVendorVersion"/>.</summary>
    public DriverVersionScheme InstalledVendorScheme { get; init; } = DriverVersionScheme.None;
}

/// <summary>One way to get a driver (or a tool that gets drivers) for one device from one source.</summary>
public sealed record DriverOffer(
    string Id,
    DriverSourceKind Source,
    string Title,
    string? Version,
    DriverVersionScheme Scheme,
    DateTime? ReleaseDate,
    DriverOfferAction Action)
{
    /// <summary>Download URL (vendor packages), page URL (pages) or tool path (tools).</summary>
    public string? Url { get; init; }

    /// <summary>Release notes or the product page.</summary>
    public string? DetailsUrl { get; init; }

    /// <summary>Download size in bytes when the source reported it.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>One plain line shown under the offer.</summary>
    public string? Note { get; init; }

    /// <summary>The page to open when this offer cannot be used (download refused, network down).</summary>
    public string? FallbackPageUrl { get; init; }

    /// <summary>The Windows Update entry behind a <see cref="DriverOfferAction.WindowsUpdate"/> offer.</summary>
    public DriverUpdateInfo? WindowsUpdate { get; init; }

    /// <summary>The winget package id behind an <see cref="DriverOfferAction.InstallTool"/> offer.</summary>
    public string? WingetId { get; init; }

    /// <summary>The page the download link was read from (sent as the HTTP referrer, as a browser would).</summary>
    public string? ReferrerUrl { get; init; }

    /// <summary>Filled in by the merger: how this offer compares with what is installed.</summary>
    public DriverVersionComparison Comparison { get; init; } = DriverVersionComparison.Unknown;

    /// <summary>Filled in by the merger: this is the newest offer for its device.</summary>
    public bool IsNewest { get; init; }
}

/// <summary>A device with every offer found for it, newest first.</summary>
public sealed record DriverDeviceOffers(DriverDeviceRef Device, IReadOnlyList<DriverOffer> Offers)
{
    /// <summary>The offer chosen by default: the newest; on a tie Windows Update, then the device maker.</summary>
    public DriverOffer? Recommended { get; init; }

    /// <summary>True when at least one source has something newer than the installed driver.</summary>
    public bool HasNewer => Offers.Any(o => o.Comparison == DriverVersionComparison.Newer);
}

/// <summary>One offer for one device, as a provider returns it.</summary>
public sealed record DeviceOffer(DriverDeviceRef Device, DriverOffer Offer);

/// <summary>What one provider found.</summary>
public sealed record DriverProviderResult(IReadOnlyList<DeviceOffer> Offers, IReadOnlyList<string> Warnings, string Status, bool Succeeded)
{
    /// <summary>Vendor links the provider adds to the "Download from the maker" card.</summary>
    public IReadOnlyList<VendorLink> VendorLinks { get; init; } = [];

    public static DriverProviderResult Skipped(string status) => new([], [], status, true);
}

/// <summary>The outcome of one source in a search, for the status line under the results.</summary>
public sealed record DriverSourceStatus(DriverSourceKind Source, string Name, bool Enabled, bool Succeeded, string Message);

/// <summary>The machine facts providers need besides the installed drivers.</summary>
public sealed record DriverSystemFacts(string? Manufacturer, string? Model, string? BoardManufacturer, string? BoardProduct, bool IsLaptop)
{
    public static DriverSystemFacts Unknown { get; } = new(null, null, null, null, false);
}

/// <summary>Everything a provider gets to work with.</summary>
public sealed record DriverSourceContext(IReadOnlyList<DriverInfo> InstalledDrivers, DriverSystemFacts System, DriverSourceSettings Settings);

/// <summary>Result of a multi-source search.</summary>
public sealed record DriverSourcesResult(
    IReadOnlyList<DriverDeviceOffers> Devices,
    IReadOnlyList<VendorLink> VendorLinks,
    IReadOnlyList<DriverSourceStatus> Sources,
    IReadOnlyList<string> Warnings,
    DateTimeOffset Captured)
{
    public static DriverSourcesResult Empty(string? warning = null) =>
        new([], [], [], warning is null ? [] : [warning], DateTimeOffset.Now);
}

/// <summary>What the download of a vendor package found before anything runs (shown in the confirmation).</summary>
public sealed record VendorPackageInfo(string Url, string FileName, long? SizeBytes, string ExpectedSigner);
