namespace PcChecker.Core.DeviceTests;

/// <summary>The mouse inputs the test shows.</summary>
public enum MouseTestInput
{
    Left,
    Right,
    Middle,
    Back,
    Forward,
    WheelUp,
    WheelDown,
    TiltLeft,
    TiltRight,
}

/// <summary>What one click told us about its timing.</summary>
public sealed record ClickTiming(MouseTestInput Button, double? IntervalMs, bool IsDoubleClick, bool IsChatter);

/// <summary>Whether the polling rate stayed even while the mouse moved.</summary>
public enum SmoothnessVerdict
{
    Unknown,
    Steady,
    Uneven,
}

/// <summary>
/// Click timing for the mouse test: the time between two clicks of the same button, whether Windows would see a
/// double-click, and "chatter": two clicks closer together than a finger can manage, the sign of a worn switch that
/// double-clicks by itself.
/// </summary>
public sealed class ClickTimingAnalyzer
{
    /// <summary>Two clicks closer than this are almost certainly one press registered twice.</summary>
    public const double ChatterThresholdMs = 60;

    private readonly Dictionary<MouseTestInput, double> _lastClick = [];
    private readonly Dictionary<MouseTestInput, int> _chatter = [];

    /// <summary>Records a button press at <paramref name="timestampMs"/>.</summary>
    public ClickTiming Register(MouseTestInput button, double timestampMs, int doubleClickLimitMs)
    {
        double? interval = null;
        if (_lastClick.TryGetValue(button, out double last) && timestampMs >= last)
        {
            interval = timestampMs - last;
        }

        _lastClick[button] = timestampMs;
        bool chatter = interval is { } gap && gap < ChatterThresholdMs;
        if (chatter)
        {
            _chatter[button] = ChatterCount(button) + 1;
        }

        bool isDouble = interval is { } i && i <= Math.Max(1, doubleClickLimitMs);
        return new ClickTiming(button, interval, isDouble, chatter);
    }

    public int ChatterCount(MouseTestInput button) => _chatter.TryGetValue(button, out int count) ? count : 0;

    public void Reset()
    {
        _lastClick.Clear();
        _chatter.Clear();
    }
}

/// <summary>Pure helpers for the mouse test.</summary>
public static class MouseTestMath
{
    /// <summary>Readings that vary more than this (coefficient of variation) count as uneven.</summary>
    public const double UnevenVariation = 0.25;

    /// <summary>
    /// Judges how even the live polling-rate readings were while the mouse moved. The first two readings (the user
    /// starting to move) and zero readings (the mouse standing still) are ignored.
    /// </summary>
    public static SmoothnessVerdict Smoothness(IReadOnlyList<int> liveHz)
    {
        var moving = (liveHz ?? []).Skip(2).Where(v => v > 0).Select(v => (double)v).ToList();
        if (moving.Count < 5)
        {
            return SmoothnessVerdict.Unknown;
        }

        double mean = moving.Average();
        if (mean <= 0)
        {
            return SmoothnessVerdict.Unknown;
        }

        double sd = Math.Sqrt(moving.Sum(v => (v - mean) * (v - mean)) / moving.Count);
        return sd / mean > UnevenVariation ? SmoothnessVerdict.Uneven : SmoothnessVerdict.Steady;
    }
}
