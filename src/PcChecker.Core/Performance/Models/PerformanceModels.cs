using PcChecker.Core.Common;

namespace PcChecker.Core.Performance;

public enum FindingCategory
{
    Startup,
    Services,
    Processes,
    Memory,
    Storage,
    Power,
    Visuals,
    Network,
    Updates,
    Thermal,
    Configuration,
}

public sealed record SlowdownFinding(
    string Id,
    FindingCategory Category,
    Severity Severity,
    string Title,
    string Detail,
    string Impact,
    IReadOnlyList<string> ActionIds,
    bool AdvancedOnly);

public sealed record StartupItem(string Name, string Command, string Location, bool Enabled, string? Publisher, string Kind);

public sealed record ServiceInfo(string Name, string DisplayName, string StartMode, string State, string? Path, bool IsMicrosoft, string? Description);

public sealed record ProcessInfo(int Pid, string Name, double CpuPercent, long WorkingSetBytes, string? Path, string? Publisher);

public sealed record PerformanceReport(
    int Score,
    IReadOnlyList<SlowdownFinding> Findings,
    IReadOnlyList<StartupItem> StartupItems,
    IReadOnlyList<ServiceInfo> Services,
    IReadOnlyList<ProcessInfo> TopProcesses,
    double? LastBootSeconds,
    double MemoryUsedPercent,
    long MemoryTotalBytes,
    string? ActivePowerPlan,
    IReadOnlyList<(string Drive, long Free, long Total, string MediaType)> Drives,
    DateTimeOffset Captured,
    IReadOnlyList<string> Warnings)
{
    public static PerformanceReport Empty(string? warning = null) =>
        new(0, [], [], [], [], null, 0, 0, null, [], DateTimeOffset.Now, warning is null ? [] : [warning]);
}
