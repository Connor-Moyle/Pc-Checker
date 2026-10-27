using PcChecker.Core.Common;

namespace PcChecker.Core.Storage;

/// <summary>Turns inventory drives into the <see cref="StorageDevice"/> entries the Storage page and its tools use. Pure.</summary>
public static class StorageDeviceMapper
{
    public static StorageDevice ToStorageDevice(PhysicalDrive drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        return new StorageDevice(
            drive.Number,
            drive.Model,
            drive.Serial,
            drive.Firmware,
            drive.SizeBytes,
            DriveMediaRules.Label(drive.Media),
            drive.BusType,
            Health(drive),
            drive.Volumes,
            drive.IsSystemDisk)
        {
            Drive = drive,
        };
    }

    /// <summary>Good → Good, Caution → Warning, Bad → Critical; without a verdict, the health Windows reports.</summary>
    public static HealthStatus Health(PhysicalDrive drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        return drive.Verdict.Verdict switch
        {
            DriveVerdict.Good => HealthStatus.Good,
            DriveVerdict.Caution => HealthStatus.Warning,
            DriveVerdict.Bad => HealthStatus.Critical,
            _ => drive.WindowsHealth?.ToUpperInvariant() switch
            {
                "HEALTHY" => HealthStatus.Good,
                "WARNING" => HealthStatus.Warning,
                "UNHEALTHY" => HealthStatus.Critical,
                _ => HealthStatus.Unknown,
            },
        };
    }

    /// <summary>Numbered disks first by number, then the hidden pool members.</summary>
    public static IReadOnlyList<StorageDevice> ToStorageDevices(IEnumerable<PhysicalDrive> drives) =>
        (drives ?? []).OrderBy(d => d.Number < 0 ? 1 : 0).ThenBy(d => d.Number).ThenBy(d => d.Model, StringComparer.OrdinalIgnoreCase).Select(ToStorageDevice).ToList();
}
