namespace PcChecker.Core.Gaming;

/// <summary>Frame-rate figures computed from present timestamps.</summary>
public readonly record struct FrameStatsSnapshot(double Fps, double FrameTimeMs, double? OnePercentLowFps, int FramesInWindow);

/// <summary>
/// Turns a stream of present timestamps (milliseconds, any origin, increasing) into FPS over a sliding 1-second
/// window, the average frame time in that window and the "1 % low" over the last 30 seconds (the average FPS of the
/// slowest 1 % of frames). A pause longer than 2 seconds (loading screen, minimised game) starts the history again,
/// and a snapshot is only produced while frames arrived in the last 2 seconds. Pure and not thread-safe: callers lock.
/// </summary>
public sealed class FrameStats
{
    /// <summary>FPS and frame time are averaged over this window.</summary>
    public const double FpsWindowMs = 1000;

    /// <summary>The 1 % low looks back this far.</summary>
    public const double LowWindowMs = 30_000;

    /// <summary>No present for this long means "no data" (the overlay shows "--").</summary>
    public const double StaleAfterMs = 2000;

    /// <summary>A gap between two presents longer than this resets the history.</summary>
    public const double GapResetMs = 2000;

    /// <summary>Fewer frame intervals than this in the 30-second window gives no 1 % low yet.</summary>
    public const int MinIntervalsForLow = 30;

    private readonly Queue<double> _timestamps = new();
    private double? _last;

    /// <summary>Timestamps currently kept (at most the last 30 seconds).</summary>
    public int Count => _timestamps.Count;

    /// <summary>Timestamp of the most recent present, or null when there is none.</summary>
    public double? LastTimestampMs => _last;

    /// <summary>Adds one present. Out-of-order timestamps are ignored; a long gap resets the history first.</summary>
    public void Add(double timestampMs)
    {
        if (double.IsNaN(timestampMs) || double.IsInfinity(timestampMs))
        {
            return;
        }

        if (_last is { } last)
        {
            if (timestampMs < last)
            {
                return;
            }

            if (timestampMs - last > GapResetMs)
            {
                Reset();
            }
        }

        _timestamps.Enqueue(timestampMs);
        _last = timestampMs;
        Trim(timestampMs);
    }

    /// <summary>Forgets every frame (used when the tracked process changes).</summary>
    public void Reset()
    {
        _timestamps.Clear();
        _last = null;
    }

    /// <summary>True when a present arrived within <see cref="StaleAfterMs"/> of <paramref name="nowMs"/>.</summary>
    public bool HasRecentFrames(double nowMs) => _last is { } last && nowMs - last <= StaleAfterMs;

    /// <summary>The current figures, or null when there is not enough recent data.</summary>
    public FrameStatsSnapshot? Snapshot(double nowMs)
    {
        if (!HasRecentFrames(nowMs) || _timestamps.Count < 2)
        {
            return null;
        }

        Trim(Math.Max(nowMs, _last!.Value));
        var times = _timestamps.ToArray();
        if (times.Length < 2)
        {
            return null;
        }

        // FPS: frames in the last second (ending now), averaged over the span they cover.
        double windowStart = nowMs - FpsWindowMs;
        int first = Array.FindIndex(times, t => t > windowStart);
        double fps;
        double frameTime;
        int inWindow = first < 0 ? 0 : times.Length - first;
        if (inWindow >= 2 && times[^1] - times[first] > 0)
        {
            frameTime = (times[^1] - times[first]) / (inWindow - 1);
            fps = 1000.0 / frameTime;
        }
        else
        {
            // Slower than 1 frame per second inside the window: use the last interval.
            frameTime = times[^1] - times[^2];
            if (frameTime <= 0)
            {
                return null;
            }

            fps = 1000.0 / frameTime;
        }

        return new FrameStatsSnapshot(fps, frameTime, OnePercentLow(times), inWindow);
    }

    /// <summary>Average FPS of the slowest 1 % of frame intervals (at least one interval), or null with too few frames.</summary>
    public static double? OnePercentLow(IReadOnlyList<double> timestampsMs)
    {
        ArgumentNullException.ThrowIfNull(timestampsMs);
        int intervals = timestampsMs.Count - 1;
        if (intervals < MinIntervalsForLow)
        {
            return null;
        }

        var frameTimes = new double[intervals];
        for (int i = 0; i < intervals; i++)
        {
            frameTimes[i] = timestampsMs[i + 1] - timestampsMs[i];
        }

        Array.Sort(frameTimes);
        int slowest = Math.Max(1, (int)Math.Ceiling(intervals / 100.0));
        double sum = 0;
        for (int i = intervals - slowest; i < intervals; i++)
        {
            sum += frameTimes[i];
        }

        double average = sum / slowest;
        return average > 0 ? 1000.0 / average : null;
    }

    private void Trim(double nowMs)
    {
        double cutoff = nowMs - LowWindowMs;
        while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
        {
            _timestamps.Dequeue();
        }
    }
}
