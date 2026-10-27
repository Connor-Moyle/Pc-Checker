namespace PcChecker.Core.DiskSpace;

/// <summary>
/// Collects what the page shows about files while a scan runs: the totals per type, the largest files, the old big
/// files and the duplicate candidates. One per scan thread, merged at the end, so no locking is needed. Pure.
/// </summary>
public sealed class DiskScanCollector
{
    private readonly DiskScanOptions _options;
    private readonly DateTime _now;
    private readonly long[] _typeBytes = new long[Enum.GetValues<FileTypeCategory>().Length];
    private readonly long[] _typeFiles = new long[Enum.GetValues<FileTypeCategory>().Length];
    private readonly BoundedTopList<DiskFileEntry> _largest;
    private readonly BoundedTopList<DiskFileEntry> _old;
    private List<DiskFileEntry> _candidates = [];

    public DiskScanCollector(DiskScanOptions options, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _now = now;
        _largest = new BoundedTopList<DiskFileEntry>(options.LargestFileCount);
        _old = new BoundedTopList<DiskFileEntry>(options.OldFileCount);
    }

    /// <summary>Files seen, cloud-only ones included.</summary>
    public long Files { get; private set; }

    /// <summary>Files whose content lives only in the cloud (not counted towards the size).</summary>
    public long CloudOnlyFiles { get; private set; }

    /// <summary>Bytes counted so far.</summary>
    public long Bytes { get; private set; }

    /// <summary>True when more duplicate candidates were found than <see cref="DiskScanOptions.MaxDuplicateCandidates"/>.</summary>
    public bool CandidatesTruncated { get; private set; }

    /// <summary>
    /// Records one file. Returns the bytes it adds to the used space: 0 for a cloud-only placeholder. The full path is
    /// only built when the file makes one of the lists.
    /// </summary>
    public long AddFile(string folderPath, ReadOnlySpan<char> name, long bytes, FileAttributes attributes, DateTime lastWrite,
        DateTime? lastAccess, FolderKind folderKind)
    {
        Files++;
        if (DiskSpaceRules.IsCloudOnly(attributes))
        {
            CloudOnlyFiles++;
            return 0;
        }

        bytes = Math.Max(0, bytes);
        Bytes += bytes;
        var category = FileTypeClassifier.Classify(name, folderKind);
        _typeBytes[(int)category] += bytes;
        _typeFiles[(int)category]++;

        var lastUsed = lastAccess is { } a && a > lastWrite ? a : lastWrite;
        bool largest = _largest.WouldAccept(bytes);
        bool old = DiskSpaceRules.IsOldBigFile(bytes, lastUsed, _now, _options.OldFileMinBytes, _options.OldFileDays) && _old.WouldAccept(bytes);
        bool candidate = bytes > 0 && bytes >= _options.DuplicateMinBytes;
        if (!largest && !old && !candidate)
        {
            return bytes;
        }

        var entry = new DiskFileEntry(DiskSpacePaths.Combine(folderPath, name.ToString()), bytes, lastWrite, lastAccess, category);
        if (largest)
        {
            _largest.Add(entry, bytes);
        }

        if (old)
        {
            _old.Add(entry, bytes);
        }

        if (candidate)
        {
            AddCandidate(entry);
        }

        return bytes;
    }

    /// <summary>Adds everything <paramref name="other"/> collected.</summary>
    public void Merge(DiskScanCollector other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Files += other.Files;
        CloudOnlyFiles += other.CloudOnlyFiles;
        Bytes += other.Bytes;
        CandidatesTruncated |= other.CandidatesTruncated;
        for (int i = 0; i < _typeBytes.Length; i++)
        {
            _typeBytes[i] += other._typeBytes[i];
            _typeFiles[i] += other._typeFiles[i];
        }

        _largest.Merge(other._largest);
        _old.Merge(other._old);
        foreach (var c in other._candidates)
        {
            AddCandidate(c);
        }
    }

    public IReadOnlyList<DiskFileEntry> LargestFiles() => _largest.ToDescendingList();

    public IReadOnlyList<DiskFileEntry> OldFiles() => _old.ToDescendingList();

    /// <summary>Every category with its totals, largest first; empty categories are left out.</summary>
    public IReadOnlyList<FileTypeTotal> Types() =>
        Enum.GetValues<FileTypeCategory>()
            .Select(c => new FileTypeTotal(c, _typeBytes[(int)c], _typeFiles[(int)c]))
            .Where(t => t.Files > 0)
            .OrderByDescending(t => t.Bytes)
            .ToList();

    /// <summary>The duplicate candidates, largest first, at most <see cref="DiskScanOptions.MaxDuplicateCandidates"/>.</summary>
    public IReadOnlyList<DiskFileEntry> DuplicateCandidates()
    {
        Trim(_options.MaxDuplicateCandidates);
        return _candidates.OrderByDescending(c => c.SizeBytes).ToList();
    }

    private void AddCandidate(DiskFileEntry entry)
    {
        _candidates.Add(entry);
        int max = Math.Max(1, _options.MaxDuplicateCandidates);
        if (_candidates.Count >= max * 2)
        {
            Trim(max);
        }
    }

    private void Trim(int max)
    {
        max = Math.Max(0, max);
        if (_candidates.Count <= max)
        {
            return;
        }

        CandidatesTruncated = true;
        _candidates = _candidates.OrderByDescending(c => c.SizeBytes).Take(max).ToList();
    }
}
