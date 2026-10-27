namespace PcChecker.Core.LiveUsage;

/// <summary>The kinds of resource the Live usage page graphs, in the order Task Manager lists them.</summary>
public enum LiveResourceKind
{
    Cpu,
    Memory,
    Disk,
    Network,
    Gpu,
}

/// <summary>What a physical disk is built from, as Task Manager labels it.</summary>
public enum DiskMediaKind
{
    Unknown,
    Hdd,
    Ssd,
    Nvme,
    Removable,
}

/// <summary>The family of a network adapter.</summary>
public enum NetworkKind
{
    Ethernet,
    WiFi,
    Other,
}

/// <summary>Static processor facts (read once).</summary>
public sealed record CpuFacts(
    string Name,
    double BaseSpeedMhz,
    int Sockets,
    int Cores,
    int LogicalProcessors,
    bool? VirtualizationEnabled,
    long? L1CacheBytes,
    long? L2CacheBytes,
    long? L3CacheBytes);

/// <summary>Static memory facts (read once). <see cref="TotalBytes"/> is what Windows can use.</summary>
public sealed record MemoryFacts(
    long TotalBytes,
    long? InstalledBytes,
    int? SpeedMhz,
    int? SlotsUsed,
    int? SlotsTotal,
    string? FormFactor,
    string? MemoryType)
{
    /// <summary>Memory kept back by the firmware and devices (installed minus usable), or null when unknown.</summary>
    public long? HardwareReservedBytes => InstalledBytes is { } installed ? Math.Max(0, installed - TotalBytes) : null;
}

/// <summary>One physical disk. <see cref="CounterInstance"/> is its PhysicalDisk counter instance ("0 C: D:").</summary>
public sealed record DiskFacts(
    int Index,
    string Model,
    IReadOnlyList<string> DriveLetters,
    DiskMediaKind Media,
    long? CapacityBytes,
    long? FormattedBytes,
    bool IsSystemDisk,
    bool HasPageFile,
    string? CounterInstance);

/// <summary>One active network adapter. <see cref="Id"/> is the adapter's interface id.</summary>
public sealed record NetworkFacts(
    string Id,
    string ConnectionName,
    string AdapterName,
    NetworkKind Kind,
    string? ConnectionType,
    IReadOnlyList<string> IPv4Addresses,
    IReadOnlyList<string> IPv6Addresses,
    string? Ssid,
    int? SignalPercent,
    long? LinkSpeedBitsPerSecond);

/// <summary>
/// One graphics adapter. <see cref="Luid"/> is written the way the GPU performance counters write it
/// ("0x00000000_0x0000D1C5") so engine counters can be matched to the card.
/// </summary>
public sealed record GpuFacts(
    int Index,
    string Name,
    string Luid,
    long DedicatedMemoryBytes,
    long SharedMemoryBytes,
    string? DriverVersion,
    DateTime? DriverDate,
    string? DirectXVersion,
    string? PhysicalLocation);

/// <summary>Everything the page lists, with the static facts for each resource.</summary>
public sealed record LiveUsageInventory(
    CpuFacts Cpu,
    MemoryFacts Memory,
    IReadOnlyList<DiskFacts> Disks,
    IReadOnlyList<NetworkFacts> Networks,
    IReadOnlyList<GpuFacts> Gpus,
    IReadOnlyList<string> Warnings);

/// <summary>Processor use at one moment. Percentages are 0–100.</summary>
public sealed record CpuSample(
    double UtilizationPercent,
    double KernelPercent,
    IReadOnlyList<double> LogicalPercents,
    double? SpeedMhz,
    int Processes,
    int Threads,
    int Handles,
    TimeSpan UpTime);

/// <summary>Memory use at one moment, in bytes.</summary>
public sealed record MemorySample(
    long TotalBytes,
    long InUseBytes,
    long AvailableBytes,
    long? CompressedBytes,
    long CommittedBytes,
    long CommitLimitBytes,
    long CachedBytes,
    long PagedPoolBytes,
    long NonPagedPoolBytes,
    long ModifiedBytes,
    long StandbyBytes,
    long FreeBytes)
{
    /// <summary>In use as a share of usable memory, 0–100.</summary>
    public double UsedPercent => TotalBytes <= 0 ? 0 : Math.Clamp(100.0 * InUseBytes / TotalBytes, 0, 100);
}

/// <summary>One disk at one moment.</summary>
public sealed record DiskSample(int Index, double ActivePercent, double AverageResponseMs, double ReadBytesPerSecond, double WriteBytesPerSecond);

/// <summary>One adapter at one moment. The Wi-Fi network name and signal are refreshed every few seconds.</summary>
public sealed record NetworkSample(string Id, double SendBitsPerSecond, double ReceiveBitsPerSecond, string? Ssid = null, int? SignalPercent = null);

/// <summary>One graphics adapter at one moment. <see cref="EnginePercents"/> is keyed by engine type ("3D", "Copy", …).</summary>
public sealed record GpuSample(
    string Luid,
    IReadOnlyDictionary<string, double> EnginePercents,
    double UtilizationPercent,
    long DedicatedUsedBytes,
    long SharedUsedBytes,
    double? TemperatureC);

/// <summary>One reading of every resource.</summary>
public sealed record LiveUsageSample(
    DateTimeOffset Timestamp,
    CpuSample Cpu,
    MemorySample Memory,
    IReadOnlyList<DiskSample> Disks,
    IReadOnlyList<NetworkSample> Networks,
    IReadOnlyList<GpuSample> Gpus,
    IReadOnlyList<string> Warnings);
