namespace PcChecker.Core.LiveUsage;

/// <summary>
/// A fixed-size history of readings (the last minute of a graph). Adding to a full ring drops the oldest reading.
/// Not thread-safe; the Live usage page only touches it from the UI thread.
/// </summary>
public sealed class SampleRing
{
    private readonly double[] _values;
    private int _start;

    public SampleRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _values = new double[capacity];
    }

    public int Capacity => _values.Length;

    public int Count { get; private set; }

    /// <summary>The newest reading, or 0 when the ring is empty.</summary>
    public double Latest => Count == 0 ? 0 : this[Count - 1];

    /// <summary>Reading <paramref name="index"/>, where 0 is the oldest one kept.</summary>
    public double this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return _values[(_start + index) % _values.Length];
        }
    }

    /// <summary>Adds a reading; NaN and infinity are stored as 0 so a graph never breaks.</summary>
    public void Add(double value)
    {
        if (!double.IsFinite(value))
        {
            value = 0;
        }

        if (Count < _values.Length)
        {
            _values[(_start + Count) % _values.Length] = value;
            Count++;
        }
        else
        {
            _values[_start] = value;
            _start = (_start + 1) % _values.Length;
        }
    }

    public void Clear()
    {
        _start = 0;
        Count = 0;
    }

    /// <summary>The highest reading kept, or 0 when empty.</summary>
    public double Max()
    {
        double max = 0;
        for (int i = 0; i < Count; i++)
        {
            max = Math.Max(max, this[i]);
        }

        return max;
    }

    /// <summary>
    /// The readings oldest first, padded at the front with NaN up to <see cref="Capacity"/> so the newest reading is always
    /// at the right edge of a graph (the graph skips the NaN slots).
    /// </summary>
    public double[] ToPaddedArray()
    {
        var result = new double[_values.Length];
        int pad = _values.Length - Count;
        for (int i = 0; i < pad; i++)
        {
            result[i] = double.NaN;
        }

        for (int i = 0; i < Count; i++)
        {
            result[pad + i] = this[i];
        }

        return result;
    }
}
