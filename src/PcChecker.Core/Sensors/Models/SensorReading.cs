using PcChecker.Core.Common;

namespace PcChecker.Core.Sensors;

/// <summary>A single sensor value with the session min/max.</summary>
public sealed record SensorReading(
    string Id,
    string HardwareName,
    HardwareKind Hardware,
    SensorKind Kind,
    string Name,
    double? Value,
    double? Min,
    double? Max,
    string Unit);

/// <summary>All readings at one point in time.</summary>
public sealed record SensorSnapshot(DateTimeOffset Timestamp, IReadOnlyList<SensorReading> Readings)
{
    public static SensorSnapshot Empty { get; } = new(DateTimeOffset.MinValue, []);

    public SensorReading? Find(string id) => Readings.FirstOrDefault(r => r.Id == id);

    public IEnumerable<SensorReading> OfKind(SensorKind kind) => Readings.Where(r => r.Kind == kind);
}

/// <summary>Raised when a temperature crosses a warning or critical threshold.</summary>
public sealed record TemperatureAlert(
    string SensorId,
    string HardwareName,
    double Value,
    double Threshold,
    Severity Severity,
    DateTimeOffset Timestamp);
