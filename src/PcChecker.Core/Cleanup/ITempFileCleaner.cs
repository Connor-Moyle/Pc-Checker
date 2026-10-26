using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>Deletes files found by <see cref="ITempFileScanner"/>, honouring <see cref="CleanupSafety"/>.</summary>
public interface ITempFileCleaner
{
    Task<CleanupRunResult> CleanAsync(CleanupScanResult scan, IEnumerable<string> categoryIds, bool dryRun, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<OperationResult> EmptyRecycleBinAsync(CancellationToken cancellationToken);
}
