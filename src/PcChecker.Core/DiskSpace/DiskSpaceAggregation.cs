namespace PcChecker.Core.DiskSpace;

/// <summary>
/// Pure tree logic for the drill-down: totals bottom-up, finding a folder by path, removing what was recycled,
/// breadcrumbs and the size-sorted folder list. Iterative, so a very deep tree cannot overflow the stack.
/// </summary>
public static class DiskSpaceAggregation
{
    /// <summary>Fills TotalBytes, TotalFiles, TotalFolders and InaccessibleFolders for every folder from its own files.</summary>
    public static void Aggregate(FolderNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        // Post-order without recursion: collect in pre-order, then walk the list backwards.
        var order = new List<FolderNode>();
        var stack = new Stack<FolderNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            order.Add(node);
            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }

        for (int i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            long bytes = node.OwnBytes;
            long files = node.OwnFiles;
            long folders = 0;
            long inaccessible = node.IsInaccessible ? 1 : 0;
            foreach (var child in node.Children)
            {
                bytes += child.TotalBytes;
                files += child.TotalFiles;
                folders += child.TotalFolders + 1;
                inaccessible += child.InaccessibleFolders;
            }

            node.TotalBytes = bytes;
            node.TotalFiles = files;
            node.TotalFolders = folders;
            node.InaccessibleFolders = inaccessible;
        }
    }

    /// <summary>
    /// Builds an aggregated tree under <paramref name="rootPath"/> from file paths and sizes; files outside the root are
    /// ignored. Used by the demo service and the tests; the real scan builds its tree while it reads the disk.
    /// </summary>
    public static FolderNode BuildTree(string rootPath, IEnumerable<(string Path, long Bytes)> files, IEnumerable<string>? emptyFolders = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var root = new FolderNode(rootPath);
        foreach (var folder in emptyFolders ?? [])
        {
            var segments = DiskSpacePaths.RelativeSegments(folder, root.FullPath);
            if (segments is not null)
            {
                EnsureFolder(root, segments, segments.Count);
            }
        }

        foreach (var (path, bytes) in files)
        {
            var segments = DiskSpacePaths.RelativeSegments(path, root.FullPath);
            if (segments is null || segments.Count == 0)
            {
                continue;
            }

            EnsureFolder(root, segments, segments.Count - 1).AddFile(bytes);
        }

        Aggregate(root);
        return root;
    }

    /// <summary>The folder at <paramref name="path"/> inside <paramref name="root"/>, or null.</summary>
    public static FolderNode? Find(FolderNode root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        var segments = DiskSpacePaths.RelativeSegments(path, root.FullPath);
        if (segments is null)
        {
            return null;
        }

        var node = root;
        foreach (var segment in segments)
        {
            node = node.FindChild(segment);
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>Removes a folder (after it was recycled) and takes its totals off every folder above it.</summary>
    public static bool RemoveFolder(FolderNode root, string path)
    {
        var node = Find(root, path);
        if (node is null || node.Parent is null)
        {
            return false;
        }

        var parent = node.Parent;
        if (!parent.RemoveChild(node))
        {
            return false;
        }

        for (var p = parent; p is not null; p = p.Parent)
        {
            p.TotalBytes = Math.Max(0, p.TotalBytes - node.TotalBytes);
            p.TotalFiles = Math.Max(0, p.TotalFiles - node.TotalFiles);
            p.TotalFolders = Math.Max(0, p.TotalFolders - node.TotalFolders - 1);
            p.InaccessibleFolders = Math.Max(0, p.InaccessibleFolders - node.InaccessibleFolders);
        }

        return true;
    }

    /// <summary>Removes one file of <paramref name="bytes"/> from its folder and every folder above it.</summary>
    public static bool RemoveFile(FolderNode root, string filePath, long bytes)
    {
        var parentPath = DiskSpacePaths.ParentOf(filePath);
        var folder = parentPath is null ? null : Find(root, parentPath);
        if (folder is null || folder.OwnFiles == 0)
        {
            return false;
        }

        long taken = Math.Min(Math.Max(0, bytes), folder.OwnBytes);
        folder.RemoveOwnFile(taken);
        for (var p = folder; p is not null; p = p.Parent)
        {
            p.TotalBytes = Math.Max(0, p.TotalBytes - taken);
            p.TotalFiles = Math.Max(0, p.TotalFiles - 1);
        }

        return true;
    }

    /// <summary>Sub-folders, largest first (ties by name).</summary>
    public static IReadOnlyList<FolderNode> SortedChildren(FolderNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Children
            .OrderByDescending(c => c.TotalBytes)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The folders from the scan root down to <paramref name="node"/>, root first.</summary>
    public static IReadOnlyList<FolderNode> Breadcrumbs(FolderNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var trail = new List<FolderNode>();
        for (var n = node; n is not null; n = n.Parent)
        {
            trail.Add(n);
        }

        trail.Reverse();
        return trail;
    }

    /// <summary>The largest direct sub-folder, or null when there is none.</summary>
    public static FolderNode? LargestChild(FolderNode node) =>
        SortedChildren(node).FirstOrDefault(c => c.TotalBytes > 0);

    private static FolderNode EnsureFolder(FolderNode root, IReadOnlyList<string> segments, int count)
    {
        var node = root;
        for (int i = 0; i < count; i++)
        {
            node = node.FindChild(segments[i]) ?? node.AddChild(segments[i]);
        }

        return node;
    }
}
