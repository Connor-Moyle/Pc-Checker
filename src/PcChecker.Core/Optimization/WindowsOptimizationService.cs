// TODO(optimization): optimization module — registry, powercfg, sc, PowerShell, DISM/SFC via IChangeGuard.
using PcChecker.Core.Common;
using PcChecker.Core.Performance;

namespace PcChecker.Core.Optimization;

/// <summary>Windows implementation of <see cref="IOptimizationService"/>.</summary>
public sealed class WindowsOptimizationService : IOptimizationService
{
    private const string NotImplemented = "Optimization actions are not implemented yet.";

    public IReadOnlyList<OptimizationAction> Actions => OptimizationCatalog.All;

    public Task<ActionState> GetStateAsync(string actionId, CancellationToken cancellationToken) =>
        Task.FromResult(new ActionState(actionId, null, null));

    public Task<OperationResult> ApplyAsync(string actionId, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> RevertAsync(string actionId, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> SetStartupItemEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> SetServiceStartModeAsync(string serviceName, string startMode, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));
}
