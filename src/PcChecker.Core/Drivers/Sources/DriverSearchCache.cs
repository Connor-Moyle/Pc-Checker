namespace PcChecker.Core.Drivers.Sources;

/// <summary>
/// The last driver update search from the Drivers page, kept for other pages (the Crashes page shows "a newer driver
/// is available" for the driver a crash is blamed on without searching again). Thread-safe; nothing is stored on disk.
/// </summary>
public sealed class DriverSearchCache
{
    private readonly object _gate = new();
    private DriverSourcesResult? _last;

    /// <summary>Raised after a new search result is stored.</summary>
    public event EventHandler? Changed;

    public DriverSourcesResult? Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    public void Set(DriverSourcesResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            _last = result;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
