namespace PcChecker.Core.Hardware;

public sealed record ComputerSystemInfo(
    string Name, string? Manufacturer, string? Model, string? SystemType, string? SystemFamily, string? SystemSkuNumber,
    long? TotalPhysicalMemoryBytes, int? NumberOfProcessors, int? NumberOfLogicalProcessors, string? Domain, string? UserName,
    string? PcSystemType, string? ChassisType, IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "System", Manufacturer, null, null, Properties);

public sealed record OperatingSystemInfo(
    string Name, string? Caption, string? Version, string? BuildNumber, string? DisplayVersion, string? Architecture,
    DateTime? InstallDate, DateTime? LastBootUpTime, string? SystemDirectory, string? WindowsDirectory, string? Locale,
    long? TotalVisibleMemoryBytes, long? FreePhysicalMemoryBytes, string? SerialNumber, string? RegisteredUser,
    IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Operating system", "Microsoft", null, null, Properties);

public sealed record BiosInfo(
    string Name, string? Manufacturer, string? Version, string? SmBiosVersion, DateTime? ReleaseDate, string? SerialNumber,
    bool? IsUefi, bool? SecureBootEnabled, IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "BIOS", Manufacturer, null, null, Properties);

public sealed record MotherboardInfo(
    string Name, string? Manufacturer, string? Product, string? Version, string? SerialNumber, string? Chipset,
    IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Motherboard", Manufacturer, null, null, Properties);

public sealed record CpuInfo(
    string Name, string? Manufacturer, string? DeviceId, string? Status, string? Description, string? Architecture,
    string? Socket, int? Cores, int? LogicalProcessors, int? MaxClockMhz, int? CurrentClockMhz, int? L2CacheKb, int? L3CacheKb,
    string? ProcessorId, string? Family, string? Stepping, bool? VirtualizationEnabled, int? LoadPercent, int? AddressWidth,
    IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Processor", Manufacturer, DeviceId, Status, Properties);

public sealed record GpuInfo(
    string Name, string? Manufacturer, string? DeviceId, string? Status, long? AdapterRamBytes, string? DriverVersion,
    DateTime? DriverDate, string? VideoProcessor, string? VideoModeDescription, int? CurrentHorizontalResolution,
    int? CurrentVerticalResolution, int? CurrentRefreshRate, string? PnpDeviceId, string? Vendor, bool IsIntegrated,
    IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Graphics", Manufacturer, DeviceId, Status, Properties);

public sealed record MemoryModuleInfo(
    string Name, string? Manufacturer, string? DeviceId, string? Status, string? BankLabel, string? DeviceLocator,
    long? CapacityBytes, int? SpeedMhz, int? ConfiguredClockSpeedMhz, string? MemoryType, string? FormFactor,
    string? PartNumber, string? SerialNumber, int? ConfiguredVoltageMv, int? DataWidth, int? TotalWidth,
    IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Memory", Manufacturer, DeviceId, Status, Properties);

public sealed record PartitionInfo(
    string Name, string? DeviceId, long? SizeBytes, long? StartingOffset, int? Index, string? Type, bool? Bootable,
    bool? BootPartition, bool? PrimaryPartition, IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Partition", null, DeviceId, null, Properties);

/// <summary>A physical drive (Win32_DiskDrive + MSFT_PhysicalDisk) and the partitions laid out on it.</summary>
public sealed record PhysicalDiskInfo(
    string Name, string? Manufacturer, string? DeviceId, string? Status, string? Model, string? SerialNumber,
    string? FirmwareRevision, string? InterfaceType, string? MediaType, string? BusType, long? SizeBytes,
    string? HealthStatusText, int? SpindleSpeed, string? PnpDeviceId, PartitionInfo[] Partitions,
    IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Storage", Manufacturer, DeviceId, Status, Properties)
{
    /// <summary>Number of partitions found on the drive.</summary>
    public int PartitionCount => Partitions.Length;
}

public sealed record LogicalDriveInfo(
    string Name, string? DeviceId, string? VolumeName, string? FileSystem, string? DriveTypeText, long? SizeBytes,
    long? FreeSpaceBytes, string? VolumeSerialNumber, bool? Compressed, IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Drive", null, DeviceId, null, Properties)
{
    public long? UsedBytes => SizeBytes.HasValue && FreeSpaceBytes.HasValue ? SizeBytes - FreeSpaceBytes : null;
    public double? UsedPercent => SizeBytes is > 0 && UsedBytes.HasValue ? 100.0 * UsedBytes.Value / SizeBytes.Value : null;
}

public sealed record NetworkAdapterInfo(
    string Name, string? Manufacturer, string? DeviceId, string? Status, string? MacAddress, string? AdapterType,
    long? SpeedBitsPerSecond, bool? PhysicalAdapter, string? NetConnectionId, string? NetConnectionStatus,
    IReadOnlyList<string> IpAddresses, IReadOnlyList<string> Gateways, IReadOnlyList<string> DnsServers, bool? DhcpEnabled,
    string? PnpDeviceId, IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Network", Manufacturer, DeviceId, Status, Properties);

public sealed record MonitorInfo(
    string Name, string? Manufacturer, string? DeviceId, string? Status, string? ProductCode, string? SerialNumber,
    int? YearOfManufacture, int? WeekOfManufacture, string? InstanceName, int? ScreenWidth, int? ScreenHeight,
    IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Monitor", Manufacturer, DeviceId, Status, Properties);

public sealed record AudioDeviceInfo(
    string Name, string? Manufacturer, string? DeviceId, string? Status, string? ProductName, string? PnpDeviceId,
    IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Audio", Manufacturer, DeviceId, Status, Properties);

public sealed record UsbDeviceInfo(
    string Name, string? Manufacturer, string? DeviceId, string? Status, string? Description, string? Service,
    string? ClassGuid, string? PnpClass, IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "USB", Manufacturer, DeviceId, Status, Properties);

public sealed record BatteryInfo(
    string Name, string? Manufacturer, string? DeviceId, string? Status, string? Chemistry, int? DesignCapacityMwh,
    int? FullChargeCapacityMwh, int? EstimatedChargePercent, int? EstimatedRunTimeMinutes, string? BatteryStatusText,
    int? DesignVoltageMv, IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Battery", Manufacturer, DeviceId, Status, Properties);

public sealed record InputDeviceInfo(
    string Name, string? Manufacturer, string? DeviceId, string? Status, string Kind, string? Description, string? Layout,
    int? NumberOfButtons, int? NumberOfFunctionKeys, string? PnpDeviceId, IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Input", Manufacturer, DeviceId, Status, Properties);

public sealed record PnpDeviceInfo(
    string Name, string? Manufacturer, string? DeviceId, string? Status, string? PnpClass, string? ClassGuid,
    string? Service, string? Description, int? ConfigManagerErrorCode, bool? Present, IReadOnlyList<string> HardwareIds,
    IReadOnlyDictionary<string, string> Properties)
    : HardwareItem(Name, "Device", Manufacturer, DeviceId, Status, Properties);
