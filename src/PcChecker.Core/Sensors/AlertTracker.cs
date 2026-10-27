using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Sensors;

/// <summary>
/// Decides when a temperature alert should be raised again. Pure state machine, one entry per sensor:
/// an alert is raised when a reading first crosses the warning or critical threshold, when it escalates from
/// warning to critical, or when it stays above the threshold for longer than <see cref="RepeatAfter"/>.
/// After an alert, the sensor must drop at least <see cref="RearmDeltaC"/> below the threshold before the same
/// level alerts again, so a value hovering around the line does not spam the user.
/// </summary>
public sealed class AlertTracker
{
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    public AlertTracker(double rearmDeltaC = 3, TimeSpan? repeatAfter = null)
    {
        RearmDeltaC = Math.Max(0, rearmDeltaC);
        RepeatAfter = repeatAfter ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>How far below the threshold a sensor must fall before it can alert again (°C).</summary>
    public double RearmDeltaC { get; }

    /// <summary>How long a sensor may stay above a threshold before the alert is repeated.</summary>
    public TimeSpan RepeatAfter { get; }

    /// <summary>Number of sensors currently above a threshold.</summary>
    public int ActiveCount => _states.Count;

    /// <summary>True when the sensor is currently above a threshold (an alert was raised and not yet cleared).</summary>
    public bool IsActive(string sensorId) => _states.ContainsKey(sensorId);

    /// <summary>Evaluates one reading. Returns an alert when one should be shown now, otherwise null.</summary>
    public TemperatureAlert? Evaluate(SensorReading reading, MonitoringSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(settings);

        if (reading.Kind != SensorKind.Temperature || reading.Value is not { } value || double.IsNaN(value))
        {
            return null;
        }

        if (SensorMath.ThresholdsFor(reading, settings) is not { } thresholds)
        {
            return null;
        }

        return Evaluate(reading.Id, reading.HardwareName, value, thresholds.Warn, thresholds.Crit, now);
    }

    /// <summary>Evaluates a raw value against explicit thresholds.</summary>
    public TemperatureAlert? Evaluate(string sensorId, string hardwareName, double value, double warn, double crit, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(sensorId);
        if (double.IsNaN(value))
        {
            return null;
        }

        var severity = SensorMath.Evaluate(value, warn, crit);
        _states.TryGetValue(sensorId, out var state);

        if (severity is null)
        {
            // Below the warning line: clear once the value has really come down.
            if (state is not null && value <= warn - RearmDeltaC)
            {
                _states.Remove(sensorId);
            }

            return null;
        }

        double threshold = severity == Severity.Critical ? crit : warn;

        if (state is null)
        {
            return Raise(sensorId, hardwareName, value, threshold, severity.Value, now);
        }

        if (severity.Value > state.Severity)
        {
            // Escalation (warning → critical) is always worth a new alert.
            return Raise(sensorId, hardwareName, value, threshold, severity.Value, now);
        }

        if (severity.Value < state.Severity && value <= state.Threshold - RearmDeltaC)
        {
            // Dropped out of critical: remember the lower level silently so a return to critical alerts again.
            _states[sensorId] = state with { Severity = severity.Value, Threshold = threshold };
            return null;
        }

        if (now - state.RaisedAt >= RepeatAfter)
        {
            return Raise(sensorId, hardwareName, value, threshold, severity.Value, now);
        }

        return null;
    }

    /// <summary>Forgets every active alert (for example after the user changes the thresholds).</summary>
    public void Reset() => _states.Clear();

    private TemperatureAlert Raise(string sensorId, string hardwareName, double value, double threshold, Severity severity, DateTimeOffset now)
    {
        _states[sensorId] = new State(severity, threshold, now);
        return new TemperatureAlert(sensorId, hardwareName, value, threshold, severity, now);
    }

    private sealed record State(Severity Severity, double Threshold, DateTimeOffset RaisedAt);
}
