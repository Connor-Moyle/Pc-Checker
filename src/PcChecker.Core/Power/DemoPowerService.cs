using PcChecker.Core.Common;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.Power;

/// <summary>
/// Demo power draw for the sample desktop (no battery, no PSU telemetry, so the total is an estimate) with the
/// three stock Windows plans. When a sensor service is supplied the numbers follow its power readings exactly;
/// otherwise it generates its own gentle wobble once a second.
/// </summary>
public sealed class DemoPowerService : IPowerService, IPowerServiceExtras, IDisposable
{
    private readonly object _gate = new();
    private readonly PowerSession _session = new();
    private readonly Random _random = DemoData.CreateRandom(99);
    private readonly ISensorService? _sensors;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private Guid _active = PowerPlanParser.Balanced;
    private Timer? _timer;

    public DemoPowerService()
        : this(null)
    {
    }

    public DemoPowerService(ISensorService? sensors)
    {
        _sensors = sensors;
        if (_sensors is not null)
        {
            _sensors.SnapshotUpdated += OnSnapshot;
            if (_sensors.Latest is { } latest)
            {
                Publish(latest.Readings);
            }
        }
        else
        {
            Tick(null);
            _timer = new Timer(Tick, null, 1000, 1000);
        }
    }

    public PowerSnapshot? Latest { get; private set; }

    public bool IsEstimate => true;

    public PowerBreakdown Breakdown { get; private set; } = PowerBreakdown.Empty;

    public BatteryReading Battery { get; } = BatteryReading.None();

    public DateTimeOffset SessionStarted => _session.Started;

    public event EventHandler<PowerSnapshot>? Updated;

    public IReadOnlyList<(DateTimeOffset Time, double Value)> GetHistory(TimeSpan span)
    {
        lock (_gate)
        {
            return _session.History(span, DateTimeOffset.Now);
        }
    }

    public async Task<IReadOnlyList<PowerPlanInfo>> GetPowerPlansAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 250).ConfigureAwait(false);
        return
        [
            new(PowerPlanParser.Balanced, "Balanced", _active == PowerPlanParser.Balanced),
            new(PowerPlanParser.HighPerformance, "High performance", _active == PowerPlanParser.HighPerformance),
            new(PowerPlanParser.UltimatePerformance, "Ultimate Performance", _active == PowerPlanParser.UltimatePerformance),
        ];
    }

    public async Task<OperationResult> SetActivePowerPlanAsync(Guid planId, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _active = planId;
        string name = PowerPlanParser.DescribeKnown(planId) ?? planId.ToString();
        return OperationResult.Ok($"Power plan switched to \"{name}\" (demo).");
    }

    public double EstimateCostPerHour(double pricePerKwh) => PowerMath.CostPerHour(Latest?.EstimatedTotalW, pricePerKwh);

    public void ResetSession()
    {
        lock (_gate)
        {
            _session.Reset(DateTimeOffset.Now);
        }
    }

    public void Dispose()
    {
        if (_sensors is not null)
        {
            _sensors.SnapshotUpdated -= OnSnapshot;
        }

        _timer?.Dispose();
        _timer = null;
    }

    private void OnSnapshot(object? sender, SensorSnapshot snapshot) => Publish(snapshot.Readings);

    private void Tick(object? state)
    {
        double seconds = (DateTimeOffset.Now - _started).TotalSeconds;
        double cpu = Math.Round(48 + 8 * Math.Sin(2 * Math.PI * seconds / 95) + DemoData.Jitter(_random, 0, 1.5), 1);
        double gpu = Math.Round(42 + 6 * Math.Sin(2 * Math.PI * seconds / 120 + 0.5) + DemoData.Jitter(_random, 0, 1.2), 1);
        Publish(
        [
            new SensorReading("cpu.power", DemoData.CpuName, HardwareKind.Cpu, SensorKind.Power, "CPU Package", cpu, cpu, cpu, "W"),
            new SensorReading("gpu.power", DemoData.GpuName, HardwareKind.Gpu, SensorKind.Power, "GPU Power", gpu, gpu, gpu, "W"),
        ]);
    }

    private void Publish(IReadOnlyList<SensorReading> readings)
    {
        PowerSnapshot snapshot;
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var breakdown = PowerMath.Aggregate(readings);
            var (total, _) = PowerMath.EstimateTotal(breakdown.CpuPackageW, breakdown.GpuW, breakdown.OtherW, breakdown.PsuW);
            _session.Record(now, total);
            Breakdown = breakdown;
            snapshot = new PowerSnapshot(now, breakdown.CpuPackageW, breakdown.GpuW, breakdown.OtherW, total, null, null, false, Math.Round(_session.EnergyWh, 3));
            Latest = snapshot;
        }

        Updated?.Invoke(this, snapshot);
    }
}
