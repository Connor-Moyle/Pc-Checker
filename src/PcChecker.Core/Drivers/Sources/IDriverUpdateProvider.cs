using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>
/// One place driver updates can come from: Windows Update, a chip maker (NVIDIA, AMD, Intel) or the PC maker's own
/// tool. A provider only looks; it never changes the system. It never throws for network or parsing problems:
/// those become warnings plus an "Open the vendor page" offer so the user still has a way forward.
/// </summary>
public interface IDriverUpdateProvider
{
    DriverSourceKind Source { get; }

    /// <summary>Short name shown in the results and in Settings.</summary>
    string Name { get; }

    Task<DriverProviderResult> FindAsync(DriverSourceContext context, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Searches every enabled <see cref="IDriverUpdateProvider"/> and merges the offers per device; downloads, checks and
/// runs vendor packages and installs PC maker tools. Every change confirms in the UI first and runs guarded.
/// </summary>
public interface IDriverSourceService
{
    /// <summary>
    /// Searches the enabled sources. <paramref name="installed"/> is the last installed-driver scan; when empty the
    /// service scans first.
    /// </summary>
    Task<DriverSourcesResult> SearchAsync(IReadOnlyList<DriverInfo> installed, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Looks up the package's size and file name without downloading it. Fails for a URL outside the allow-list.</summary>
    Task<OperationResult<VendorPackageInfo>> InspectPackageAsync(DriverOffer offer, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a vendor package into a folder only administrators can change, checks its Authenticode signature
    /// and signer, deletes it when anything is wrong, otherwise creates a restore point and runs the installer.
    /// </summary>
    Task<OperationResult> InstallPackageAsync(DriverOffer offer, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Starts an installed tool from a <see cref="DriverOfferAction.OpenTool"/> offer.</summary>
    OperationResult OpenTool(DriverOffer offer);

    /// <summary>True when winget can be run from its protected install folder.</summary>
    Task<bool> IsWingetAvailableAsync(CancellationToken cancellationToken);

    /// <summary>Installs the tool behind an <see cref="DriverOfferAction.InstallTool"/> offer with winget, guarded.</summary>
    Task<OperationResult> InstallToolAsync(DriverOffer offer, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}
