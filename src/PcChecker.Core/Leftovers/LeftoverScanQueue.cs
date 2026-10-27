namespace PcChecker.Core.Leftovers;

/// <summary>
/// Hands "find the leftovers of the program I just uninstalled" from the Apps page to the Leftovers page without
/// either page knowing the other. The Apps page queues the program; the Leftovers page takes it when it is shown, or
/// straight away through <see cref="Queued"/> when it is already open. Only the latest request is kept. Thread-safe.
/// </summary>
public sealed class LeftoverScanQueue
{
    private readonly object _gate = new();
    private LeftoverFocus? _pending;

    /// <summary>Raised after a request is queued (on the caller's thread).</summary>
    public event EventHandler? Queued;

    /// <summary>True while a request waits to be taken.</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>Queues a focused scan, replacing any request that was not taken yet.</summary>
    public void Enqueue(LeftoverFocus focus)
    {
        ArgumentNullException.ThrowIfNull(focus);
        lock (_gate)
        {
            _pending = focus;
        }

        Queued?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes the waiting request, or returns null when there is none.</summary>
    public LeftoverFocus? Take()
    {
        lock (_gate)
        {
            var focus = _pending;
            _pending = null;
            return focus;
        }
    }
}
