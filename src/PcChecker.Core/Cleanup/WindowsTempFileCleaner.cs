// TODO(cleanup): cleanup module — delete with safety checks, recycle bin via SHEmptyRecycleBin.
using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>Windows implementation of <see cref="ITempFileCleaner"/>.</summary>
public sealed class WindowsTempFileCleaner : ITempFileCleaner
{
    public Task<CleanupRunResult> CleanAsync(CleanupScanResult scan, IEnumerable<string> categoryIds, bool dryRun, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(new CleanupRunResult(0, 0, [], ["Cleanup is not implemented yet."], dryRun));

    public Task<OperationResult> EmptyRecycleBinAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail("Cleanup is not implemented yet."));
}
