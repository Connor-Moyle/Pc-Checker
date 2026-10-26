namespace PcChecker.Core.Sensors;

/// <summary>Live hardware sensors (temperatures, loads, clocks, power, fans) with short history.</summary>
public interface ISensorService
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    SensorSnapshot? Latest { get; }
    void Start();
    void Stop();
    event EventHandler<SensorSnapshot>? SnapshotUpdated;
    event EventHandler<TemperatureAlert>? AlertRaised;
    IReadOnlyList<(DateTimeOffset Time, double Value)> GetHistory(string sensorId, TimeSpan span);
    IReadOnlyList<SensorReading> GetTemperatures();
    IReadOnlyList<string> SensorIds { get; }
}
