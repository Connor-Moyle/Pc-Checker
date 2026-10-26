using PcChecker.Core.Common;

namespace PcChecker.Core.Storage;

public enum BlockState
{
    Untested,
    Ok,
    Slow,
    Bad,
}

public enum RepairMode
{
    Scan,
    SpotFix,
    OfflineScanAndFix,
}

public sealed record StorageDevice(
    int Number,
    string Model,
    string? Serial,
    string? Firmware,
    long SizeBytes,
    string MediaType,
    string BusType,
    HealthStatus Health,
    IReadOnlyList<string> Volumes,
    bool IsSystemDisk);

public sealed record SurfaceScanProgress(
    long BlocksTotal,
    long BlocksDone,
    long BadBlocks,
    long SlowBlocks,
    double CurrentMBps,
    IReadOnlyList<BlockState> Map)
{
    public double Percent => BlocksTotal <= 0 ? 0 : 100.0 * BlocksDone / BlocksTotal;
}

public sealed record SurfaceScanResult(
    int DiskNumber,
    long BytesScanned,
    long BadBlocks,
    long SlowBlocks,
    double AvgMBps,
    double MinMBps,
    TimeSpan Elapsed,
    bool Completed,
    IReadOnlyList<long> BadBlockOffsets,
    IReadOnlyList<BlockState> Map,
    string Summary);

public sealed record DiskBenchmarkResult(
    string Drive,
    double SeqReadMBps,
    double SeqWriteMBps,
    double Rand4kReadMBps,
    double Rand4kWriteMBps,
    double Rand4kReadIops,
    double Rand4kWriteIops,
    long TestFileBytes,
    string Summary);

public sealed record DiskErrorEvent(DateTimeOffset Time, string Source, long EventId, string Message, string? Device);
