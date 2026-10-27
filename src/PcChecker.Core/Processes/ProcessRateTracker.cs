namespace PcChecker.Core.Processes;

/// <summary>CPU share and disk rates of one process over the last interval.</summary>
public readonly record struct ProcessRates(double CpuPercent, double ReadBytesPerSecond, double WriteBytesPerSecond)
{
    public static ProcessRates Zero => default;
}

/// <summary>
/// Remembers the previous reading of every process and turns cumulative counters into rates. Pure .NET: the caller
/// supplies the readings and the time. A process seen for the first time (or a reused process id, which has a new
/// start time and therefore a new key) reports zero until the next reading.
/// </summary>
public sealed class ProcessRateTracker
{
    private Dictionary<string, ProcessCounters> _previous = new(StringComparer.Ordinal);
    private DateTimeOffset? _previousTime;

    /// <summary>Number of processes remembered from the last reading.</summary>
    public int Count => _previous.Count;

    /// <summary>Computes the rates for <paramref name="current"/> and remembers it for the next call.</summary>
    public IReadOnlyDictionary<string, ProcessRates> Update(IReadOnlyCollection<ProcessCounters> current, DateTimeOffset now, int logicalProcessors)
    {
        ArgumentNullException.ThrowIfNull(current);
        var elapsed = _previousTime is { } previousTime ? now - previousTime : TimeSpan.Zero;
        var rates = new Dictionary<string, ProcessRates>(current.Count, StringComparer.Ordinal);
        var next = new Dictionary<string, ProcessCounters>(current.Count, StringComparer.Ordinal);

        foreach (var counters in current)
        {
            string key = counters.Key;
            next[key] = counters;
            if (elapsed > TimeSpan.Zero && _previous.TryGetValue(key, out var before))
            {
                rates[key] = new ProcessRates(
                    ProcessMath.CpuPercent(before.CpuTime, counters.CpuTime, elapsed, logicalProcessors),
                    ProcessMath.RatePerSecond(before.ReadBytes, counters.ReadBytes, elapsed),
                    ProcessMath.RatePerSecond(before.WriteBytes, counters.WriteBytes, elapsed));
            }
            else
            {
                rates[key] = ProcessRates.Zero;
            }
        }

        _previous = next;
        _previousTime = now;
        return rates;
    }

    /// <summary>Forgets every reading, for example after a long pause.</summary>
    public void Reset()
    {
        _previous = new Dictionary<string, ProcessCounters>(StringComparer.Ordinal);
        _previousTime = null;
    }
}
