using PcChecker.Core.Sensors;

namespace PcChecker.Core.Power;

/// <summary>
/// Session bookkeeping shared by the power services: integrates watts into energy (Wh) between samples and keeps
/// the history of total watts. Pure; not thread-safe (callers lock).
/// </summary>
public sealed class PowerSession
{
    private readonly SensorRingBuffer _history;
    private DateTimeOffset? _lastSample;

    public PowerSession(int historyCapacity = 3600, DateTimeOffset? started = null)
    {
        _history = new SensorRingBuffer(historyCapacity);
        Started = started ?? DateTimeOffset.Now;
    }

    public DateTimeOffset Started { get; private set; }

    /// <summary>Energy used since <see cref="Started"/>, in watt-hours.</summary>
    public double EnergyWh { get; private set; }

    /// <summary>Records one total-watts sample, integrating the energy since the previous one.</summary>
    public void Record(DateTimeOffset now, double? totalW)
    {
        if (_lastSample is { } last)
        {
            EnergyWh += PowerMath.IntegrateEnergyWh(totalW, now - last);
        }

        _lastSample = now;
        if (totalW is { } w && !double.IsNaN(w))
        {
            _history.Add(now, w);
        }
    }

    public IReadOnlyList<(DateTimeOffset Time, double Value)> History(TimeSpan span, DateTimeOffset now) => _history.Window(span, now);

    /// <summary>Starts the energy counter again (history is kept).</summary>
    public void Reset(DateTimeOffset now)
    {
        EnergyWh = 0;
        Started = now;
        _lastSample = now;
    }
}
