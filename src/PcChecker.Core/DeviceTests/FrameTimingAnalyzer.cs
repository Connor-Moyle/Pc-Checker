namespace PcChecker.Core.DeviceTests;

/// <summary>
/// Frame timing for the monitor motion test. It is fed the time stamp of every frame the app draws
/// (CompositionTarget.Rendering) and works out the frames per second over the last second and how many frames were
/// skipped: a gap of about two refresh intervals means one frame was missed.
/// WPF draws at the desktop composition rate, which usually follows the main screen. When the tested screen is faster
/// than that (a 144 Hz second screen next to a 60 Hz main screen), the usual frame gap is longer than the screen's
/// refresh interval; skips are then counted against the usual gap instead, so the screen is not blamed for it.
/// </summary>
public sealed class FrameTimingAnalyzer
{
    /// <summary>A gap longer than this many refresh intervals counts as skipped frames.</summary>
    public const double SkipFactor = 1.5;

    /// <summary>Frame gaps needed before the usual gap is known and skips are counted.</summary>
    public const int WarmUpFrames = 30;

    /// <summary>The usual gap counts as a slower drawing clock when it is this much longer than the refresh interval.</summary>
    public const double SlowClockFactor = 1.25;

    private const int RecentGaps = 120;

    private readonly Queue<double> _window = new();
    private readonly Queue<double> _gaps = new();
    private double? _last;

    public FrameTimingAnalyzer(double refreshHz)
    {
        RefreshHz = refreshHz > 0 ? refreshHz : 60;
    }

    public double RefreshHz { get; }

    public double ExpectedIntervalMs => 1000.0 / RefreshHz;

    /// <summary>The usual (median) gap between recent frames, or null until enough frames were seen.</summary>
    public double? TypicalIntervalMs { get; private set; }

    /// <summary>
    /// True when the app is drawn at a clearly slower rate than the screen refreshes (usually because the main screen
    /// runs at a lower refresh rate); skipped frames are then counted against that slower rate.
    /// </summary>
    public bool DrawsSlowerThanScreen => TypicalIntervalMs is { } typical && typical > ExpectedIntervalMs * SlowClockFactor;

    /// <summary>The rate the app is actually drawn at, from the usual frame gap.</summary>
    public double? DrawingHz => TypicalIntervalMs is { } typical && typical > 0 ? 1000.0 / typical : null;

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
            double gap = timestampMs - before;
            if (TypicalIntervalMs is not null)
            {
                double expected = DrawsSlowerThanScreen ? TypicalIntervalMs.Value : ExpectedIntervalMs;
                SkippedFrames += SkippedIn(gap, expected);
            }

            _gaps.Enqueue(gap);
            while (_gaps.Count > RecentGaps)
            {
                _gaps.Dequeue();
            }

            if (_gaps.Count >= WarmUpFrames)
            {
                TypicalIntervalMs = Median(_gaps);
            }
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
        _gaps.Clear();
        _last = null;
        TypicalIntervalMs = null;
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

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
