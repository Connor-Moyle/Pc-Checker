namespace PcChecker.Core.Games;

/// <summary>
/// Hands one request from the Games page to another page without either page knowing the other (the same idea as the
/// Leftovers hand-off). The sender queues the request and opens the page; the receiving page takes it when it is
/// shown, or straight away through <see cref="Queued"/> when it is already open. Only the latest request is kept.
/// Thread-safe.
/// </summary>
public class GameHandOff<T>
    where T : class
{
    private readonly object _gate = new();
    private T? _pending;

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

    /// <summary>Queues a request, replacing any that was not taken yet.</summary>
    public void Enqueue(T request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _pending = request;
        }

        Queued?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes the waiting request, or returns null when there is none.</summary>
    public T? Take()
    {
        lock (_gate)
        {
            var request = _pending;
            _pending = null;
            return request;
        }
    }
}

/// <summary>Games page → Missing DLLs page: scan this game's folder.</summary>
public sealed class GameFolderScanHandOff : GameHandOff<GameFolderScanRequest>;

/// <summary>Games page → Overlay page: open a game profile for this game's program.</summary>
public sealed class GameProfileHandOff : GameHandOff<GameProfileRequest>;
