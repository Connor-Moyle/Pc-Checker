using PcChecker.Core.Optimization;

namespace PcChecker.Core.Cleanup;

public sealed record CleanupCategory(
    string Id,
    string Name,
    string Description,
    RiskLevel Risk,
    IReadOnlyList<string> PathTemplates,
    IReadOnlyList<string> IncludePatterns,
    bool EnabledByDefault,
    bool Recurse);

public sealed record CleanupFile(string Path, long SizeBytes, DateTime LastWrite);

public sealed record CategoryScanResult(
    CleanupCategory Category,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<CleanupFile> Files,
    IReadOnlyList<string> Errors);

public sealed record CleanupScanResult(IReadOnlyList<CategoryScanResult> Categories, long TotalBytes, DateTimeOffset Captured)
{
    public static CleanupScanResult Empty() => new([], 0, DateTimeOffset.Now);
}

public sealed record CleanupRunResult(int DeletedFiles, long FreedBytes, IReadOnlyList<string> Skipped, IReadOnlyList<string> Errors, bool DryRun);
