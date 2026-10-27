namespace PcChecker.Core.Storage;

/// <summary>
/// Lists every physical drive once with its facts and SMART health: SATA and NVMe drives, USB drives and bridges, memory
/// cards, Storage Spaces (and their pool members), RAID volumes, dynamic, offline and empty disks. Routine refreshes never
/// wake a sleeping hard disk.
/// </summary>
public interface IDriveInventoryService
{
    /// <summary>The drives from the last refresh (empty before the first one).</summary>
    IReadOnlyList<PhysicalDrive> Last { get; }

    Task<IReadOnlyList<PhysicalDrive>> GetDrivesAsync(DriveReadMode mode, CancellationToken cancellationToken);

    /// <summary>Reads one drive again, waking it if asked. Returns the refreshed drive, or null when it is gone.</summary>
    Task<PhysicalDrive?> RefreshDriveAsync(string key, DriveReadMode mode, CancellationToken cancellationToken);
}
