using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>Demo junk-file scan with believable sizes per category.</summary>
public sealed class DemoTempFileScanner : ITempFileScanner
{
    private static readonly Dictionary<string, (int Files, long Bytes)> Sizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user.temp"] = (412, 1_380_000_000),
        ["windows.temp"] = (96, 240_000_000),
        ["windows.update.cache"] = (38, 2_900_000_000),
        ["delivery.optimization"] = (12, 1_100_000_000),
        ["thumbnails"] = (9, 310_000_000),
        ["recycle.bin"] = (57, 4_600_000_000),
        ["browser.chrome.cache"] = (3120, 1_750_000_000),
        ["browser.edge.cache"] = (640, 420_000_000),
        ["dx.shader.cache"] = (210, 980_000_000),
        ["nvidia.shader.cache"] = (330, 2_200_000_000),
        ["windows.error.reports"] = (24, 86_000_000),
    };

    public IReadOnlyList<CleanupCategory> Categories => CleanupCatalog.All;

    public async Task<CleanupScanResult> ScanAsync(IEnumerable<string> categoryIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(categoryIds ?? [], StringComparer.OrdinalIgnoreCase);
        var results = new List<CategoryScanResult>();
        var categories = Categories.Where(c => wanted.Count == 0 || wanted.Contains(c.Id)).ToList();
        for (int i = 0; i < categories.Count; i++)
        {
            var category = categories[i];
            progress?.Report(new ProgressUpdate(category.Name, 100.0 * i / Math.Max(1, categories.Count)));
            await Task.Delay(70, cancellationToken).ConfigureAwait(false);
            var (files, bytes) = Sizes.TryGetValue(category.Id, out var s) ? s : (0, 0L);
            var sample = Enumerable.Range(0, Math.Min(files, 5))
                .Select(n => new CleanupFile($@"C:\demo\{category.Id}\file{n}.tmp", bytes / Math.Max(1, files), DateTime.Now.AddDays(-n - 2)))
                .ToList();
            results.Add(new CategoryScanResult(category, files, bytes, sample, []));
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        return new CleanupScanResult(results, results.Sum(r => r.TotalBytes), DateTimeOffset.Now);
    }
}
