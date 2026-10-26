using PcChecker.Core.Common;

namespace PcChecker.Core.Hardware;

/// <summary>Scans the machine for every connected device and its properties.</summary>
public interface IHardwareInventoryService
{
    Task<HardwareInventory> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>The most recent scan result, or null before the first scan.</summary>
    HardwareInventory? Last { get; }
}
