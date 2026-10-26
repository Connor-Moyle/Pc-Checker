using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Power;

/// <summary>
/// Windows implementation of <see cref="IPowerService"/>: component wattages come from the sensor service, the
/// battery from WMI, the total is measured by a PSU sensor when one exists and estimated otherwise, and power
/// plans are read and switched with <c>powercfg</c> (switching goes through the change guard).
/// </summary>
public sealed class WindowsPowerService : IPowerService, IPowerServiceExtras, IDisposable
{
    private static readonly TimeSpan BatteryRefresh = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SensorStale = TimeSpan.FromSeconds(5);

    private readonly ISensorService _sensors;
    private readonly ISettingsService _settings;
    private readonly IChangeGuard _changeGuard;
    private readonly ILogger<WindowsPowerService> _logger;
    private readonly object _gate = new();
    private readonly PowerSession _session;
    private readonly Timer _fallbackTimer;

    private BatteryReading _battery = BatteryReading.None();
    private DateTimeOffset _batteryRead = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSensorSnapshot = DateTimeOffset.MinValue;
    private bool _updating;

    public WindowsPowerService(ISensorService sensors, ISettingsService settings, IChangeGuard changeGuard, ILogger<WindowsPowerService> logger)
    {
        _sensors = sensors;
        _settings = settings;
        _changeGuard = changeGuard;
        _logger = logger;
        _session = new PowerSession(SensorRingBuffer.CapacityFor(settings.Current.Monitoring.HistoryMinutes, settings.Current.Monitoring.PollIntervalMs));
        _sensors.SnapshotUpdated += OnSnapshot;
        // Laptops without a working sensor driver still get battery numbers from this timer.
        _fallbackTimer = new Timer(OnFallbackTick, null, 2000, 2000);
    }

    public PowerSnapshot? Latest { get; private set; }

    public bool IsEstimate { get; private set; } = true;

    public PowerBreakdown Breakdown { get; private set; } = PowerBreakdown.Empty;

    public BatteryReading Battery => _battery;

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
        var result = await PowerCfg.RunAsync("/list", cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            _logger.LogWarning("powercfg /list failed ({Code}): {Error}", result.ExitCode, result.StdErr.Trim());
            return [];
        }

        var plans = PowerPlanParser.Parse(result.StdOut);
        if (plans.Count > 0 && !plans.Any(p => p.IsActive))
        {
            var active = await PowerCfg.RunAsync("/getactivescheme", cancellationToken).ConfigureAwait(false);
            if (PowerPlanParser.ParseActive(active.StdOut) is { } current)
            {
                plans = plans.Select(p => p with { IsActive = p.Id == current.Id }).ToList();
            }
        }

        return plans;
    }

    public async Task<OperationResult> SetActivePowerPlanAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plans = await GetPowerPlansAsync(cancellationToken).ConfigureAwait(false);
        var target = plans.FirstOrDefault(p => p.Id == planId);
        var previous = plans.FirstOrDefault(p => p.IsActive);
        string targetName = target?.Name ?? PowerPlanParser.DescribeKnown(planId) ?? planId.ToString();
        if (previous is not null && previous.Id == planId)
        {
            return OperationResult.Ok($"\"{targetName}\" is already the active power plan.");
        }

        // Restore point behaviour follows the user's settings (null = "per settings"); the switch itself is fully reversible.
        var options = ChangeOptions.For(
            "Power",
            targetName,
            reversible: true,
            undoHint: previous is null ? "Choose the previous plan in Power > Power plan." : $"Switch back to \"{previous.Name}\" (powercfg /setactive {previous.Id})");

        return await _changeGuard.RunGuardedAsync($"Switch power plan to \"{targetName}\"", options, async ct =>
        {
            var result = await PowerCfg.RunAsync($"/setactive {planId}", ct).ConfigureAwait(false);
            if (!result.Success)
            {
                string error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim();
                return OperationResult.Fail($"Windows refused to switch the power plan: {error}");
            }

            var check = await PowerCfg.RunAsync("/getactivescheme", ct).ConfigureAwait(false);
            var active = PowerPlanParser.ParseActive(check.StdOut);
            if (active is not null && active.Id != planId)
            {
                return OperationResult.Fail($"powercfg reported success but \"{active.Name}\" is still active.");
            }

            return OperationResult.Ok($"Power plan switched to \"{targetName}\".");
        }, null, cancellationToken).ConfigureAwait(false);
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
        _sensors.SnapshotUpdated -= OnSnapshot;
        _fallbackTimer.Dispose();
    }

    private void OnSnapshot(object? sender, SensorSnapshot snapshot)
    {
        _lastSensorSnapshot = DateTimeOffset.Now;
        Publish(snapshot.Readings);
    }

    private void OnFallbackTick(object? state)
    {
        if (DateTimeOffset.Now - _lastSensorSnapshot < SensorStale)
        {
            return;
        }

        Publish(_sensors.Latest?.Readings ?? []);
    }

    private void Publish(IReadOnlyList<SensorReading> readings)
    {
        PowerSnapshot snapshot;
        lock (_gate)
        {
            if (_updating)
            {
                return;
            }

            _updating = true;
            try
            {
                var now = DateTimeOffset.Now;
                RefreshBattery(now);
                var breakdown = PowerMath.Aggregate(readings);
                var battery = _battery;

                var (total, isEstimate) = PowerMath.EstimateTotal(breakdown.CpuPackageW, breakdown.GpuW, breakdown.OtherW, breakdown.PsuW);
                if (total is null && battery.OnBattery && battery.DischargeW is { } discharge)
                {
                    // No component sensors: on battery the discharge rate is the whole system.
                    total = Math.Round(discharge, 1);
                    isEstimate = false;
                }

                _session.Record(now, total);
                Breakdown = breakdown;
                IsEstimate = isEstimate;
                snapshot = new PowerSnapshot(
                    now,
                    breakdown.CpuPackageW is { } cpu ? Math.Round(cpu, 1) : null,
                    breakdown.GpuW is { } gpu ? Math.Round(gpu, 1) : null,
                    breakdown.OtherW is { } other ? Math.Round(other, 1) : null,
                    total,
                    battery.OnBattery ? battery.DischargeW : null,
                    battery.Percent,
                    battery.OnBattery,
                    Math.Round(_session.EnergyWh, 3));
                Latest = snapshot;
            }
            finally
            {
                _updating = false;
            }
        }

        try
        {
            Updated?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A power listener threw.");
        }
    }

    private void RefreshBattery(DateTimeOffset now)
    {
        if (now - _batteryRead < BatteryRefresh)
        {
            return;
        }

        _batteryRead = now;
        try
        {
            _battery = WindowsBatteryReader.Read();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Battery read failed.");
            _battery = BatteryReading.None(ex.Message);
        }
    }
}
