namespace PcChecker.Core.DiskSpace;

/// <summary>
/// The pure steps of the duplicate search: group by size, split groups by a key (a quick hash of the start and end,
/// then a full SHA-256), drop singletons and order the result by wasted space. Reading files is the service's job.
/// </summary>
public static class DuplicateFinder
{
    /// <summary>Bytes read from the start and from the end of a file for the quick hash.</summary>
    public const int PartialBlockBytes = 64 * 1024;

    /// <summary>
    /// Files of the same size, at least <paramref name="minBytes"/> (and never empty files), in groups of two or more.
    /// A path listed twice counts once. Groups are ordered by the space they could waste, largest first.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<DiskFileEntry>> GroupBySize(IEnumerable<DiskFileEntry> files, long minBytes)
    {
        ArgumentNullException.ThrowIfNull(files);
        long floor = Math.Max(1, minBytes);
        return files
            .Where(f => f.SizeBytes >= floor)
            .DistinctBy(f => DiskSpacePaths.Normalize(f.Path), StringComparer.OrdinalIgnoreCase)
            .GroupBy(f => f.SizeBytes)
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Key * (g.Count() - 1))
            .Select(g => (IReadOnlyList<DiskFileEntry>)g.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList())
            .ToList();
    }

    /// <summary>
    /// The candidates worth reading: files that share their size with another file, in size groups that hold at least
    /// one file the user may remove. A group made only of protected files (copies inside Windows or Program Files) could
    /// never free anything, so it is not hashed at all.
    /// </summary>
    public static IReadOnlyList<DiskFileEntry> ActionableCandidates(IEnumerable<DiskFileEntry> candidates, long minBytes, Func<string, bool> isProtected)
    {
        ArgumentNullException.ThrowIfNull(isProtected);
        return GroupBySize(candidates, minBytes)
            .Where(g => g.Any(f => !isProtected(f.Path)))
            .SelectMany(g => g)
            .ToList();
    }

    /// <summary>
    /// Splits every group by <paramref name="keyOf"/> and keeps the parts with two or more files. A null key (the
    /// file could not be read or changed size) drops that file.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<T>> SplitBy<T>(IEnumerable<IReadOnlyList<T>> groups, Func<T, string?> keyOf)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(keyOf);
        var result = new List<IReadOnlyList<T>>();
        foreach (var group in groups)
        {
            foreach (var part in group.Select(f => (File: f, Key: keyOf(f))).Where(x => x.Key is not null).GroupBy(x => x.Key!, StringComparer.Ordinal))
            {
                var files = part.Select(x => x.File).ToList();
                if (files.Count > 1)
                {
                    result.Add(files);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Keeps one entry per physical file: hard links (same volume and file id) share their data, so deleting one frees
    /// nothing. Entries without an identity are all kept.
    /// </summary>
    public static IReadOnlyList<T> CollapseSameFile<T>(IReadOnlyList<T> group, Func<T, string?> identityOf)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(identityOf);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return group.Where(f => identityOf(f) is not { } id || seen.Add(id)).ToList();
    }

    /// <summary>
    /// The byte ranges the quick hash reads: the whole file when it is at most two blocks, otherwise the first and the
    /// last block. When the whole file is read the quick hash already is the full hash.
    /// </summary>
    public static IReadOnlyList<(long Offset, int Length)> PartialRanges(long size, int blockBytes = PartialBlockBytes)
    {
        if (size <= 0)
        {
            return [];
        }

        blockBytes = Math.Max(1, blockBytes);
        if (size <= 2L * blockBytes)
        {
            return [(0, (int)size)];
        }

        return [(0, blockBytes), (size - blockBytes, blockBytes)];
    }

    /// <summary>True when the quick hash did not cover the whole file, so a full hash must confirm the match.</summary>
    public static bool NeedsFullHash(long size, int blockBytes = PartialBlockBytes) => size > 2L * Math.Max(1, blockBytes);

    /// <summary>Final groups, most wasted space first; files in a group by path.</summary>
    public static IReadOnlyList<DuplicateGroup> ToGroups(IEnumerable<(string Hash, IReadOnlyList<DiskFileEntry> Files)> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return groups
            .Where(g => g.Files.Count > 1)
            .Select(g => new DuplicateGroup(g.Hash, g.Files[0].SizeBytes, g.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(g => g.WastedBytes)
            .ThenBy(g => g.Files[0].Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>Which copies the "keep newest" and "keep oldest" helpers tick for removal.</summary>
public static class DuplicateSelection
{
    /// <summary>Every copy except the most recently changed one (and never a protected one).</summary>
    public static IReadOnlyList<DiskFileEntry> KeepNewest(DuplicateGroup group, Func<string, bool>? isProtected = null) =>
        AllBut(group, group.Files.OrderByDescending(f => f.LastWrite).ThenBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First(), isProtected);

    /// <summary>Every copy except the oldest one (and never a protected one).</summary>
    public static IReadOnlyList<DiskFileEntry> KeepOldest(DuplicateGroup group, Func<string, bool>? isProtected = null) =>
        AllBut(group, group.Files.OrderBy(f => f.LastWrite).ThenBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First(), isProtected);

    private static IReadOnlyList<DiskFileEntry> AllBut(DuplicateGroup group, DiskFileEntry keep, Func<string, bool>? isProtected) =>
        group.Files.Where(f => !ReferenceEquals(f, keep) && !(isProtected?.Invoke(f.Path) ?? false)).ToList();
}
