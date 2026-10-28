namespace PcChecker.Core.AppHistory;

/// <summary>One reading of a running process for the recorder: its identity, app and cumulative counters.</summary>
public readonly record struct AppProcessReading(int Pid, DateTimeOffset? StartTime, string AppKey, TimeSpan CpuTime, long ReadBytes, long WriteBytes);

/// <summary>
/// The math behind PC Checker's own recording. Windows only reports each process's total processor time and disk
/// bytes since it started, so the recorder reads them once a minute and adds up the difference per app:
/// <list type="bullet">
/// <item>A process seen at the previous reading adds what it used since then.</item>
/// <item>A process that started after the previous reading adds everything it used so far (all of it is new).</item>
/// <item>A process that was already running at the first reading, or could not be read before, only sets its starting
/// point: PC Checker cannot know when that earlier use happened.</item>
/// <item>A process that ended between two readings loses its last minute, at most.</item>
/// </list>
/// Counters that go backwards (a reused process id) count as zero, and processor time is capped at what the elapsed
/// time allows on every core. <see cref="Flush"/> hands out the totals per app for the period and starts a new one.
/// Not thread-safe; the recorder calls it from one timer.
/// </summary>
public sealed class AppUsageAccumulator
{
    private Dictionary<string, Mark> _marks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastReading;
    private DateTimeOffset? _periodStart;

    /// <summary>When the current period started, or null before the first reading.</summary>
    public DateTimeOffset? PeriodStart => _periodStart;

    /// <summary>Number of apps with usage waiting to be written.</summary>
    public int PendingApps => _pending.Count;

    /// <summary>Adds one reading of every process taken at <paramref name="now"/>.</summary>
    public void Add(IEnumerable<AppProcessReading> readings, DateTimeOffset now, int logicalProcessors)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var previous = _lastReading;
        var marks = new Dictionary<string, Mark>(StringComparer.Ordinal);
        double maxCpuSeconds = previous is { } last && logicalProcessors > 0
            ? Math.Max(0, (now - last).TotalSeconds) * logicalProcessors * 1.02
            : double.MaxValue;

        foreach (var reading in readings)
        {
            if (string.IsNullOrWhiteSpace(reading.AppKey) || reading.Pid <= 0)
            {
                continue;
            }

            var key = ProcessKeyOf(reading);
            var mark = new Mark(reading.CpuTime, reading.ReadBytes, reading.WriteBytes);
            marks[key] = mark;
            if (previous is not { } since || now <= since)
            {
                continue;
            }

            Mark baseline;
            if (_marks.TryGetValue(key, out var earlier))
            {
                baseline = earlier;
            }
            else if (reading.StartTime is { } started && started >= since)
            {
                baseline = default;
            }
            else
            {
                continue;
            }

            double cpu = Math.Min(Math.Max(0, (reading.CpuTime - baseline.Cpu).TotalSeconds), maxCpuSeconds);
            long read = Math.Max(0, reading.ReadBytes - baseline.Read);
            long write = Math.Max(0, reading.WriteBytes - baseline.Write);
            if (cpu <= 0 && read <= 0 && write <= 0)
            {
                continue;
            }

            if (!_pending.TryGetValue(reading.AppKey, out var pending))
            {
                _pending[reading.AppKey] = pending = new Pending();
            }

            pending.Cpu += cpu;
            pending.Read += read;
            pending.Write += write;
        }

        _marks = marks;
        if (previous is null || now > previous)
        {
            _lastReading = now;
        }

        _periodStart ??= now;
    }

    /// <summary>
    /// The usage added since the last flush, one record per app (covering the period from its start to
    /// <paramref name="now"/>), and starts a new period. Empty when nothing was used.
    /// </summary>
    public IReadOnlyList<AppUsageRecord> Flush(DateTimeOffset now)
    {
        if (_periodStart is not { } start)
        {
            return [];
        }

        var end = now < start ? start : now;
        var records = _pending
            .Where(p => p.Value.Cpu > 0 || p.Value.Read > 0 || p.Value.Write > 0)
            .Select(p => new AppUsageRecord(p.Key, start, end, Math.Round(p.Value.Cpu, 3), null, null, null, p.Value.Read, p.Value.Write))
            .OrderBy(r => r.AppKey, StringComparer.Ordinal)
            .ToList();
        _pending.Clear();
        _periodStart = end;
        return records;
    }

    /// <summary>Forgets every starting point (after recording was paused), so the next reading is a new baseline.</summary>
    public void Reset()
    {
        _marks.Clear();
        _pending.Clear();
        _lastReading = null;
        _periodStart = null;
    }

    /// <summary>Process ids are reused, so a run of a program is its id plus its start time.</summary>
    private static string ProcessKeyOf(AppProcessReading reading) =>
        reading.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + (reading.StartTime?.UtcTicks ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private readonly record struct Mark(TimeSpan Cpu, long Read, long Write);

    private sealed class Pending
    {
        public double Cpu;
        public long Read;
        public long Write;
    }
}
