// TODO(sensors): sensors module — LibreHardwareMonitor Computer, timer, ring buffers, thresholds, shared host.
using LibreHardwareMonitor.Hardware;

namespace PcChecker.Core.Sensors;

/// <summary>
/// Sensor service backed by LibreHardwareMonitor. Also the <see cref="IHardwareMonitorHost"/> other modules share;
/// register one instance for both interfaces.
/// </summary>
public sealed class LibreHardwareSensorService : ISensorService, IHardwareMonitorHost
{
    public bool IsAvailable => false;
    public string? UnavailableReason => "Sensor monitoring is not implemented yet.";
    public SensorSnapshot? Latest => null;
    public IReadOnlyList<string> SensorIds => [];

    public bool IsOpen => false;
    public string? OpenError => "The hardware monitor is not implemented yet.";

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

    public Task EnsureOpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public T? WithComputer<T>(Func<IComputer, T> func) => default;

    public void WithComputer(Action<IComputer> action)
    {
    }

    internal void RaiseForCompiler()
    {
        SnapshotUpdated?.Invoke(this, SensorSnapshot.Empty);
        AlertRaised?.Invoke(this, new TemperatureAlert(string.Empty, string.Empty, 0, 0, Common.Severity.Info, DateTimeOffset.Now));
    }
}
