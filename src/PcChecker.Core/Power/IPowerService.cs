using PcChecker.Core.Common;

namespace PcChecker.Core.Power;

/// <summary>Power draw estimates, battery discharge, power plans and energy cost.</summary>
public interface IPowerService
{
    PowerSnapshot? Latest { get; }
    event EventHandler<PowerSnapshot>? Updated;
    IReadOnlyList<(DateTimeOffset Time, double Value)> GetHistory(TimeSpan span);
    Task<IReadOnlyList<PowerPlanInfo>> GetPowerPlansAsync(CancellationToken cancellationToken);
    Task<OperationResult> SetActivePowerPlanAsync(Guid planId, CancellationToken cancellationToken);
    double EstimateCostPerHour(double pricePerKwh);
}
