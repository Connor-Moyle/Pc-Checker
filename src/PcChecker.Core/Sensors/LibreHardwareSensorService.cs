// TODO(sensors): sensors module — LibreHardwareMonitor Computer, timer, ring buffers, thresholds.
namespace PcChecker.Core.Sensors;

/// <summary>Sensor service backed by LibreHardwareMonitor.</summary>
public sealed class LibreHardwareSensorService : ISensorService
{
    public bool IsAvailable => false;
    public string? UnavailableReason => "Sensor monitoring is not implemented yet.";
    public SensorSnapshot? Latest => null;
    public IReadOnlyList<string> SensorIds => [];

    public event EventHandler<SensorSnapshot>? SnapshotUpdated;
    public event EventHandler<TemperatureAlert>? AlertRaised;

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public IReadOnlyList<(DateTimeOffset Time, double Value)> GetHistory(string sensorId, TimeSpan span) => [];

    public IReadOnlyList<SensorReading> GetTemperatures() => [];

    internal void RaiseForCompiler()
    {
        SnapshotUpdated?.Invoke(this, SensorSnapshot.Empty);
        AlertRaised?.Invoke(this, new TemperatureAlert(string.Empty, string.Empty, 0, 0, Common.Severity.Info, DateTimeOffset.Now));
    }
}
