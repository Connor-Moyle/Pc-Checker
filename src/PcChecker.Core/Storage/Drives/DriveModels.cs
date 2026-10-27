using PcChecker.Core.Health;

namespace PcChecker.Core.Storage;

/// <summary>What the drive stores data on.</summary>
public enum DriveMedia
{
    Unknown,

    /// <summary>A spinning hard disk.</summary>
    Hdd,

    /// <summary>A solid state drive (SATA, NVMe or USB flash based).</summary>
    Ssd,

    /// <summary>Storage class memory (Intel Optane and similar).</summary>
    Scm,

    /// <summary>An SD or MMC memory card.</summary>
    FlashCard,
}

/// <summary>Where a drive sits in the storage stack.</summary>
public enum DriveRole
{
    /// <summary>An ordinary disk with its own disk number.</summary>
    Disk,

    /// <summary>A Storage Spaces virtual disk made from pool member drives.</summary>
    StorageSpace,

    /// <summary>A physical drive inside a Storage Spaces pool. Windows hides it as a disk of its own.</summary>
    PoolMember,

    /// <summary>An Intel RST / AMD RAID volume. Its member drives sit behind the RAID driver.</summary>
    RaidArray,
}

/// <summary>Whether the drive is spinning / awake.</summary>
public enum DrivePowerState
{
    Unknown,
    Active,

    /// <summary>The drive is in standby (an HDD has spun down). Reading SMART would wake it.</summary>
    Asleep,
}

/// <summary>How SMART data was (or was not) obtained for a drive.</summary>
public enum SmartAccess
{
    /// <summary>Not tried yet.</summary>
    NotRead,

    /// <summary>Read just now.</summary>
    Read,

    /// <summary>The drive was asleep; the last reading (if any) is shown instead.</summary>
    SkippedAsleep,

    /// <summary>The drive does not support SMART or did not answer.</summary>
    NotSupported,

    /// <summary>A USB enclosure or adapter did not pass the SMART command through.</summary>
    BlockedByEnclosure,

    /// <summary>A RAID volume: SMART belongs to each member drive and the RAID driver may hide it.</summary>
    HiddenByRaid,

    /// <summary>A Storage Spaces virtual disk has no SMART of its own; see its member drives.</summary>
    NotApplicable,

    /// <summary>Reading SMART needs administrator rights.</summary>
    NeedsAdmin,
}

/// <summary>The plain verdict shown for each drive.</summary>
public enum DriveVerdict
{
    Unknown,
    Good,
    Caution,
    Bad,
}

/// <summary>A drive's verdict with the one-line reason and every finding behind it.</summary>
public sealed record DriveHealthVerdict(DriveVerdict Verdict, string Reason, IReadOnlyList<string> Findings)
{
    public static DriveHealthVerdict Unknown(string reason) => new(DriveVerdict.Unknown, reason, []);

    /// <summary>"Good", "Caution", "Bad" or "Unknown".</summary>
    public string Label => Verdict switch
    {
        DriveVerdict.Good => "Good",
        DriveVerdict.Caution => "Caution",
        DriveVerdict.Bad => "Bad",
        _ => "Unknown",
    };
}

/// <summary>Decoded health values from ATA SMART attributes or the NVMe health log. Null means "not reported".</summary>
public sealed record SmartValues
{
    public long? ReallocatedSectors { get; init; }
    public long? PowerOnHours { get; init; }
    public long? SpinRetries { get; init; }
    public long? PowerCycles { get; init; }
    public long? ReportedUncorrectable { get; init; }
    public long? CommandTimeouts { get; init; }
    public double? TemperatureC { get; init; }
    public long? PendingSectors { get; init; }
    public long? OfflineUncorrectable { get; init; }
    public long? CrcErrors { get; init; }
    public double? WearPercent { get; init; }
    public long? HostWritesGb { get; init; }

    /// <summary>NVMe media and data integrity errors.</summary>
    public long? MediaErrors { get; init; }

    /// <summary>NVMe unsafe shutdowns (power lost without a clean shutdown).</summary>
    public long? UnsafeShutdowns { get; init; }

    /// <summary>NVMe available spare (percent) and its threshold.</summary>
    public int? AvailableSparePercent { get; init; }
    public int? AvailableSpareThreshold { get; init; }

    /// <summary>NVMe critical warning bits (0 = none).</summary>
    public int? NvmeCriticalWarning { get; init; }

    /// <summary>Attributes whose normalized value is at or below the drive's own threshold ("id Name").</summary>
    public IReadOnlyList<string> FailedAttributes { get; init; } = [];

    /// <summary>Pre-fail attributes at or below their threshold (the drive itself predicts failure).</summary>
    public IReadOnlyList<string> FailedPreFailAttributes { get; init; } = [];
}

/// <summary>One SMART reading of one drive.</summary>
public sealed record SmartReading(
    DateTimeOffset ReadAt,
    IReadOnlyList<SmartAttribute> Attributes,
    SmartValues Values,
    bool PredictsFailure,
    string Source);

/// <summary>Everything PC Checker knows about one physical drive, a Storage Spaces virtual disk or a RAID volume.</summary>
public sealed record PhysicalDrive
{
    /// <summary>Windows disk number (Disk 0, Disk 1…), or -1 when Windows hides the drive (Storage Spaces pool member).</summary>
    public int Number { get; init; } = -1;

    /// <summary>The MSFT_PhysicalDisk DeviceId when known (also set for pool members).</summary>
    public string? PhysicalDeviceId { get; init; }

    public string Model { get; init; } = "Unknown drive";
    public string? Serial { get; init; }
    public string? Firmware { get; init; }
    public long SizeBytes { get; init; }
    public DriveMedia Media { get; init; }

    /// <summary>Spindle speed for hard disks when the drive reports it.</summary>
    public int? Rpm { get; init; }

    /// <summary>"NVMe", "SATA", "USB", "RAID", "Storage Spaces", "SD"…</summary>
    public string BusType { get; init; } = "Unknown";

    /// <summary>"SATA 6 Gb/s", "PCIe 4.0 x4"… or null when not known.</summary>
    public string? LinkSpeed { get; init; }

    /// <summary>"GPT", "MBR" or "Not initialized".</summary>
    public string PartitionStyle { get; init; } = "Unknown";

    public bool IsDynamic { get; init; }
    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsSystemDisk { get; init; }
    public bool IsBootDisk { get; init; }
    public bool IsUsbBridge { get; init; }
    public IReadOnlyList<string> Volumes { get; init; } = [];

    /// <summary>Number of partitions without a drive letter (recovery, EFI, hidden data partitions).</summary>
    public int HiddenPartitions { get; init; }

    public DriveRole Role { get; init; }

    /// <summary>Storage Spaces pool the drive belongs to, or the pool a virtual disk is made from.</summary>
    public string? PoolName { get; init; }

    /// <summary>For a Storage Spaces virtual disk: "Two-way mirror", "Parity"…</summary>
    public string? Resiliency { get; init; }

    /// <summary>For a Storage Spaces virtual disk: the member drive models.</summary>
    public IReadOnlyList<string> Members { get; init; } = [];

    /// <summary>MSFT_PhysicalDisk / MSFT_Disk health status text ("Healthy", "Warning", "Unhealthy"), or null.</summary>
    public string? WindowsHealth { get; init; }

    public string? PnpDeviceId { get; init; }
    public DrivePowerState Power { get; init; }
    public SmartAccess SmartAccess { get; init; }

    /// <summary>A plain sentence about SMART access ("The USB enclosure does not pass SMART through.").</summary>
    public string? SmartNote { get; init; }

    /// <summary>The latest SMART reading (possibly from an earlier refresh when the drive is asleep now).</summary>
    public SmartReading? Smart { get; init; }

    public DriveHealthVerdict Verdict { get; init; } = DriveHealthVerdict.Unknown("Not checked yet.");

    /// <summary>True when the drive can be opened by number for scans and benchmarks.</summary>
    public bool HasDiskNumber => Number >= 0;

    /// <summary>A key that identifies the same drive across refreshes (serial when known, else model + size).</summary>
    public string Key => DriveSerial.Normalize(Serial) is { Length: > 0 } s && !DriveSerial.IsPlaceholder(Serial)
        ? "SN:" + s
        : $"MD:{DriveSerial.Normalize(Model)}:{SizeBytes}:{Number}";
}

/// <summary>How hard the inventory may try to read SMART.</summary>
public enum DriveReadMode
{
    /// <summary>Routine refresh: never wakes a sleeping drive.</summary>
    Routine,

    /// <summary>The person asked for a fresh reading: sleeping drives are woken.</summary>
    WakeIfAsleep,
}
