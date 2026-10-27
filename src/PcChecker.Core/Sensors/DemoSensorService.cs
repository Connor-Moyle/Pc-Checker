using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Sensors;

/// <summary>
/// Demo sensors: the sample gaming desktop under light load. Every reading in <see cref="DemoSensorCatalog"/> is
/// a slow sine wave plus noise, updated once a second, with the same history, min/max and alert behaviour as the
/// real service. Pure .NET, so it runs anywhere.
/// </summary>
public sealed class DemoSensorService : ISensorService, ISensorServiceExtras, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SensorRingBuffer> _history = new(StringComparer.Ordinal);
    private readonly AlertTracker _alerts = new();
    private readonly Random _random = DemoData.CreateRandom();
    private readonly MonitoringSettings _monitoring;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private Timer? _timer;

    public DemoSensorService()
        : this(null)
    {
    }

    public DemoSensorService(ISettingsService? settings)
    {
        _monitoring = settings?.Current.Monitoring ?? new MonitoringSettings();
        int capacity = SensorRingBuffer.CapacityFor(_monitoring.HistoryMinutes, 1000);
        foreach (var spec in DemoSensorCatalog.All)
        {
            _history[spec.Id] = new SensorRingBuffer(capacity);
        }

        Tick(null);
    }

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public SensorSnapshot? Latest { get; private set; }

    public IReadOnlyList<string> SensorIds => DemoSensorCatalog.All.Select(t => t.Id).ToList();

    public IReadOnlyList<string> Warnings => [];

    public event EventHandler<SensorSnapshot>? SnapshotUpdated;

    public event EventHandler<TemperatureAlert>? AlertRaised;

    public void Start()
    {
        lock (_gate)
        {
            _timer ??= new Timer(Tick, null, 1000, 1000);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    public IReadOnlyList<(DateTimeOffset Time, double Value)> GetHistory(string sensorId, TimeSpan span)
    {
        lock (_gate)
        {
            return _history.TryGetValue(sensorId, out var buffer) ? buffer.Window(span, DateTimeOffset.Now) : [];
        }
    }

    public IReadOnlyList<SensorReading> GetTemperatures() =>
        Latest?.OfKind(SensorKind.Temperature)
            .OrderBy(r => SensorMapping.GroupOrder(r.Hardware))
            .ThenBy(r => r.HardwareName, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

    public void ResetMinMax()
    {
        lock (_gate)
        {
            foreach (var buffer in _history.Values)
            {
                buffer.ResetMinMax();
            }
        }
    }

    public void Dispose() => Stop();

    private void Tick(object? state)
    {
        var now = DateTimeOffset.Now;
        double seconds = (now - _started).TotalSeconds;
        var readings = new List<SensorReading>(DemoSensorCatalog.All.Count);
        List<TemperatureAlert>? alerts = null;
        string? cpuName = DemoSensorCatalog.All.FirstOrDefault(s => s.Hardware == HardwareKind.Cpu)?.HardwareName;

        lock (_gate)
        {
            foreach (var spec in DemoSensorCatalog.All)
            {
                double value = spec.ValueAt(seconds, _random.NextDouble() * 2 - 1);
                var buffer = _history[spec.Id];
                buffer.Add(now, value);
                var reading = new SensorReading(spec.Id, spec.HardwareName, spec.Hardware, spec.Kind, spec.Name, value, buffer.Min, buffer.Max, spec.Unit);
                readings.Add(reading);

                if (_monitoring.AlertsEnabled && _alerts.Evaluate(reading, _monitoring, now, cpuName) is { } alert)
                {
                    (alerts ??= []).Add(alert);
                }
            }
        }

        var snapshot = new SensorSnapshot(now, readings);
        Latest = snapshot;
        SnapshotUpdated?.Invoke(this, snapshot);

        if (alerts is null)
        {
            return;
        }

        foreach (var alert in alerts)
        {
            AlertRaised?.Invoke(this, alert);
        }
    }
}
