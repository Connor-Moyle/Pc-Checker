namespace PcChecker.Core.Gaming;

/// <summary>
/// Watches which application is in front (polled every 500 ms). PC Checker itself and Windows shell processes
/// (Explorer, Start, search, ...) are ignored, so <see cref="Current"/> keeps the last real app while you look at them.
/// Start/Stop are reference counted: every caller of <see cref="Start"/> calls <see cref="Stop"/> once.
/// </summary>
public interface IForegroundAppTracker
{
    /// <summary>The last non-ignored foreground app, or null before the first one was seen.</summary>
    ForegroundApp? Current { get; }

    /// <summary>Raised (on a background thread) when a different app, or the same app switching full screen on/off, comes to the front.</summary>
    event EventHandler<ForegroundApp>? ForegroundChanged;

    void Start();

    void Stop();

    /// <summary>Programs with a visible window, busiest first (GPU use where Windows reports it, then CPU).</summary>
    Task<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken cancellationToken);

    /// <summary>True while the process is still running (false when it exited or cannot be found).</summary>
    bool IsProcessRunning(int processId);

    /// <summary>Calls <paramref name="onExited"/> once when the process ends. Dispose the result to stop watching.</summary>
    IDisposable WatchProcessExit(int processId, Action onExited);
}
