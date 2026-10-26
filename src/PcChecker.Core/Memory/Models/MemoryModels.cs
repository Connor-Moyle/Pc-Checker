using PcChecker.Core.Hardware;

namespace PcChecker.Core.Memory;

public sealed record PageFileInfo(string Drive, int InitialMb, int MaximumMb, bool SystemManaged);

public sealed record MemoryOverview(
    long TotalBytes,
    long AvailableBytes,
    long CommitTotalBytes,
    long CommitLimitBytes,
    long CachedBytes,
    long StandbyBytes,
    bool MemoryCompressionEnabled,
    bool SysMainEnabled,
    bool AutomaticPageFile,
    IReadOnlyList<PageFileInfo> PageFiles,
    string XmpState,
    int? ConfiguredMhz,
    int? RatedMhz,
    IReadOnlyList<MemoryModuleInfo> Modules,
    string? LastMemoryDiagnosticResult,
    int Slots,
    int SlotsUsed,
    bool DualChannelLikely,
    IReadOnlyList<string> Warnings)
{
    public long UsedBytes => Math.Max(0, TotalBytes - AvailableBytes);
    public double UsedPercent => TotalBytes <= 0 ? 0 : 100.0 * UsedBytes / TotalBytes;

    public static MemoryOverview Empty(string? warning = null) =>
        new(0, 0, 0, 0, 0, 0, false, false, true, [], "Unknown", null, null, [], null, 0, 0, false, warning is null ? [] : [warning]);
}

public sealed record MemoryTestResult(
    long BytesTested,
    int Passes,
    long Errors,
    IReadOnlyList<string> ErrorDetails,
    TimeSpan Elapsed,
    bool Completed,
    string Summary);

public sealed record MemoryProfile(
    string Id,
    string Name,
    string Description,
    bool MemoryCompression,
    bool SysMain,
    bool AutomaticPageFile,
    int? PageFileInitialMb,
    int? PageFileMaxMb,
    bool AdvancedOnly);
