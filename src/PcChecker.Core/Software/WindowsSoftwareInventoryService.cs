// TODO(software): software module — registry Uninstall keys, Get-AppxPackage, sizes, bundles.
using PcChecker.Core.Common;

namespace PcChecker.Core.Software;

/// <summary>Windows implementation of <see cref="ISoftwareInventoryService"/>.</summary>
public sealed class WindowsSoftwareInventoryService : ISoftwareInventoryService
{
    public SoftwareInventory? Last { get; private set; }

    public Task<SoftwareInventory> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        Last = SoftwareInventory.Empty("Software inventory is not implemented yet.");
        return Task.FromResult(Last);
    }
}
