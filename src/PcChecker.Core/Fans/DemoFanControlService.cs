using PcChecker.Core.Common;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.Fans;

/// <summary>
/// Demo fans for the sample desktop: CPU cooler, two case fans on the motherboard chip and the GPU fans. All
/// controllable. Pure .NET: speeds follow the chosen mode with a little wobble, curves follow the demo sensors
/// when a sensor service is supplied (58 °C otherwise), and the same alerts/safety path as the real service exists.
/// </summary>
public sealed class DemoFanControlService : IFanControlService, IFanControlAdvanced, IDisposable
{
    private const string CpuSensor = "cpu.package";
    private const string GpuSensor = "gpu.core";
    private static readonly string[] CpuFans = ["fan.cpu", "fan.case.front", "fan.case.rear"];
    private static readonly string[] GpuFans = ["fan.gpu"];

    private readonly object _gate = new();
    private readonly ISensorService? _sensors;
    private readonly Random _random = DemoData.CreateRandom(Environment.TickCount / 1000);
    private readonly List<FanControlAlert> _alerts = [];
    private readonly Dictionary<string, FanState> _state = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fan.cpu"] = new("CPU cooler", HardwareKind.Motherboard, "Nuvoton NCT6798D", 1800, 42),
        ["fan.case.front"] = new("Case fan (front)", HardwareKind.Motherboard, "Nuvoton NCT6798D", 1500, 35),
        ["fan.case.rear"] = new("Case fan (rear)", HardwareKind.Motherboard, "Nuvoton NCT6798D", 1500, 38),
        ["fan.gpu"] = new("GPU fans", HardwareKind.Gpu, DemoData.GpuName, 2600, 46),
    };

    private Timer? _timer;
    private bool _disposed;

    public DemoFanControlService()
        : this(null)
    {
    }

    public DemoFanControlService(ISensorService? sensors)
    {
        _sensors = sensors;
    }

    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public FanProfile? ActiveProfile { get; private set; }
    public IReadOnlyList<FanProfile> BuiltInProfiles { get; } = FanCurveMath.BuiltInProfiles(CpuSensor, GpuSensor, CpuFans, GpuFans);

    public IReadOnlyList<string> CapabilityNotes { get; } =
    [
        "Motherboard fan headers (CPU and case fans): Nuvoton NCT6798D on " + DemoData.MotherboardName + " — 3 controllable",
        "GPU fans: " + DemoData.GpuName + " via the NVIDIA driver — 1 controllable",
    ];

    public IReadOnlyList<FanControlAlert> Alerts
    {
        get
        {
            lock (_gate)
            {
                return _alerts.ToList();
            }
        }
    }

    public IReadOnlyList<string> SoftwareControlledFanIds
    {
        get
        {
            lock (_gate)
            {
                return _state.Where(kv => kv.Value.Mode != FanMode.Auto).Select(kv => kv.Key).ToList();
            }
        }
    }

    public event EventHandler<IReadOnlyList<FanDevice>>? FansUpdated;
    public event EventHandler<FanControlAlert>? AlertRaised;

    public async Task<IReadOnlyList<FanDevice>> GetFansAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        EnsureTimer();
        var fans = Build();
        FansUpdated?.Invoke(this, fans);
        return fans;
    }

    public Task<OperationResult> SetManualAsync(string fanId, double percent, CancellationToken cancellationToken) =>
        SetManualAsync(fanId, percent, false, cancellationToken);

    public async Task<OperationResult> SetManualAsync(string fanId, double percent, bool allowBelowMinimum, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 200).ConfigureAwait(false);
        double clamped = FanSafetyRules.ClampManual(percent, 20, allowBelowMinimum);
        lock (_gate)
        {
            if (!_state.TryGetValue(fanId, out var s))
            {
                return OperationResult.Fail($"Unknown fan \"{fanId}\".");
            }

            s.Mode = FanMode.Manual;
            s.Percent = clamped;
            s.Curve = null;
        }

        EnsureTimer();
        FansUpdated?.Invoke(this, Build());
        return OperationResult.Ok($"{Name(fanId)} set to {clamped:0} % (demo).");
    }

    public async Task<OperationResult> SetAutoAsync(string fanId, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 200).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_state.TryGetValue(fanId, out var s))
            {
                return OperationResult.Fail($"Unknown fan \"{fanId}\".");
            }

            s.Mode = FanMode.Auto;
            s.Curve = null;
            s.Percent = s.AutoPercent;
        }

        FansUpdated?.Invoke(this, Build());
        return OperationResult.Ok($"{Name(fanId)} returned to automatic control (demo).");
    }

    public async Task<OperationResult> SetCurveAsync(string fanId, FanCurve curve, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(curve);
        await DemoData.DelayAsync(cancellationToken, 50, 200).ConfigureAwait(false);
        var problem = FanCurveMath.Validate(curve);
        if (problem is not null)
        {
            return OperationResult.Fail(problem);
        }

        lock (_gate)
        {
            if (!_state.TryGetValue(fanId, out var s))
            {
                return OperationResult.Fail($"Unknown fan \"{fanId}\".");
            }

            s.Mode = FanMode.Curve;
            s.Curve = FanCurveMath.Clone(curve);
            s.Curve.Points = FanCurveMath.Normalize(s.Curve.Points);
            s.Percent = FanCurveMath.Evaluate(s.Curve, ReadTemperature(s.Curve.TemperatureSensorId) ?? 58, null);
        }

        EnsureTimer();
        FansUpdated?.Invoke(this, Build());
        return OperationResult.Ok($"{Name(fanId)} now follows its curve (demo).");
    }

    public async Task<OperationResult> ApplyProfileAsync(FanProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        int applied = 0;
        var problems = new List<string>();
        lock (_gate)
        {
            foreach (var (id, assignment) in profile.Fans)
            {
                if (!_state.TryGetValue(id, out var s))
                {
                    continue;
                }

                switch (assignment.Mode)
                {
                    case FanMode.Manual:
                        s.Mode = FanMode.Manual;
                        s.Curve = null;
                        s.Percent = FanSafetyRules.ClampManual(assignment.ManualPercent ?? s.Percent, 20, false);
                        break;
                    case FanMode.Curve when assignment.Curve is not null:
                        var problem = FanCurveMath.Validate(assignment.Curve);
                        if (problem is not null)
                        {
                            problems.Add($"{s.Name}: {problem}");
                            continue;
                        }

                        s.Mode = FanMode.Curve;
                        s.Curve = FanCurveMath.Clone(assignment.Curve);
                        s.Percent = FanCurveMath.Evaluate(s.Curve, ReadTemperature(s.Curve.TemperatureSensorId) ?? 58, null);
                        break;
                    default:
                        s.Mode = FanMode.Auto;
                        s.Curve = null;
                        s.Percent = s.AutoPercent;
                        break;
                }

                applied++;
            }
        }

        ActiveProfile = FanCurveMath.Clone(profile);
        EnsureTimer();
        FansUpdated?.Invoke(this, Build());
        if (applied == 0)
        {
            return OperationResult.Fail($"Profile \"{profile.Name}\" does not mention any fan on this PC.");
        }

        return problems.Count == 0
            ? OperationResult.Ok($"Profile \"{profile.Name}\" applied to {applied} fan(s) (demo).")
            : OperationResult.Fail($"Profile \"{profile.Name}\" applied to {applied} fan(s); problems: {string.Join("; ", problems)}");
    }

    public async Task<OperationResult> RevertAllToAutoAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 200).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var s in _state.Values)
            {
                s.Mode = FanMode.Auto;
                s.Curve = null;
                s.Percent = s.AutoPercent;
            }
        }

        ActiveProfile = null;
        FansUpdated?.Invoke(this, Build());
        return OperationResult.Ok("All fans are back under automatic control (demo).");
    }

    public IReadOnlyList<FanTemperatureSource> GetTemperatureSources()
    {
        var fromSensors = _sensors?.GetTemperatures();
        if (fromSensors is { Count: > 0 })
        {
            return fromSensors.Select(t => new FanTemperatureSource(t.Id, $"{t.HardwareName} — {t.Name}", t.Hardware, t.Value)).ToList();
        }

        return
        [
            new(CpuSensor, DemoData.CpuName + " — CPU Package", HardwareKind.Cpu, 58),
            new("cpu.core.max", DemoData.CpuName + " — Core Max", HardwareKind.Cpu, 61),
            new(GpuSensor, DemoData.GpuName + " — GPU Core", HardwareKind.Gpu, 46),
            new("gpu.hotspot", DemoData.GpuName + " — GPU Hot Spot", HardwareKind.Gpu, 58),
            new("mb.vrm", "Nuvoton NCT6798D — VRM", HardwareKind.Motherboard, 44),
            new("mb.chipset", "Nuvoton NCT6798D — Chipset", HardwareKind.Motherboard, 49),
            new("nvme.temp", DemoData.NvmeModel + " — Temperature", HardwareKind.Storage, 41),
        ];
    }

    public IReadOnlyList<RawFanControl> GetRawControls()
    {
        lock (_gate)
        {
            return _state.Select(kv => new RawFanControl(kv.Key + ".control", kv.Value.HardwareName, kv.Value.Name,
                kv.Value.Mode == FanMode.Auto ? "Default" : "Software", kv.Value.Mode == FanMode.Auto ? null : kv.Value.Percent,
                0, 100, kv.Value.Percent, kv.Key + ".rpm")).ToList();
        }
    }

    public Task<IReadOnlyList<string>> RunStartupAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public async Task ShutdownAsync()
    {
        await RevertAllToAutoAsync(CancellationToken.None).ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Simulates a hot CPU for the demo: fans get forced to full speed and an alert is raised (used by the page's "simulate" link in demo mode).</summary>
    public void SimulateOverheat()
    {
        lock (_gate)
        {
            foreach (var s in _state.Values.Where(s => s.Mode != FanMode.Auto))
            {
                s.Percent = 100;
            }
        }

        var alert = new FanControlAlert(DateTimeOffset.Now, FanAlertKind.ForcedFullSpeed, string.Empty, "All controlled fans",
            $"{DemoData.CpuName} CPU Package reached 96 °C (critical limit 95 °C). All software-controlled fans were forced to 100 % until it cools down.", Severity.High);
        lock (_gate)
        {
            _alerts.Add(alert);
        }

        AlertRaised?.Invoke(this, alert);
        FansUpdated?.Invoke(this, Build());
    }

    private void EnsureTimer()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _timer ??= new Timer(_ => Tick(), null, 1000, 1000);
        }
    }

    private void Tick()
    {
        try
        {
            lock (_gate)
            {
                foreach (var s in _state.Values.Where(s => s.Mode == FanMode.Curve && s.Curve is not null))
                {
                    double temp = ReadTemperature(s.Curve!.TemperatureSensorId) ?? 58;
                    s.Percent = FanCurveMath.Evaluate(s.Curve, temp, s.Percent);
                }
            }

            FansUpdated?.Invoke(this, Build());
        }
        catch
        {
            // Demo loop must never surface errors.
        }
    }

    private double? ReadTemperature(string sensorId)
    {
        try
        {
            return _sensors?.Latest?.Find(sensorId)?.Value;
        }
        catch
        {
            return null;
        }
    }

    private string Name(string id)
    {
        lock (_gate)
        {
            return _state.TryGetValue(id, out var s) ? s.Name : id;
        }
    }

    private IReadOnlyList<FanDevice> Build()
    {
        lock (_gate)
        {
            return _state.Select(kv =>
            {
                var s = kv.Value;
                double rpm = Math.Round(DemoData.Jitter(_random, s.MaxRpm * s.Percent / 100, 25));
                return new FanDevice(kv.Key, s.Name, s.Kind, s.HardwareName, rpm, Math.Round(s.Percent), true, 20, 100, s.Mode, kv.Key + ".control", kv.Key + ".rpm");
            }).ToList();
        }
    }

    private sealed class FanState(string name, HardwareKind kind, string hardwareName, double maxRpm, double autoPercent)
    {
        public string Name { get; } = name;
        public HardwareKind Kind { get; } = kind;
        public string HardwareName { get; } = hardwareName;
        public double MaxRpm { get; } = maxRpm;
        public double AutoPercent { get; } = autoPercent;
        public FanMode Mode { get; set; } = FanMode.Auto;
        public double Percent { get; set; } = autoPercent;
        public FanCurve? Curve { get; set; }
    }
}
