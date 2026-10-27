namespace PcChecker.Core.DiskSpace;

/// <summary>
/// Keeps the <see cref="Capacity"/> items with the largest keys seen so far (a min-heap), so a scan of millions of
/// files only ever holds a few hundred of them. Not thread-safe: each scan thread keeps its own and they are merged.
/// </summary>
public sealed class BoundedTopList<T>
{
    private readonly PriorityQueue<T, long> _heap = new();

    public BoundedTopList(int capacity)
    {
        Capacity = Math.Max(0, capacity);
    }

    public int Capacity { get; }

    public int Count => _heap.Count;

    /// <summary>True when an item with <paramref name="key"/> would be kept; lets callers skip building the item.</summary>
    public bool WouldAccept(long key) =>
        Capacity > 0 && (_heap.Count < Capacity || (_heap.TryPeek(out _, out var smallest) && key > smallest));

    public void Add(T item, long key)
    {
        if (!WouldAccept(key))
        {
            return;
        }

        if (_heap.Count < Capacity)
        {
            _heap.Enqueue(item, key);
        }
        else
        {
            _heap.EnqueueDequeue(item, key);
        }
    }

    public void Merge(BoundedTopList<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (item, key) in other._heap.UnorderedItems)
        {
            Add(item, key);
        }
    }

    /// <summary>The kept items, largest key first.</summary>
    public IReadOnlyList<T> ToDescendingList() =>
        _heap.UnorderedItems.OrderByDescending(e => e.Priority).Select(e => e.Element).ToList();
}
