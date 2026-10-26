// TODO(cleanup): cleanup module — enumerate category roots, apply CleanupSafety, sizes.
using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>Windows implementation of <see cref="ITempFileScanner"/>.</summary>
public sealed class WindowsTempFileScanner : ITempFileScanner
{
    public IReadOnlyList<CleanupCategory> Categories => CleanupCatalog.All;

    public Task<CleanupScanResult> ScanAsync(IEnumerable<string> categoryIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(CleanupScanResult.Empty());
}
