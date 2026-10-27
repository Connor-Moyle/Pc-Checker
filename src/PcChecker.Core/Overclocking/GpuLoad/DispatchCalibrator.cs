namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>
/// Pure dispatch-time calibration: starting from a small iteration count it scales the work per element until one
/// dispatch takes about the target time (50–100 ms), so the card is fully busy while every dispatch stays far below
/// the Windows driver timeout (TDR, 2 s). The first measurement is treated as warm-up (shader and buffer setup).
/// </summary>
public sealed class DispatchCalibrator
{
    public const int DefaultMinIterations = 1;
    public const int DefaultMaxIterations = 1 << 20;
    public const int DefaultMaxRounds = 12;
    public const double Tolerance = 0.25;

    /// <summary>Above this a dispatch is getting close to the driver timeout and the work is cut hard at once.</summary>
    public const double DangerMs = 500;

    private readonly double _targetMs;
    private readonly int _min;
    private readonly int _max;
    private readonly int _maxRounds;
    private bool _warmedUp;

    public DispatchCalibrator(int initialIterations, double targetMs, int minIterations = DefaultMinIterations, int maxIterations = DefaultMaxIterations, int maxRounds = DefaultMaxRounds)
    {
        if (targetMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMs));
        }

        _targetMs = targetMs;
        _min = Math.Max(1, minIterations);
        _max = Math.Max(_min, maxIterations);
        _maxRounds = Math.Max(1, maxRounds);
        Iterations = Math.Clamp(initialIterations, _min, _max);
    }

    /// <summary>Iterations to use for the next dispatch.</summary>
    public int Iterations { get; private set; }

    /// <summary>True once a dispatch landed within ±25 % of the target, or calibration ran out of rounds.</summary>
    public bool IsCalibrated { get; private set; }

    public int Rounds { get; private set; }

    /// <summary>Last measured dispatch time in milliseconds.</summary>
    public double LastMs { get; private set; }

    /// <summary>Feeds one measured dispatch time and returns the iteration count for the next dispatch.</summary>
    public int Observe(double measuredMs)
    {
        LastMs = measuredMs;

        // A dangerously slow dispatch is acted on even during warm-up.
        if (!_warmedUp && measuredMs < DangerMs)
        {
            _warmedUp = true;
            return Iterations;
        }

        _warmedUp = true;
        if (IsCalibrated && measuredMs < DangerMs)
        {
            return Iterations;
        }

        Rounds++;
        if (WithinTolerance(measuredMs, _targetMs))
        {
            IsCalibrated = true;
            return Iterations;
        }

        int next = NextIterations(Iterations, measuredMs, _targetMs, _min, _max);
        bool stuck = next == Iterations;
        Iterations = next;
        if (Rounds >= _maxRounds || stuck)
        {
            IsCalibrated = true;
        }

        return Iterations;
    }

    /// <summary>True when <paramref name="measuredMs"/> is within ±<paramref name="tolerance"/> of the target.</summary>
    public static bool WithinTolerance(double measuredMs, double targetMs, double tolerance = Tolerance) =>
        measuredMs > 0 && Math.Abs(measuredMs - targetMs) <= targetMs * tolerance;

    /// <summary>
    /// Scales the iteration count by target/measured. Growth is limited to ×8 per step so a noisy fast sample cannot
    /// overshoot into a timeout; shrinking is unlimited when a dispatch is dangerously slow and ÷4 otherwise.
    /// </summary>
    public static int NextIterations(int current, double measuredMs, double targetMs, int min = DefaultMinIterations, int max = DefaultMaxIterations)
    {
        current = Math.Max(1, current);
        double ratio = measuredMs <= 0.01 || double.IsNaN(measuredMs) ? 8 : targetMs / measuredMs;
        double minRatio = measuredMs >= DangerMs ? 0 : 0.25;
        ratio = Math.Clamp(ratio, minRatio, 8);
        double next = Math.Round(current * ratio);
        return (int)Math.Clamp(next, Math.Max(1, min), Math.Max(min, max));
    }
}

/// <summary>Pure sliding-window rate counter (dispatches per second over the last couple of seconds).</summary>
public sealed class RateMeter
{
    private readonly Queue<(double Seconds, long Count)> _samples = new();
    private readonly double _windowSeconds;

    public RateMeter(double windowSeconds = 2)
    {
        _windowSeconds = Math.Max(0.1, windowSeconds);
    }

    /// <summary>Records that the running total reached <paramref name="count"/> at <paramref name="elapsedSeconds"/>.</summary>
    public void Add(double elapsedSeconds, long count)
    {
        _samples.Enqueue((elapsedSeconds, count));
        while (_samples.Count > 2 && elapsedSeconds - _samples.Peek().Seconds > _windowSeconds)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>Events per second across the window, 0 until two samples exist.</summary>
    public double Rate
    {
        get
        {
            if (_samples.Count < 2)
            {
                return 0;
            }

            var first = _samples.Peek();
            var last = _samples.Last();
            double seconds = last.Seconds - first.Seconds;
            return seconds <= 0 ? 0 : (last.Count - first.Count) / seconds;
        }
    }
}
