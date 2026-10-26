using PcChecker.Core.Common;

namespace PcChecker.Core.Software;

/// <summary>Lists installed programs from the registry (64/32/user hives) and Store apps.</summary>
public interface ISoftwareInventoryService
{
    Task<SoftwareInventory> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    SoftwareInventory? Last { get; }
}
