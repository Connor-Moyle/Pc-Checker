using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Performance;
using PcChecker.Core.Restore;
using PcChecker.Core.Startup;

namespace PcChecker.Core.Optimization;

/// <summary>
/// Windows implementation of <see cref="IOptimizationService"/>: implements every <see cref="OptimizationCatalog"/>
/// action (powercfg, registry, sc, fsutil, defrag, netsh, DISM, SFC) and aggregates the
/// <see cref="IOptimizationActionProvider"/>s other modules register. Every change runs through
/// <see cref="IChangeGuard"/>; long-running tools stream their output through <see cref="OutputLine"/>.
/// </summary>
public sealed partial class WindowsOptimizationService : IOptimizationService, IOptimizationOutputSource
{
    private const string Module = "Optimization";

    /// <summary>A catalog action's state probe, apply and (optional) revert steps.</summary>
    private sealed record Handler(
        Func<CancellationToken, Task<ActionState>> State,
        Func<CancellationToken, Task<OperationResult>> Apply,
        Func<CancellationToken, Task<OperationResult>>? Revert);

    /// <summary>Every catalog id this service implements. Tests check it covers <see cref="OptimizationCatalog.All"/>.</summary>
    public static IReadOnlyList<string> SupportedActionIds { get; } =
    [
        "power.highperformance", "power.balanced", "power.ultimate",
        "visuals.performance", "visuals.balanced", "transparency.disable", "animations.disable",
        "faststartup.disable", "faststartup.enable", "hibernation.disable",
        "gamemode.enable", "gamebar.disable", "backgroundapps.disable", "storagesense.enable", "notifications.focus",
        "trim.enable", "defrag.optimize", "tempfiles.clean",
        "dns.cloudflare", "telemetry.minimal", "sysmain.disable", "search.indexing.disable",
        "dism.restorehealth", "sfc.scannow", "network.reset",
    ];

    private readonly IChangeGuard _guard;
    private readonly IStartupService _startup;
    private readonly IReadOnlyList<IOptimizationActionProvider> _providers;
    private readonly ILogger<WindowsOptimizationService> _logger;
    private readonly Dictionary<string, Handler> _handlers;
    private Guid? _previousPowerScheme;

    public WindowsOptimizationService(IChangeGuard guard, IStartupService startup, IEnumerable<IOptimizationActionProvider> providers,
        ILogger<WindowsOptimizationService> logger)
    {
        _guard = guard;
        _startup = startup;
        _providers = providers.ToList();
        _logger = logger;
        _handlers = BuildHandlers();
    }

    public event EventHandler<string>? OutputLine;

    /// <summary>Ids that have a handler on this instance (equal to <see cref="SupportedActionIds"/>).</summary>
    public IReadOnlyCollection<string> HandlerIds => _handlers.Keys;

    public IReadOnlyList<OptimizationAction> Actions =>
        OptimizationCatalog.All.Concat(_providers.SelectMany(p => SafeActions(p))).ToList();

    public async Task<ActionState> GetStateAsync(string actionId, CancellationToken cancellationToken)
    {
        if (FindProvider(actionId) is { } provider)
        {
            try
            {
                return await provider.GetStateAsync(actionId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider state check failed for {Action}.", actionId);
                return new ActionState(actionId, null, "Unknown");
            }
        }

        if (!_handlers.TryGetValue(actionId, out var handler))
        {
            return new ActionState(actionId, null, "Not available");
        }

        try
        {
            return await handler.State(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "State check failed for {Action}.", actionId);
            return new ActionState(actionId, null, "Unknown");
        }
    }

    public Task<OperationResult> ApplyAsync(string actionId, CancellationToken cancellationToken) => RunAsync(actionId, revert: false, cancellationToken);

    public Task<OperationResult> RevertAsync(string actionId, CancellationToken cancellationToken) => RunAsync(actionId, revert: true, cancellationToken);

    /// <summary>Startup toggles belong to the Startup module, which guards and journals them itself.</summary>
    public Task<OperationResult> SetStartupItemEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken) =>
        _startup.SetStartupItemEnabledAsync(item, enabled, cancellationToken);

    /// <summary>
    /// Service start modes belong to the Startup module too: it owns the single protected-service list
    /// (<see cref="ProtectedServices"/>), so the Performance and Startup pages refuse exactly the same services.
    /// </summary>
    public Task<OperationResult> SetServiceStartModeAsync(string serviceName, string startMode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (ServiceClassifier.ToScArgument(startMode) is null)
        {
            return Task.FromResult(OperationResult.Fail($"\"{startMode}\" is not a start mode that can be set. Use Automatic, Automatic (Delayed), Manual or Disabled."));
        }

        return _startup.SetServiceStartModeAsync(serviceName, startMode, cancellationToken);
    }

    private async Task<OperationResult> RunAsync(string actionId, bool revert, CancellationToken cancellationToken)
    {
        if (FindProvider(actionId) is { } provider)
        {
            try
            {
                return revert
                    ? await provider.RevertAsync(actionId, cancellationToken).ConfigureAwait(false)
                    : await provider.ApplyAsync(actionId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Provider action {Action} threw.", actionId);
                return OperationResult.FromException(ex);
            }
        }

        var action = OptimizationCatalog.Find(actionId);
        if (action is null || !_handlers.TryGetValue(actionId, out var handler))
        {
            return OperationResult.Fail($"\"{actionId}\" is not an optimization this version knows about.");
        }

        if (revert && (handler.Revert is null || !action.Reversible))
        {
            return OperationResult.Fail($"\"{action.Title}\" cannot be undone from here.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Optimization actions can only run on Windows.");
        }

        if (actionId is "faststartup.enable" or "faststartup.disable")
        {
            // Fast startup needs hibernation; the Startup module turns that on first and guards/journals the change itself,
            // so both pages behave the same instead of this one silently writing HiberbootEnabled.
            bool enable = actionId == "faststartup.enable" ? !revert : revert;
            return await _startup.SetFastStartupAsync(enable, cancellationToken).ConfigureAwait(false);
        }

        string description = (revert ? "Undo: " : string.Empty) + action.Title;
        var options = ChangeOptions.For(Module, actionId, action.Reversible, action.Reversible ? "Use Revert on the Optimize page." : null);
        var result = await _guard.RunGuardedAsync(description, options,
            ct => revert ? handler.Revert!(ct) : handler.Apply(ct), null, cancellationToken).ConfigureAwait(false);

        if (result.Success && action.RequiresRestart && !result.RebootRequired)
        {
            result = result with { RebootRequired = true };
        }

        return result;
    }

    private IOptimizationActionProvider? FindProvider(string actionId)
    {
        foreach (var provider in _providers)
        {
            try
            {
                if (provider.Handles(actionId))
                {
                    return provider;
                }
            }
            catch
            {
                // A broken provider must not block the catalog actions.
            }
        }

        return null;
    }

    private static IReadOnlyList<OptimizationAction> SafeActions(IOptimizationActionProvider provider)
    {
        try
        {
            return provider.Actions;
        }
        catch
        {
            return [];
        }
    }

    private void Emit(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            OutputLine?.Invoke(this, line);
        }
        catch
        {
            // UI handlers must not break the action.
        }
    }
}
