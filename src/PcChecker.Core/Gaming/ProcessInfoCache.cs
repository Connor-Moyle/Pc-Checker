namespace PcChecker.Core.Gaming;

/// <summary>
/// Remembers the name and exe path of processes the foreground tracker has already looked up. Windows reuses process
/// ids, so an entry is keyed by the id <em>and</em> the process start time: a new program that gets an old id misses
/// the cache instead of being reported (and matched to a game profile) under the old program's name. A process whose
/// start time cannot be read is never cached. Not thread-safe; the tracker polls on one thread at a time.
/// </summary>
public sealed class ProcessInfoCache
{
    public const int DefaultCapacity = 256;

    private readonly Dictionary<int, (long StartTime, string Name, string? Path)> _entries = [];
    private readonly int _capacity;

    public ProcessInfoCache(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public int Count => _entries.Count;

    /// <summary>The remembered name and path for this run of the process, if any.</summary>
    public bool TryGet(int processId, long? startTime, out (string Name, string? Path) info)
    {
        if (startTime is { } start && _entries.TryGetValue(processId, out var entry) && entry.StartTime == start)
        {
            info = (entry.Name, entry.Path);
            return true;
        }

        info = default;
        return false;
    }

    /// <summary>Remembers the name and path for this run of the process (ignored when the start time is unknown).</summary>
    public void Set(int processId, long? startTime, string name, string? path)
    {
        if (startTime is not { } start)
        {
            _entries.Remove(processId);
            return;
        }

        if (_entries.Count >= _capacity && !_entries.ContainsKey(processId))
        {
            _entries.Clear();
        }

        _entries[processId] = (start, name, path);
    }
}
