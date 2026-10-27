using PcChecker.Core.Common;
using PcChecker.Core.Sensors;
using PcChecker.Core.Sensors.PawnIo;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Fans;

/// <summary>
/// Demo fans for the sample desktop: CPU cooler, two case fans on the motherboard chip and the GPU fans (controllable),
/// a read-only CPU_OPT header and an empty header that reads 0 RPM (hidden until "Show empty headers" is on). Pure .NET: speeds follow the chosen mode with a little wobble, curves follow the demo sensors
/// when a sensor service is supplied (58 °C otherwise), and the same alerts/safety path as the real service exists.
/// </summary>
public sealed class DemoFanControlService : IFanControlService, IFanControlAdvanced, IFanDiagnostics, IDisposable
{
    private const string CpuSensor = "cpu.package";
    private const string GpuSensor = "gpu.core";
    private static readonly string[] CpuFans = ["fan.cpu", "fan.case.front", "fan.case.rear"];
    private static readonly string[] GpuFans = ["fan.gpu"];

    private const double DefaultMinimumPercent = 20;
    private readonly object _gate = new();
    private readonly ISensorService? _sensors;
    private readonly ISettingsService? _settings;
    private readonly Random _random = DemoData.CreateRandom(Environment.TickCount / 1000);
    private readonly List<FanControlAlert> _alerts = [];
    private readonly Dictionary<string, FanState> _state = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fan.cpu"] = new("CPU cooler", HardwareKind.Motherboard, "Nuvoton NCT6798D", 1800, 42),
        ["fan.case.front"] = new("Case fan (front)", HardwareKind.Motherboard, "Nuvoton NCT6798D", 1500, 35),
        ["fan.case.rear"] = new("Case fan (rear)", HardwareKind.Motherboard, "Nuvoton NCT6798D", 1500, 38),
        ["fan.cpu.opt"] = new("CPU_OPT header (pump)", HardwareKind.Motherboard, "Nuvoton NCT6798D", 3200, 72) { Controllable = false },
        ["fan.header.6"] = new("Fan 6 (motherboard header)", HardwareKind.Motherboard, "Nuvoton NCT6798D", 1500, 40) { Empty = true },
        ["fan.gpu"] = new("GPU fans", HardwareKind.Gpu, DemoData.GpuName, 2600, 46),
    };

    private Timer? _timer;
    private bool _disposed;

    public DemoFanControlService()
        : this(null, null)
    {
    }

    public DemoFanControlService(ISensorService? sensors, ISettingsService? settings)
    {
        _sensors = sensors;
        _settings = settings;
    }

    /// <summary>The "Minimum fan speed" from the Fans page (20 % when no settings service is wired up, e.g. in tests).</summary>
    private double MinimumPercent
    {
        get
        {
            try
            {
                return _settings?.Current.Fans.MinimumPercent ?? DefaultMinimumPercent;
            }
            catch
            {
                return DefaultMinimumPercent;
            }
        }
    }

    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public FanProfile? ActiveProfile { get; private set; }
    public IReadOnlyList<FanProfile> BuiltInProfiles { get; } = FanCurveMath.BuiltInProfiles(CpuSensor, GpuSensor, CpuFans, GpuFans);

    public IReadOnlyList<string> CapabilityNotes { get; } =
    [
        "Motherboard fan headers (CPU and case fans): Nuvoton NCT6798D on " + DemoData.MotherboardName + " — 4 controllable",
        "Read-only: Nuvoton NCT6798D reports 1 fan speed(s) but exposes no control",
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
        double clamped = FanSafetyRules.ClampManual(percent, MinimumPercent, allowBelowMinimum);
        lock (_gate)
        {
            if (!_state.TryGetValue(fanId, out var s) || !s.Controllable)
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
            if (!_state.TryGetValue(fanId, out var s) || !s.Controllable)
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
            if (!_state.TryGetValue(fanId, out var s) || !s.Controllable)
            {
                return OperationResult.Fail($"Unknown fan \"{fanId}\".");
            }

            s.Mode = FanMode.Curve;
            s.Curve = FanCurveMath.Clone(curve);
            s.Curve.Points = FanCurveMath.Normalize(s.Curve.Points);
            s.Percent = ClampCurve(FanCurveMath.Evaluate(s.Curve, ReadTemperature(s.Curve.TemperatureSensorId) ?? 58, null));
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
                if (!_state.TryGetValue(id, out var s) || !s.Controllable)
                {
                    continue;
                }

                switch (assignment.Mode)
                {
                    case FanMode.Manual:
                        s.Mode = FanMode.Manual;
                        s.Curve = null;
                        s.Percent = FanSafetyRules.ClampManual(assignment.ManualPercent ?? s.Percent, MinimumPercent, false);
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
                        s.Percent = ClampCurve(FanCurveMath.Evaluate(s.Curve, ReadTemperature(s.Curve.TemperatureSensorId) ?? 58, null));
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
            new("nvme.temp", DemoData.NvmeModel + " — Temperature", HardwareKind.Storage, 35),
        ];
    }

    public IReadOnlyList<RawFanControl> GetRawControls()
    {
        lock (_gate)
        {
            return _state.Where(kv => kv.Value.Controllable).Select(kv => new RawFanControl(kv.Key + ".control", kv.Value.HardwareName, kv.Value.Name,
                kv.Value.Mode == FanMode.Auto ? "Default" : "Software", kv.Value.Mode == FanMode.Auto ? null : kv.Value.Percent,
                0, 100, kv.Value.Percent, kv.Key + ".rpm")).ToList();
        }
    }

    public async Task<FanDiagnosticReport> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 200).ConfigureAwait(false);
        return FanDiagnostics.Build(BuildDiagnosticInput(), DateTimeOffset.Now);
    }

    public async Task<SensorReloadResult> ReloadSensorsAsync(CancellationToken cancellationToken)
    {
        await RevertAllToAutoAsync(cancellationToken).ConfigureAwait(false);
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        var counts = CountMotherboard();
        var message = SensorReloadText.Describe(counts, counts) + " (demo)";
        return new SensorReloadResult(true, message, counts, counts);
    }

    /// <summary>What the demo desktop's hardware monitor would report: the board, its Nuvoton chip, the graphics card and every fan sensor.</summary>
    internal FanDiagnosticInput BuildDiagnosticInput()
    {
        const string chipId = "/lpc/nct6798d/0";
        const string gpuId = "/gpu-nvidia/0";
        var sensors = new List<FanSensorInfo>();
        var fans = Build();
        int index = 0;
        foreach (var fan in fans)
        {
            bool gpu = fan.Hardware == HardwareKind.Gpu;
            string hw = gpu ? gpuId : chipId;
            int i = gpu ? 0 : index++;
            sensors.Add(new FanSensorInfo(hw + "/fan/" + i, hw, fan.HardwareName, fan.Hardware, SensorKind.Fan, gpu ? "GPU Fan 1" : "Fan #" + (i + 1), i, fan.Rpm));
            if (fan.Controllable)
            {
                sensors.Add(new FanSensorInfo(hw + "/control/" + i, hw, fan.HardwareName, fan.Hardware, SensorKind.Control, gpu ? "GPU Fan 1" : "Fan #" + (i + 1), i, fan.Percent));
            }
        }

        // A spare PWM output with no speed input (the pump header on many boards is wired like this).
        sensors.Add(new FanSensorInfo(chipId + "/control/6", chipId, "Nuvoton NCT6798D", HardwareKind.Motherboard, SensorKind.Control, "Fan #7", 6, 60));

        var pairs = FanPairing.Pair(sensors);
        int boardFans = sensors.Count(s => s.Hardware == HardwareKind.Motherboard && s.Kind == SensorKind.Fan);
        int boardControls = sensors.Count(s => s.Hardware == HardwareKind.Motherboard && s.Kind == SensorKind.Control);
        var nodes = new List<FanHardwareNode>
        {
            new(DemoData.MotherboardName, "Motherboard", HardwareKind.Motherboard, 0, 0, 0, 0),
            new("Nuvoton NCT6798D", "SuperIO", HardwareKind.Motherboard, 1, 38, boardFans, boardControls),
            new(DemoData.GpuName, "GpuNvidia", HardwareKind.Gpu, 0, 24, 1, 1),
        };
        return new FanDiagnosticInput(new DemoPawnIoService().GetStatus(), true, null, nodes, sensors, pairs,
            [new ConflictingApp("HWiNFO", "HWiNFO64")], false);
    }

    private MotherboardSensorCounts CountMotherboard()
    {
        var input = BuildDiagnosticInput();
        return new MotherboardSensorCounts(
            input.Sensors.Count(s => s.Hardware == HardwareKind.Motherboard && s.Kind == SensorKind.Fan),
            input.Sensors.Count(s => s.Hardware == HardwareKind.Motherboard && s.Kind == SensorKind.Control),
            input.Hardware.Where(h => h.Kind == HardwareKind.Motherboard && h.Depth > 0).Sum(h => h.SensorCount),
            true);
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
                    s.Percent = ClampCurve(FanCurveMath.Evaluate(s.Curve, temp, s.Percent));
                }
            }

            FansUpdated?.Invoke(this, Build());
        }
        catch
        {
            // Demo loop must never surface errors.
        }
    }

    private double ClampCurve(double percent) => FanSafetyRules.ClampManual(percent, MinimumPercent, false);

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
                double rpm = s.Empty ? 0 : Math.Round(DemoData.Jitter(_random, s.MaxRpm * s.Percent / 100, 25));
                return s.Controllable
                    ? new FanDevice(kv.Key, s.Name, s.Kind, s.HardwareName, rpm, Math.Round(s.Percent), true, 0, 100, s.Mode, kv.Key + ".control", kv.Key + ".rpm")
                    : new FanDevice(kv.Key, s.Name, s.Kind, s.HardwareName, rpm, null, false, 0, 100, FanMode.Auto, null, kv.Key + ".rpm");
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
        public bool Controllable { get; init; } = true;
        public bool Empty { get; init; }
    }
}
