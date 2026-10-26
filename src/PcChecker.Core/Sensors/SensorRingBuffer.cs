namespace PcChecker.Core.Sensors;

/// <summary>
/// Fixed-size history of one sensor: timestamped samples in a circular buffer plus the session minimum and
/// maximum (which can be reset without losing the history). Not thread-safe; callers hold their own lock.
/// </summary>
public sealed class SensorRingBuffer
{
    private (DateTimeOffset Time, double Value)[] _items;
    private int _start;
    private int _count;

    public SensorRingBuffer(int capacity)
    {
        _items = new (DateTimeOffset, double)[Math.Max(1, capacity)];
    }

    public int Capacity => _items.Length;

    public int Count => _count;

    /// <summary>Lowest value since the last <see cref="ResetMinMax"/>, or null when nothing was recorded.</summary>
    public double? Min { get; private set; }

    /// <summary>Highest value since the last <see cref="ResetMinMax"/>, or null when nothing was recorded.</summary>
    public double? Max { get; private set; }

    /// <summary>The most recent value, or null when empty.</summary>
    public double? Last => _count == 0 ? null : _items[(_start + _count - 1) % _items.Length].Value;

    /// <summary>Adds a sample; NaN and infinities are ignored.</summary>
    public void Add(DateTimeOffset time, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return;
        }

        if (_count == _items.Length)
        {
            _items[_start] = (time, value);
            _start = (_start + 1) % _items.Length;
        }
        else
        {
            _items[(_start + _count) % _items.Length] = (time, value);
            _count++;
        }

        Min = Min is { } min ? Math.Min(min, value) : value;
        Max = Max is { } max ? Math.Max(max, value) : value;
    }

    /// <summary>Samples newer than <paramref name="now"/> − <paramref name="span"/>, oldest first.</summary>
    public IReadOnlyList<(DateTimeOffset Time, double Value)> Window(TimeSpan span, DateTimeOffset now)
    {
        if (span <= TimeSpan.Zero)
        {
            return [];
        }

        // Guard against "everything" windows that would underflow the clock.
        var cutoff = span >= now - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : now - span;
        var result = new List<(DateTimeOffset, double)>(_count);
        for (int i = 0; i < _count; i++)
        {
            var item = _items[(_start + i) % _items.Length];
            if (item.Time >= cutoff)
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>Every sample, oldest first.</summary>
    public IReadOnlyList<(DateTimeOffset Time, double Value)> All()
    {
        var result = new List<(DateTimeOffset, double)>(_count);
        for (int i = 0; i < _count; i++)
        {
            result.Add(_items[(_start + i) % _items.Length]);
        }

        return result;
    }

    /// <summary>Starts the min/max tracking again from the next sample (the history itself is kept).</summary>
    public void ResetMinMax()
    {
        Min = null;
        Max = null;
        if (Last is { } last)
        {
            Min = last;
            Max = last;
        }
    }

    /// <summary>Changes the capacity, keeping the newest samples.</summary>
    public void Resize(int capacity)
    {
        capacity = Math.Max(1, capacity);
        if (capacity == _items.Length)
        {
            return;
        }

        var kept = All();
        var items = new (DateTimeOffset, double)[capacity];
        int skip = Math.Max(0, kept.Count - capacity);
        int n = 0;
        for (int i = skip; i < kept.Count; i++)
        {
            items[n++] = kept[i];
        }

        _items = items;
        _start = 0;
        _count = n;
    }

    /// <summary>How many samples fit in <paramref name="historyMinutes"/> at one sample every <paramref name="pollIntervalMs"/>.</summary>
    public static int CapacityFor(int historyMinutes, int pollIntervalMs)
    {
        int minutes = Math.Clamp(historyMinutes, 1, 24 * 60);
        int interval = Math.Clamp(pollIntervalMs, 100, 60_000);
        long samples = (long)minutes * 60_000 / interval;
        return (int)Math.Clamp(samples, 60, 50_000);
    }
}
