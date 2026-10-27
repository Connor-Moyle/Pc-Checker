using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>Demo cleaner: walks the selected categories with a believable pace, reports a few locked files and deletes nothing.</summary>
public sealed class DemoTempFileCleaner : ITempFileCleaner
{
    public async Task<CleanupRunResult> CleanAsync(CleanupScanResult scan, IEnumerable<string> categoryIds, bool dryRun, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var wanted = new HashSet<string>(categoryIds ?? [], StringComparer.OrdinalIgnoreCase);
        var selected = scan.Categories.Where(c => wanted.Count == 0 || wanted.Contains(c.Category.Id)).ToList();
        if (selected.Count == 0)
        {
            return new CleanupRunResult(0, 0, [], ["No categories were selected."], dryRun);
        }

        int deleted = 0;
        long freed = 0;
        var skipped = new List<string>();
        var messages = new List<string>();
        var random = DemoData.CreateRandom(11);

        for (int i = 0; i < selected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = selected[i];
            progress?.Report(new ProgressUpdate((dryRun ? "Checking " : "Cleaning ") + category.Category.Name, 100.0 * i / selected.Count));
            await Task.Delay(dryRun ? 60 : random.Next(120, 320), cancellationToken).ConfigureAwait(false);

            if (CleanupRules.IsReportOnly(category.Category.Id))
            {
                skipped.Add($"{category.Category.Name}: measured only; remove it with Storage settings or Disk Cleanup.");
                continue;
            }

            if (category.BlockedReason is { } blocked)
            {
                skipped.Add($"{category.Category.Name}: {blocked}");
                continue;
            }

            if (category.Category.Kind == CleanupKind.Command)
            {
                messages.Add(dryRun
                    ? $"{category.Category.Name}: Windows would run this."
                    : DemoCleanupCommandService.Message(category.Category));
                freed += category.SizeKnown ? category.TotalBytes : 0;
                continue;
            }

            if (dryRun)
            {
                deleted += category.FileCount;
                freed += category.TotalBytes;
                continue;
            }

            // A real run always meets a couple of files a program still has open.
            var locked = category.Files.Where((_, index) => category.Files.Count > 6 && index % 17 == 5).Take(2).ToList();
            foreach (var file in locked)
            {
                skipped.Add(file.Path + " (in use)");
            }

            deleted += category.FileCount - locked.Count;
            freed += category.TotalBytes - locked.Sum(f => f.SizeBytes);
        }

        progress?.Report(new ProgressUpdate("Done", 100, $"{ByteSize.Format(freed)} freed"));
        return new CleanupRunResult(deleted, freed, skipped, [], dryRun) { Messages = messages };
    }

    public async Task<OperationResult> EmptyRecycleBinAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("Recycle Bin emptied: 4.6 GB freed.");
    }
}
