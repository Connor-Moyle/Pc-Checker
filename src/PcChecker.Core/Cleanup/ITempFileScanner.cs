using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>Finds junk files by category without deleting anything.</summary>
public interface ITempFileScanner
{
    IReadOnlyList<CleanupCategory> Categories { get; }
    Task<CleanupScanResult> ScanAsync(IEnumerable<string> categoryIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}
