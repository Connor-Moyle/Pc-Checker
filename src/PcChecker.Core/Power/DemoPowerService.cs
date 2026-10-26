using PcChecker.Core.Common;

namespace PcChecker.Core.Power;

/// <summary>Demo power draw for a desktop (no battery) with a few power plans.</summary>
public sealed class DemoPowerService : IPowerService, IDisposable
{
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private readonly object _gate = new();
    private readonly List<(DateTimeOffset Time, double Value)> _history = [];
    private readonly Random _random = DemoData.CreateRandom(99);
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private Guid _active = Balanced;
    private double _energyWh;
    private Timer? _timer;

    public DemoPowerService()
    {
        Tick(null);
        _timer = new Timer(Tick, null, 1000, 1000);
    }

    public PowerSnapshot? Latest { get; private set; }

    public event EventHandler<PowerSnapshot>? Updated;

    public IReadOnlyList<(DateTimeOffset Time, double Value)> GetHistory(TimeSpan span)
    {
        lock (_gate)
        {
            var cutoff = DateTimeOffset.Now - span;
            return _history.Where(p => p.Time >= cutoff).ToList();
        }
    }

    public async Task<IReadOnlyList<PowerPlanInfo>> GetPowerPlansAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 250).ConfigureAwait(false);
        return
        [
            new(Balanced, "Balanced", _active == Balanced),
            new(HighPerformance, "High performance", _active == HighPerformance),
            new(Ultimate, "Ultimate Performance", _active == Ultimate),
        ];
    }

    public async Task<OperationResult> SetActivePowerPlanAsync(Guid planId, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _active = planId;
        return OperationResult.Ok("Power plan changed (demo).");
    }

    public double EstimateCostPerHour(double pricePerKwh) => (Latest?.EstimatedTotalW ?? 0) / 1000.0 * pricePerKwh;

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Tick(object? state)
    {
        double cpu = Math.Round(DemoData.Jitter(_random, 48, 5), 1);
        double gpu = Math.Round(DemoData.Jitter(_random, 42, 4), 1);
        double other = 35;
        double total = Math.Round((cpu + gpu + other) * 1.15, 0);
        _energyWh += total / 3600.0;
        var snapshot = new PowerSnapshot(DateTimeOffset.Now, cpu, gpu, other, total, null, null, false, Math.Round(_energyWh, 3));
        lock (_gate)
        {
            _history.Add((snapshot.Timestamp, total));
            if (_history.Count > 3600)
            {
                _history.RemoveAt(0);
            }
        }

        Latest = snapshot;
        Updated?.Invoke(this, snapshot);
    }
}
