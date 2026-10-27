namespace PcChecker.Core.History;

/// <summary>
/// Collects the live readings that arrive between two recorder ticks (about one per second) and turns them into one
/// sample: the peak temperature, the average load, power and memory use, the last free space. Not thread-safe; the
/// recorder locks around it.
/// </summary>
public sealed class HistorySampleAccumulator
{
    private readonly Dictionary<string, (double Sum, int Count, double Max, double Min, double Last)> _values = new(StringComparer.Ordinal);

    /// <summary>Number of readings added since the last <see cref="Flush"/>.</summary>
    public int Readings { get; private set; }

    public bool IsEmpty => _values.Count == 0;

    /// <summary>Adds one reading of <paramref name="metric"/>. Missing and non-finite values are ignored.</summary>
    public void Add(string metric, double? value)
    {
        if (value is not { } v || !double.IsFinite(v) || !HistoryMetrics.IsValidId(metric))
        {
            return;
        }

        Readings++;
        _values[metric] = _values.TryGetValue(metric, out var a)
            ? (a.Sum + v, a.Count + 1, Math.Max(a.Max, v), Math.Min(a.Min, v), v)
            : (v, 1, v, v, v);
    }

    /// <summary>
    /// Returns the combined sample stamped with <paramref name="time"/> covering <paramref name="spanSeconds"/>, or null
    /// when nothing was added, and starts over.
    /// </summary>
    public HistorySample? Flush(DateTimeOffset time, int spanSeconds)
    {
        if (_values.Count == 0)
        {
            return null;
        }

        var values = new Dictionary<string, double>(_values.Count, StringComparer.Ordinal);
        foreach (var (metric, a) in _values)
        {
            values[metric] = HistoryMetrics.AggregationOf(metric) switch
            {
                HistoryAggregation.Max => a.Max,
                HistoryAggregation.Min => a.Min,
                HistoryAggregation.Last => a.Last,
                _ => a.Sum / a.Count,
            };
        }

        _values.Clear();
        Readings = 0;
        return new HistorySample(time, Math.Max(0, spanSeconds), values);
    }
}
