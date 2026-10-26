using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>Demo cleaner: reports what would be freed and deletes nothing.</summary>
public sealed class DemoTempFileCleaner : ITempFileCleaner
{
    public async Task<CleanupRunResult> CleanAsync(CleanupScanResult scan, IEnumerable<string> categoryIds, bool dryRun, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var wanted = new HashSet<string>(categoryIds ?? [], StringComparer.OrdinalIgnoreCase);
        var selected = scan.Categories.Where(c => wanted.Count == 0 || wanted.Contains(c.Category.Id)).ToList();
        for (int i = 0; i < selected.Count; i++)
        {
            progress?.Report(new ProgressUpdate((dryRun ? "Checking " : "Cleaning ") + selected[i].Category.Name, 100.0 * i / Math.Max(1, selected.Count)));
            await Task.Delay(90, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        return new CleanupRunResult(selected.Sum(c => c.FileCount), selected.Sum(c => c.TotalBytes), [], [], dryRun);
    }

    public async Task<OperationResult> EmptyRecycleBinAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Recycle Bin emptied: 4.6 GB freed (demo).");
    }
}
