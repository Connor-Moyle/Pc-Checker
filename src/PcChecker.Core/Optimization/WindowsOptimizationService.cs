// TODO(optimization): optimization module — registry, powercfg, sc, PowerShell, DISM/SFC via IChangeGuard.
using PcChecker.Core.Common;
using PcChecker.Core.Performance;

namespace PcChecker.Core.Optimization;

/// <summary>
/// Windows implementation of <see cref="IOptimizationService"/>: the catalog actions plus every
/// <see cref="IOptimizationActionProvider"/> registered by other modules.
/// </summary>
public sealed class WindowsOptimizationService : IOptimizationService
{
    private const string NotImplemented = "Optimization actions are not implemented yet.";

    private readonly IReadOnlyList<IOptimizationActionProvider> _providers;

    public WindowsOptimizationService(IEnumerable<IOptimizationActionProvider> providers)
    {
        _providers = providers.ToList();
    }

    public IReadOnlyList<OptimizationAction> Actions =>
        OptimizationCatalog.All.Concat(_providers.SelectMany(p => p.Actions)).ToList();

    public Task<ActionState> GetStateAsync(string actionId, CancellationToken cancellationToken) =>
        FindProvider(actionId) is { } p ? p.GetStateAsync(actionId, cancellationToken) : Task.FromResult(new ActionState(actionId, null, null));

    public Task<OperationResult> ApplyAsync(string actionId, CancellationToken cancellationToken) =>
        FindProvider(actionId) is { } p ? p.ApplyAsync(actionId, cancellationToken) : Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> RevertAsync(string actionId, CancellationToken cancellationToken) =>
        FindProvider(actionId) is { } p ? p.RevertAsync(actionId, cancellationToken) : Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> SetStartupItemEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> SetServiceStartModeAsync(string serviceName, string startMode, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    private IOptimizationActionProvider? FindProvider(string actionId) => _providers.FirstOrDefault(p => p.Handles(actionId));
}
