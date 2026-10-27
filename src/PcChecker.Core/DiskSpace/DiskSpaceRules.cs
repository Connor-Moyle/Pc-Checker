namespace PcChecker.Core.DiskSpace;

/// <summary>Small pure rules the scan, the duplicate finder and the page share.</summary>
public static class DiskSpaceRules
{
    // FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: OneDrive and other cloud placeholders.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>
    /// True for a cloud placeholder whose content is not on this PC (OneDrive "online-only" files): it takes no space
    /// and reading it would download it, so it is counted but its size is not, and it is never hashed.
    /// </summary>
    public static bool IsCloudOnly(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    /// <summary>True when a file is big enough and has not been opened or changed for <paramref name="days"/> days.</summary>
    public static bool IsOldBigFile(long bytes, DateTime lastUsed, DateTime now, long minBytes, int days) =>
        bytes >= Math.Max(1, minBytes) && days > 0 && lastUsed < now.AddDays(-days);

    /// <summary>
    /// Drops every path that lies inside another path of the list (recycling the folder already takes it) and exact
    /// duplicates, keeping the first spelling. Order is kept.
    /// </summary>
    public static IReadOnlyList<T> CollapseNested<T>(IEnumerable<T> items, Func<T, string> pathOf)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(pathOf);
        var list = items.Select(i => (Item: i, Path: DiskSpacePaths.Normalize(pathOf(i)))).ToList();
        var all = new HashSet<string>(list.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<T>();
        foreach (var (item, path) in list)
        {
            // Walk up the parents: linear in the depth, so ticking thousands of duplicates stays instant.
            bool insideAnother = false;
            for (var parent = DiskSpacePaths.ParentOf(path); parent is not null && !insideAnother; parent = DiskSpacePaths.ParentOf(parent))
            {
                insideAnother = all.Contains(parent);
            }

            if (!insideAnother && seen.Add(path))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>
    /// True when the path is inside a OneDrive folder ("OneDrive" or "OneDrive - Company"): removing it there also
    /// removes it from OneDrive online, so the confirmation says so.
    /// </summary>
    public static bool IsInCloudFolder(string path) =>
        DiskSpacePaths.Segments(path).SkipLast(1).Any(s => s.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A test for "is this path one of <paramref name="removed"/> or inside one of them", built once so checking
    /// thousands of paths only walks each path's parents.
    /// </summary>
    public static Func<string, bool> RemovedFilter(IEnumerable<string> removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        var set = new HashSet<string>(removed.Where(r => !string.IsNullOrWhiteSpace(r)).Select(DiskSpacePaths.Normalize), StringComparer.OrdinalIgnoreCase);
        return path =>
        {
            for (var p = string.IsNullOrWhiteSpace(path) ? null : DiskSpacePaths.Normalize(path); p is not null; p = DiskSpacePaths.ParentOf(p))
            {
                if (set.Contains(p))
                {
                    return true;
                }
            }

            return false;
        };
    }

    /// <summary>
    /// The scan result after some items were recycled, without scanning again: the folder tree (updated in place), the
    /// file lists and the duplicate candidates no longer show them. The type totals only lose the files that were
    /// listed one by one, because the scan does not keep every file of a recycled folder; a new scan is exact.
    /// </summary>
    public static DiskScanResult WithoutRemoved(DiskScanResult scan, IReadOnlyList<RecycleRequest> removed)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(removed);
        if (removed.Count == 0)
        {
            return scan;
        }

        foreach (var item in removed)
        {
            if (item.IsFolder)
            {
                DiskSpaceAggregation.RemoveFolder(scan.Root, item.Path);
            }
            else
            {
                DiskSpaceAggregation.RemoveFile(scan.Root, item.Path, item.SizeBytes);
            }
        }

        var gone = RemovedFilter(removed.Select(r => r.Path));

        var removedFiles = scan.LargestFiles.Concat(scan.OldFiles).Concat(scan.DuplicateCandidates)
            .Where(f => gone(f.Path))
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var types = scan.Types
            .Select(t => t with
            {
                Bytes = Math.Max(0, t.Bytes - removedFiles.Where(f => f.Category == t.Category).Sum(f => f.SizeBytes)),
                Files = Math.Max(0, t.Files - removedFiles.Count(f => f.Category == t.Category)),
            })
            .ToList();

        return scan with
        {
            LargestFiles = scan.LargestFiles.Where(f => !gone(f.Path)).ToList(),
            OldFiles = scan.OldFiles.Where(f => !gone(f.Path)).ToList(),
            DuplicateCandidates = scan.DuplicateCandidates.Where(f => !gone(f.Path)).ToList(),
            Types = types,
        };
    }
}
