namespace PcChecker.Core.DiskSpace;

/// <summary>What kind of content a file is, for the breakdown by type.</summary>
public enum FileTypeCategory
{
    Video,
    Images,
    Games,
    Archives,
    Installers,
    System,
    Other,
}

/// <summary>A drive the user can scan, with its used and free space.</summary>
public sealed record DriveSpaceInfo(
    string RootPath,
    string Label,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    bool IsSystemDrive)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    /// <summary>"C:" from "C:\".</summary>
    public string Letter => RootPath.TrimEnd('\\', '/');
}

/// <summary>One file seen by the scan. Times are local; <see cref="LastAccess"/> is null when Windows does not keep it.</summary>
public sealed record DiskFileEntry(
    string Path,
    long SizeBytes,
    DateTime LastWrite,
    DateTime? LastAccess,
    FileTypeCategory Category)
{
    public string Name => DiskSpacePaths.NameOf(Path);

    public string Folder => DiskSpacePaths.ParentOf(Path) ?? Path;

    /// <summary>The later of the last write and last access time: when the file was last used, as far as Windows knows.</summary>
    public DateTime LastUsed => LastAccess is { } access && access > LastWrite ? access : LastWrite;
}

/// <summary>Total size and file count of one <see cref="FileTypeCategory"/>.</summary>
public sealed record FileTypeTotal(FileTypeCategory Category, long Bytes, long Files);

/// <summary>Limits and thresholds for one scan.</summary>
public sealed record DiskScanOptions
{
    public static DiskScanOptions Default { get; } = new();

    /// <summary>How many of the largest files to keep.</summary>
    public int LargestFileCount { get; init; } = 100;

    /// <summary>Files at least this large are kept as duplicate candidates.</summary>
    public long DuplicateMinBytes { get; init; } = 1024L * 1024;

    /// <summary>A file at least this large that has not been used for <see cref="OldFileDays"/> is an old big file.</summary>
    public long OldFileMinBytes { get; init; } = 50L * 1024 * 1024;

    public int OldFileDays { get; init; } = 365;

    /// <summary>How many old big files to keep (largest first).</summary>
    public int OldFileCount { get; init; } = 100;

    /// <summary>Upper bound on duplicate candidates kept in memory; the largest files win when there are more.</summary>
    public int MaxDuplicateCandidates { get; init; } = 250_000;
}

/// <summary>Everything one scan found. <see cref="Root"/> is the folder tree for the drill-down.</summary>
public sealed record DiskScanResult(
    string RootPath,
    FolderNode Root,
    DriveSpaceInfo? Drive,
    IReadOnlyList<DiskFileEntry> LargestFiles,
    IReadOnlyList<DiskFileEntry> OldFiles,
    IReadOnlyList<FileTypeTotal> Types,
    IReadOnlyList<DiskFileEntry> DuplicateCandidates,
    DiskScanStats Stats,
    DateTimeOffset Captured);

/// <summary>Counters from a scan, including what it could not read or chose to skip.</summary>
public sealed record DiskScanStats(
    long Files,
    long Folders,
    long InaccessibleFolders,
    long SkippedLinks,
    long CloudOnlyFiles,
    TimeSpan Duration,
    bool CandidatesTruncated);

/// <summary>Files with identical content. Keeping one copy frees <see cref="WastedBytes"/>.</summary>
public sealed record DuplicateGroup(string Hash, long SizeBytes, IReadOnlyList<DiskFileEntry> Files)
{
    public long WastedBytes => SizeBytes * Math.Max(0, Files.Count - 1);
}

public sealed record DuplicateScanResult(
    IReadOnlyList<DuplicateGroup> Groups,
    long FilesCompared,
    long FilesUnreadable,
    TimeSpan Duration)
{
    public long WastedBytes => Groups.Sum(g => g.WastedBytes);
}

/// <summary>One item the user asked to move to the Recycle Bin.</summary>
public sealed record RecycleRequest(string Path, bool IsFolder, long SizeBytes);

/// <summary>The outcome for one <see cref="RecycleRequest"/>.</summary>
public sealed record RecycleItemResult(RecycleRequest Request, bool Success, string Message);

public sealed record RecycleResult(IReadOnlyList<RecycleItemResult> Items)
{
    public int Recycled => Items.Count(i => i.Success);

    public int Failed => Items.Count(i => !i.Success);

    public long RecycledBytes => Items.Where(i => i.Success).Sum(i => i.Request.SizeBytes);
}

/// <summary>
/// Locations that are never offered for deletion. Built from the running machine by the Windows service and from
/// sample paths by the demo service; <see cref="DiskSpaceProtection"/> applies the rules.
/// </summary>
public sealed record ProtectedLocations(
    string? WindowsDirectory,
    IReadOnlyList<string> ProgramDirectories,
    string? UsersRoot,
    string? UserProfile,
    IReadOnlyList<string> AppDirectories)
{
    public static ProtectedLocations None { get; } = new(null, [], null, null, []);
}
