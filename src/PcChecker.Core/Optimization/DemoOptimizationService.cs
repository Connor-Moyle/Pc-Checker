using PcChecker.Core.Common;
using PcChecker.Core.Performance;

namespace PcChecker.Core.Optimization;

/// <summary>Demo optimization: remembers applied actions in memory and never touches the system.</summary>
public sealed class DemoOptimizationService : IOptimizationService
{
    private readonly IReadOnlyList<IOptimizationActionProvider> _providers;
    private readonly HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase)
    {
        "power.balanced", "gamemode.enable", "storagesense.enable", "trim.enable",
    };

    public DemoOptimizationService(IEnumerable<IOptimizationActionProvider> providers)
    {
        _providers = providers.ToList();
    }

    public IReadOnlyList<OptimizationAction> Actions =>
        OptimizationCatalog.All.Concat(_providers.SelectMany(p => p.Actions)).ToList();

    public async Task<ActionState> GetStateAsync(string actionId, CancellationToken cancellationToken)
    {
        if (FindProvider(actionId) is { } p)
        {
            return await p.GetStateAsync(actionId, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        return new ActionState(actionId, _applied.Contains(actionId), _applied.Contains(actionId) ? "On" : "Off");
    }

    public async Task<OperationResult> ApplyAsync(string actionId, CancellationToken cancellationToken)
    {
        if (FindProvider(actionId) is { } p)
        {
            return await p.ApplyAsync(actionId, cancellationToken).ConfigureAwait(false);
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _applied.Add(actionId);
        var action = OptimizationCatalog.All.FirstOrDefault(a => a.Id == actionId);
        return OperationResult.Ok($"Applied \"{action?.Title ?? actionId}\" (demo).", action?.RequiresRestart ?? false);
    }

    public async Task<OperationResult> RevertAsync(string actionId, CancellationToken cancellationToken)
    {
        if (FindProvider(actionId) is { } p)
        {
            return await p.RevertAsync(actionId, cancellationToken).ConfigureAwait(false);
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _applied.Remove(actionId);
        return OperationResult.Ok($"Reverted \"{actionId}\" (demo).");
    }

    public async Task<OperationResult> SetStartupItemEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok($"{item.Name} is now {(enabled ? "enabled" : "disabled")} at startup (demo).");
    }

    public async Task<OperationResult> SetServiceStartModeAsync(string serviceName, string startMode, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok($"Service {serviceName} start mode set to {startMode} (demo).");
    }

    private IOptimizationActionProvider? FindProvider(string actionId) => _providers.FirstOrDefault(p => p.Handles(actionId));
}
