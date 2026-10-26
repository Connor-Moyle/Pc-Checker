using PcChecker.Core.Common;

namespace PcChecker.Core.Sensors;

/// <summary>Demo sensors: a gaming desktop under light load, updated every second with gentle wobble.</summary>
public sealed class DemoSensorService : ISensorService, IDisposable
{
    private static readonly (string Id, string HardwareName, HardwareKind Hardware, SensorKind Kind, string Name, double Base, double Amp, string Unit)[] Table =
    [
        ("cpu.package", DemoData.CpuName, HardwareKind.Cpu, SensorKind.Temperature, "CPU Package", 58, 3, "°C"),
        ("cpu.core.max", DemoData.CpuName, HardwareKind.Cpu, SensorKind.Temperature, "Core Max", 61, 4, "°C"),
        ("cpu.load", DemoData.CpuName, HardwareKind.Cpu, SensorKind.Load, "CPU Total", 14, 6, "%"),
        ("cpu.clock", DemoData.CpuName, HardwareKind.Cpu, SensorKind.Clock, "CPU Core #1", 5300, 60, "MHz"),
        ("cpu.power", DemoData.CpuName, HardwareKind.Cpu, SensorKind.Power, "CPU Package", 48, 5, "W"),
        ("gpu.core", DemoData.GpuName, HardwareKind.Gpu, SensorKind.Temperature, "GPU Core", 46, 2, "°C"),
        ("gpu.hotspot", DemoData.GpuName, HardwareKind.Gpu, SensorKind.Temperature, "GPU Hot Spot", 58, 2, "°C"),
        ("gpu.load", DemoData.GpuName, HardwareKind.Gpu, SensorKind.Load, "GPU Core", 9, 4, "%"),
        ("gpu.clock", DemoData.GpuName, HardwareKind.Gpu, SensorKind.Clock, "GPU Core", 2505, 20, "MHz"),
        ("gpu.memclock", DemoData.GpuName, HardwareKind.Gpu, SensorKind.Clock, "GPU Memory", 10501, 0, "MHz"),
        ("gpu.vram", DemoData.GpuName, HardwareKind.Gpu, SensorKind.Data, "GPU Memory Used", 2140, 40, "MB"),
        ("gpu.power", DemoData.GpuName, HardwareKind.Gpu, SensorKind.Power, "GPU Package", 42, 4, "W"),
        ("gpu.fan", DemoData.GpuName, HardwareKind.Gpu, SensorKind.Fan, "GPU Fan 1", 0, 0, "RPM"),
        ("gpu.fan.control", DemoData.GpuName, HardwareKind.Gpu, SensorKind.Control, "GPU Fan 1", 0, 0, "%"),
        ("ram.used", "Generic Memory", HardwareKind.Memory, SensorKind.Data, "Memory Used", 13.4, 0.2, "GB"),
        ("ram.load", "Generic Memory", HardwareKind.Memory, SensorKind.Load, "Memory", 42, 1, "%"),
        ("mb.vrm", "Nuvoton NCT6798D", HardwareKind.Motherboard, SensorKind.Temperature, "VRM", 44, 1, "°C"),
        ("mb.chipset", "Nuvoton NCT6798D", HardwareKind.Motherboard, SensorKind.Temperature, "Chipset", 49, 1, "°C"),
        ("fan.cpu.rpm", "Nuvoton NCT6798D", HardwareKind.Motherboard, SensorKind.Fan, "CPU Fan", 820, 25, "RPM"),
        ("fan.case.front.rpm", "Nuvoton NCT6798D", HardwareKind.Motherboard, SensorKind.Fan, "Chassis Fan #1", 640, 20, "RPM"),
        ("fan.case.rear.rpm", "Nuvoton NCT6798D", HardwareKind.Motherboard, SensorKind.Fan, "Chassis Fan #2", 655, 20, "RPM"),
        ("nvme.temp", DemoData.NvmeModel, HardwareKind.Storage, SensorKind.Temperature, "Temperature", 41, 1, "°C"),
        ("hdd.temp", DemoData.HddModel, HardwareKind.Storage, SensorKind.Temperature, "Temperature", 36, 1, "°C"),
        ("net.down", "Intel Ethernet Controller I226-V", HardwareKind.Network, SensorKind.Throughput, "Download Speed", 1.8, 1.5, "MB/s"),
        ("net.up", "Intel Ethernet Controller I226-V", HardwareKind.Network, SensorKind.Throughput, "Upload Speed", 0.3, 0.2, "MB/s"),
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, List<(DateTimeOffset Time, double Value)>> _history = new();
    private readonly Dictionary<string, (double Min, double Max)> _minMax = new();
    private readonly Random _random = DemoData.CreateRandom();
    private Timer? _timer;

    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public SensorSnapshot? Latest { get; private set; }
    public IReadOnlyList<string> SensorIds => Table.Select(t => t.Id).ToList();

    public event EventHandler<SensorSnapshot>? SnapshotUpdated;
    public event EventHandler<TemperatureAlert>? AlertRaised;

    public DemoSensorService()
    {
        Tick(null);
    }

    public void Start()
    {
        _timer ??= new Timer(Tick, null, 1000, 1000);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public IReadOnlyList<(DateTimeOffset Time, double Value)> GetHistory(string sensorId, TimeSpan span)
    {
        lock (_gate)
        {
            if (!_history.TryGetValue(sensorId, out var list))
            {
                return [];
            }

            var cutoff = DateTimeOffset.Now - span;
            return list.Where(p => p.Time >= cutoff).ToList();
        }
    }

    public IReadOnlyList<SensorReading> GetTemperatures() => Latest?.OfKind(SensorKind.Temperature).ToList() ?? [];

    public void Dispose() => Stop();

    private void Tick(object? state)
    {
        var now = DateTimeOffset.Now;
        var readings = new List<SensorReading>(Table.Length);
        lock (_gate)
        {
            foreach (var t in Table)
            {
                double value = Math.Round(Math.Max(0, DemoData.Jitter(_random, t.Base, t.Amp)), 1);
                var (min, max) = _minMax.TryGetValue(t.Id, out var mm) ? (Math.Min(mm.Min, value), Math.Max(mm.Max, value)) : (value, value);
                _minMax[t.Id] = (min, max);
                if (!_history.TryGetValue(t.Id, out var list))
                {
                    _history[t.Id] = list = [];
                }

                list.Add((now, value));
                if (list.Count > 3600)
                {
                    list.RemoveAt(0);
                }

                readings.Add(new SensorReading(t.Id, t.HardwareName, t.Hardware, t.Kind, t.Name, value, min, max, t.Unit));
            }
        }

        Latest = new SensorSnapshot(now, readings);
        SnapshotUpdated?.Invoke(this, Latest);
    }

    internal void RaiseAlertForCompiler() =>
        AlertRaised?.Invoke(this, new TemperatureAlert("cpu.package", DemoData.CpuName, 0, 0, Severity.Info, DateTimeOffset.Now));
}
