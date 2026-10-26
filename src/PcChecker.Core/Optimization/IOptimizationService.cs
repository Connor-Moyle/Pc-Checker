using PcChecker.Core.Common;
using PcChecker.Core.Performance;

namespace PcChecker.Core.Optimization;

/// <summary>Applies and reverts optimization actions.</summary>
public interface IOptimizationService
{
    IReadOnlyList<OptimizationAction> Actions { get; }
    Task<ActionState> GetStateAsync(string actionId, CancellationToken cancellationToken);
    Task<OperationResult> ApplyAsync(string actionId, CancellationToken cancellationToken);
    Task<OperationResult> RevertAsync(string actionId, CancellationToken cancellationToken);
    Task<OperationResult> SetStartupItemEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken);
    Task<OperationResult> SetServiceStartModeAsync(string serviceName, string startMode, CancellationToken cancellationToken);
}
