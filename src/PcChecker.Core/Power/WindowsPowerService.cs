// TODO(power): power module — sensor power readings, BatteryStatus WMI, powercfg.
using PcChecker.Core.Common;

namespace PcChecker.Core.Power;

/// <summary>Windows implementation of <see cref="IPowerService"/>.</summary>
public sealed class WindowsPowerService : IPowerService
{
    public PowerSnapshot? Latest => null;

    public event EventHandler<PowerSnapshot>? Updated;

    public IReadOnlyList<(DateTimeOffset Time, double Value)> GetHistory(TimeSpan span) => [];

    public Task<IReadOnlyList<PowerPlanInfo>> GetPowerPlansAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PowerPlanInfo>>([]);

    public Task<OperationResult> SetActivePowerPlanAsync(Guid planId, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail("Power management is not implemented yet."));

    public double EstimateCostPerHour(double pricePerKwh) => 0;

    internal void RaiseForCompiler() => Updated?.Invoke(this, new PowerSnapshot(DateTimeOffset.Now, null, null, null, null, null, null, false, 0));
}
