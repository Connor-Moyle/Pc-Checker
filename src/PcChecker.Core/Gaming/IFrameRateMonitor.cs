namespace PcChecker.Core.Gaming;

/// <summary>
/// Measures the frame rate of one process by counting its presented frames (the PresentMon approach: a real-time
/// event tracing session on the DirectX present events). Needs administrator rights, which PC Checker always has.
/// </summary>
public interface IFrameRateMonitor
{
    /// <summary>False when frame counting cannot work on this PC; <see cref="UnavailableReason"/> says why.</summary>
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    /// <summary>The process being measured (by default the game in front, see <see cref="IForegroundAppTracker"/>).</summary>
    int? TargetProcessId { get; }

    /// <summary>Measures this process from now on (null stops measuring until the next foreground change).</summary>
    void Track(int? processId);

    /// <summary>The most recent reading, or null when the tracked process presented nothing in the last 2 seconds.</summary>
    FrameRateSample? Latest { get; }

    /// <summary>Raised about twice a second while frames are arriving (background thread).</summary>
    event EventHandler<FrameRateSample>? Updated;

    void Start();

    void Stop();
}
