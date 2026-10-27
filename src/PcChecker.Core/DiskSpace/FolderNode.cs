namespace PcChecker.Core.DiskSpace;

/// <summary>
/// One folder in the scanned tree. During a scan only the thread that reads a folder adds its files and sub-folders;
/// <see cref="DiskSpaceAggregation.Aggregate"/> fills the totals once every folder has been read.
/// </summary>
public sealed class FolderNode
{
    private List<FolderNode>? _children;

    public FolderNode(string fullPath, FolderNode? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        FullPath = parent is null ? DiskSpacePaths.Normalize(fullPath) : fullPath;
        Name = DiskSpacePaths.NameOf(FullPath);
        Parent = parent;
    }

    public string Name { get; }

    public string FullPath { get; }

    public FolderNode? Parent { get; private set; }

    public IReadOnlyList<FolderNode> Children => (IReadOnlyList<FolderNode>?)_children ?? [];

    /// <summary>Bytes of the files directly in this folder.</summary>
    public long OwnBytes { get; private set; }

    /// <summary>Number of files directly in this folder.</summary>
    public long OwnFiles { get; private set; }

    /// <summary>Bytes of everything in this folder and below (after aggregation).</summary>
    public long TotalBytes { get; internal set; }

    /// <summary>Files in this folder and below (after aggregation).</summary>
    public long TotalFiles { get; internal set; }

    /// <summary>Sub-folders at any depth (after aggregation).</summary>
    public long TotalFolders { get; internal set; }

    /// <summary>Folders here and below that could not be read (after aggregation).</summary>
    public long InaccessibleFolders { get; internal set; }

    /// <summary>True when this folder's own listing could not be read.</summary>
    public bool IsInaccessible { get; set; }

    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    /// <summary>Adds a sub-folder. Only the thread that reads this folder may call it.</summary>
    public FolderNode AddChild(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var child = new FolderNode(DiskSpacePaths.Combine(FullPath, name), this);
        (_children ??= []).Add(child);
        return child;
    }

    /// <summary>Counts one file directly in this folder.</summary>
    public void AddFile(long bytes)
    {
        OwnBytes += Math.Max(0, bytes);
        OwnFiles++;
    }

    /// <summary>The direct sub-folder called <paramref name="name"/> (any case), or null.</summary>
    public FolderNode? FindChild(string name)
    {
        if (_children is null)
        {
            return null;
        }

        foreach (var child in _children)
        {
            if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    internal bool RemoveChild(FolderNode child)
    {
        if (_children is null || !_children.Remove(child))
        {
            return false;
        }

        child.Parent = null;
        return true;
    }

    internal void RemoveOwnFile(long bytes)
    {
        OwnBytes = Math.Max(0, OwnBytes - bytes);
        OwnFiles = Math.Max(0, OwnFiles - 1);
    }

    public override string ToString() => FullPath;
}
