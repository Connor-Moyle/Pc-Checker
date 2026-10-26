// TODO(hardware): hardware module — WMI queries per class, property bag mapping.
using PcChecker.Core.Common;

namespace PcChecker.Core.Hardware;

/// <summary>Windows implementation of <see cref="IHardwareInventoryService"/> based on WMI.</summary>
public sealed class WindowsHardwareInventoryService : IHardwareInventoryService
{
    public HardwareInventory? Last { get; private set; }

    public Task<HardwareInventory> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Hardware scan", 100));
        var inventory = new HardwareInventory();
        inventory.Warnings.Add("Hardware scanning is not implemented yet.");
        Last = inventory;
        return Task.FromResult(inventory);
    }
}
