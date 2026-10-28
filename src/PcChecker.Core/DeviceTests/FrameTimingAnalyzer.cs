namespace PcChecker.Core.DeviceTests;

/// <summary>
/// Frame timing for the monitor motion test. It is fed the time stamp of every frame the app draws
/// (CompositionTarget.Rendering) and works out the frames per second over the last second and how many frames were
/// skipped: a gap of about two refresh intervals means one frame was missed.
/// </summary>
public sealed class FrameTimingAnalyzer
{
    /// <summary>A gap longer than this many refresh intervals counts as skipped frames.</summary>
    public const double SkipFactor = 1.5;

    private readonly Queue<double> _window = new();
    private double? _last;

    public FrameTimingAnalyzer(double refreshHz)
    {
        RefreshHz = refreshHz > 0 ? refreshHz : 60;
    }

    public double RefreshHz { get; }

    public double ExpectedIntervalMs => 1000.0 / RefreshHz;

    /// <summary>Frames drawn during the last second.</summary>
    public int FramesPerSecond => _window.Count;

    /// <summary>Frames skipped since the start (or the last <see cref="Reset"/>).</summary>
    public int SkippedFrames { get; private set; }

    public int TotalFrames { get; private set; }

    /// <summary>
    /// Adds a frame. Time stamps that are not later than the previous one are ignored, because WPF can raise the
    /// Rendering event more than once for the same frame.
    /// </summary>
    public bool AddFrame(double timestampMs)
    {
        if (_last is { } previous && timestampMs <= previous)
        {
            return false;
        }

        if (_last is { } before)
        {
            SkippedFrames += SkippedIn(timestampMs - before, ExpectedIntervalMs);
        }

        _last = timestampMs;
        TotalFrames++;
        _window.Enqueue(timestampMs);
        while (_window.Count > 0 && _window.Peek() <= timestampMs - 1000)
        {
            _window.Dequeue();
        }

        return true;
    }

    public void Reset()
    {
        _window.Clear();
        _last = null;
        SkippedFrames = 0;
        TotalFrames = 0;
    }

    /// <summary>How many frames a gap of <paramref name="intervalMs"/> skipped at a refresh interval of <paramref name="expectedMs"/>.</summary>
    public static int SkippedIn(double intervalMs, double expectedMs)
    {
        if (expectedMs <= 0 || intervalMs <= expectedMs * SkipFactor)
        {
            return 0;
        }

        // Very long gaps (the window was moved, the PC paused) are not the monitor's fault; cap them.
        return Math.Min(30, Math.Max(1, (int)Math.Round(intervalMs / expectedMs) - 1));
    }
}
