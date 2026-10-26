using PcChecker.Core.Common;

namespace PcChecker.Core.Optimization;

/// <summary>
/// A module that contributes its own optimization actions (Latency, Memory, Startup, Input…). The
/// <see cref="IOptimizationService"/> aggregates every registered provider and dispatches by <see cref="Handles"/>.
/// Providers run their changes through <c>IChangeGuard</c> themselves.
/// </summary>
public interface IOptimizationActionProvider
{
    IReadOnlyList<OptimizationAction> Actions { get; }
    bool Handles(string actionId);
    Task<ActionState> GetStateAsync(string id, CancellationToken cancellationToken);
    Task<OperationResult> ApplyAsync(string id, CancellationToken cancellationToken);
    Task<OperationResult> RevertAsync(string id, CancellationToken cancellationToken);
}
