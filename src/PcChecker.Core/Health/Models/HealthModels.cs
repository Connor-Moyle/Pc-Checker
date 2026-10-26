using PcChecker.Core.Common;

namespace PcChecker.Core.Health;

public sealed record HealthReport(
    int Score,
    IReadOnlyList<HealthItem> Items,
    IReadOnlyList<DiskHealth> Disks,
    IReadOnlyList<BatteryHealth> Batteries,
    IReadOnlyList<SystemErrorEntry> SystemErrors,
    IReadOnlyList<DeviceProblem> DeviceProblems,
    IReadOnlyList<ReliabilityEvent> Reliability,
    IReadOnlyList<CrashDumpInfo> CrashDumps,
    DateTimeOffset Captured,
    IReadOnlyList<string> Warnings)
{
    public static HealthReport Empty(string? warning = null) =>
        new(0, [], [], [], [], [], [], [], DateTimeOffset.Now, warning is null ? [] : [warning]);

    public HealthStatus OverallStatus => HealthScoring.StatusFromScore(Score);
}

public sealed record HealthItem(
    string Component,
    HealthStatus Status,
    string Title,
    string Detail,
    string? Recommendation,
    IReadOnlyDictionary<string, string> Metrics)
{
    public static IReadOnlyDictionary<string, string> NoMetrics { get; } = new Dictionary<string, string>();
}

public sealed record DiskHealth(
    string Model,
    string? Serial,
    string? Firmware,
    long SizeBytes,
    string MediaType,
    HealthStatus Status,
    string? SmartStatusText,
    double? TemperatureC,
    long? PowerOnHours,
    long? ReallocatedSectors,
    long? PendingSectors,
    long? UncorrectableErrors,
    double? WearPercent,
    long? HostWritesGb,
    IReadOnlyList<SmartAttribute> Attributes);

public sealed record SmartAttribute(int Id, string Name, int? Current, int? Worst, int? Threshold, long? RawValue, bool Failed);

public sealed record BatteryHealth(
    string Name,
    int? DesignCapacityMwh,
    int? FullChargeCapacityMwh,
    double? WearPercent,
    int? CycleCount,
    string? Chemistry,
    string Status,
    int? ChargePercent);

public sealed record SystemErrorEntry(DateTimeOffset Time, string Log, string Source, Severity Level, long EventId, string Message);

public sealed record DeviceProblem(string Name, string DeviceId, int ErrorCode, string Description, string Suggestion);

public sealed record ReliabilityEvent(DateTimeOffset Time, string Source, string Type, string Message);

public sealed record CrashDumpInfo(string Path, DateTimeOffset Time, long SizeBytes);
