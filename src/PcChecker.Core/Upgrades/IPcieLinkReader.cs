namespace PcChecker.Core.Upgrades;

/// <summary>Reads the PCIe link a device trained at (generation and lanes) from Windows' device properties. Read only.</summary>
public interface IPcieLinkReader
{
    /// <summary>The link of the device with this PnP instance id, or null when Windows does not report it.</summary>
    Task<PcieLinkInfo?> ReadAsync(string? pnpDeviceId, CancellationToken cancellationToken);
}
