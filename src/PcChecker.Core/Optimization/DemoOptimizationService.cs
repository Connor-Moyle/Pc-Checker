using PcChecker.Core.Common;
using PcChecker.Core.Performance;
using PcChecker.Core.Startup;

namespace PcChecker.Core.Optimization;

/// <summary>
/// Demo optimization: describes the sample gaming desktop's current settings, remembers applied actions in memory,
/// streams fake console output for the long-running repair tools and never touches the system.
/// </summary>
public sealed class DemoOptimizationService : IOptimizationService, IOptimizationOutputSource
{
    private readonly IReadOnlyList<IOptimizationActionProvider> _providers;
    private readonly IStartupService? _startup;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _current = new(StringComparer.OrdinalIgnoreCase)
    {
        ["power.balanced"] = "Active: Balanced",
        ["power.highperformance"] = "Active: Balanced",
        ["power.ultimate"] = "Active: Balanced",
        ["visuals.performance"] = "Let Windows choose",
        ["visuals.balanced"] = "Let Windows choose",
        ["transparency.disable"] = "Transparency on",
        ["animations.disable"] = "Animations on",
        ["faststartup.disable"] = "Fast startup on",
        ["faststartup.enable"] = "Fast startup on",
        ["hibernation.disable"] = "Hibernation on",
        ["gamemode.enable"] = "Game Mode on",
        ["gamebar.disable"] = "Game Bar on",
        ["backgroundapps.disable"] = "Background apps allowed",
        ["storagesense.enable"] = "Storage Sense on",
        ["notifications.focus"] = "Tips and suggestions on",
        ["trim.enable"] = "TRIM on",
        ["defrag.optimize"] = "Runs on demand",
        ["tempfiles.clean"] = "Opens the Cleanup page",
        ["dns.cloudflare"] = "Automatic (DHCP) DNS",
        ["telemetry.minimal"] = "Diagnostic data: Windows default",
        ["sysmain.disable"] = "Automatic, running",
        ["search.indexing.disable"] = "Automatic (Delayed), running",
        ["dism.restorehealth"] = "Runs on demand (5–20 min)",
        ["sfc.scannow"] = "Runs on demand (5–15 min)",
        ["network.reset"] = "Runs on demand; restart afterwards",
    };

    private readonly HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase)
    {
        "power.balanced", "visuals.balanced", "faststartup.enable", "gamemode.enable", "storagesense.enable", "trim.enable",
    };

    private static readonly HashSet<string> OnDemand = new(StringComparer.OrdinalIgnoreCase)
    {
        "defrag.optimize", "tempfiles.clean", "dism.restorehealth", "sfc.scannow", "network.reset",
    };

    public DemoOptimizationService(IEnumerable<IOptimizationActionProvider> providers, IStartupService? startup = null)
    {
        _providers = providers.ToList();
        _startup = startup;
    }

    public event EventHandler<string>? OutputLine;

    public IReadOnlyList<OptimizationAction> Actions =>
        OptimizationCatalog.All.Concat(_providers.SelectMany(p => p.Actions)).ToList();

    public async Task<ActionState> GetStateAsync(string actionId, CancellationToken cancellationToken)
    {
        if (FindProvider(actionId) is { } p)
        {
            return await p.GetStateAsync(actionId, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            bool? applied = OnDemand.Contains(actionId) ? null : _applied.Contains(actionId);
            return new ActionState(actionId, applied, _current.TryGetValue(actionId, out var text) ? text : "Unknown");
        }
    }

    public async Task<OperationResult> ApplyAsync(string actionId, CancellationToken cancellationToken)
    {
        if (FindProvider(actionId) is { } p)
        {
            return await p.ApplyAsync(actionId, cancellationToken).ConfigureAwait(false);
        }

        var action = OptimizationCatalog.Find(actionId);
        if (action is null)
        {
            return OperationResult.Fail($"\"{actionId}\" is not an optimization this version knows about.");
        }

        if (actionId == "tempfiles.clean")
        {
            return OperationResult.Fail("Use the Cleanup page to choose what to remove.");
        }

        if (OnDemand.Contains(actionId))
        {
            await StreamDemoOutputAsync(actionId, cancellationToken).ConfigureAwait(false);
            return OperationResult.Ok($"{action.Title} finished (demo).", action.RequiresRestart);
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _applied.Add(actionId);
            if (OptimizationCatalog.Opposites.TryGetValue(actionId, out var opposite))
            {
                _applied.Remove(opposite);
            }

            UpdateDemoState(actionId, applied: true);
        }

        return OperationResult.Ok($"Applied \"{action.Title}\" (demo).", action.RequiresRestart);
    }

    public async Task<OperationResult> RevertAsync(string actionId, CancellationToken cancellationToken)
    {
        if (FindProvider(actionId) is { } p)
        {
            return await p.RevertAsync(actionId, cancellationToken).ConfigureAwait(false);
        }

        var action = OptimizationCatalog.Find(actionId);
        if (action is not null && !action.Reversible)
        {
            return OperationResult.Fail($"\"{action.Title}\" cannot be undone from here.");
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _applied.Remove(actionId);
            if (OptimizationCatalog.Opposites.TryGetValue(actionId, out var opposite))
            {
                _applied.Add(opposite);
            }

            UpdateDemoState(actionId, applied: false);
        }

        return OperationResult.Ok($"Reverted \"{action?.Title ?? actionId}\" (demo).");
    }

    public async Task<OperationResult> SetStartupItemEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken)
    {
        if (_startup is not null)
        {
            return await _startup.SetStartupItemEnabledAsync(item, enabled, cancellationToken).ConfigureAwait(false);
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok($"{item.Name} is now {(enabled ? "enabled" : "disabled")} at startup (demo).");
    }

    public async Task<OperationResult> SetServiceStartModeAsync(string serviceName, string startMode, CancellationToken cancellationToken)
    {
        if (ServiceClassifier.ToScArgument(startMode) is null)
        {
            return OperationResult.Fail($"\"{startMode}\" is not a start mode that can be set.");
        }

        if (_startup is not null)
        {
            return await _startup.SetServiceStartModeAsync(serviceName, startMode, cancellationToken).ConfigureAwait(false);
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok($"Service {serviceName} start mode set to {startMode} (demo).");
    }

    private void UpdateDemoState(string actionId, bool applied)
    {
        switch (actionId)
        {
            case "power.balanced":
            case "power.highperformance":
            case "power.ultimate":
                string plan = applied ? PowerPlanName(actionId) : "Balanced";
                foreach (var id in new[] { "power.balanced", "power.highperformance", "power.ultimate" })
                {
                    _current[id] = "Active: " + plan;
                    if (applied)
                    {
                        _applied.Remove(id);
                    }
                }

                if (applied)
                {
                    _applied.Add(actionId);
                }
                else
                {
                    _applied.Add("power.balanced");
                }

                break;
            case "visuals.performance":
            case "visuals.balanced":
                string fx = (actionId == "visuals.performance") == applied ? "Best performance" : "Let Windows choose";
                _current["visuals.performance"] = fx;
                _current["visuals.balanced"] = fx;
                break;
            case "faststartup.disable":
            case "faststartup.enable":
                string fs = (actionId == "faststartup.enable") == applied ? "Fast startup on" : "Fast startup off";
                _current["faststartup.disable"] = fs;
                _current["faststartup.enable"] = fs;
                break;
            case "transparency.disable": _current[actionId] = applied ? "Transparency off" : "Transparency on"; break;
            case "animations.disable": _current[actionId] = applied ? "Animations off" : "Animations on"; break;
            case "hibernation.disable": _current[actionId] = applied ? "Hibernation off" : "Hibernation on"; break;
            case "gamemode.enable": _current[actionId] = applied ? "Game Mode on" : "Game Mode off"; break;
            case "gamebar.disable": _current[actionId] = applied ? "Game Bar off" : "Game Bar on"; break;
            case "backgroundapps.disable": _current[actionId] = applied ? "Background apps blocked" : "Background apps allowed"; break;
            case "storagesense.enable": _current[actionId] = applied ? "Storage Sense on" : "Storage Sense off"; break;
            case "notifications.focus": _current[actionId] = applied ? "Tips and suggestions off" : "Tips and suggestions on"; break;
            case "trim.enable": _current[actionId] = applied ? "TRIM on" : "TRIM off"; break;
            case "dns.cloudflare": _current[actionId] = applied ? "Cloudflare DNS in use" : "Automatic (DHCP) DNS"; break;
            case "telemetry.minimal": _current[actionId] = applied ? "Diagnostic data: required only" : "Diagnostic data: Windows default"; break;
            case "sysmain.disable": _current[actionId] = applied ? "Disabled, stopped" : "Automatic, running"; break;
            case "search.indexing.disable": _current[actionId] = applied ? "Disabled, stopped" : "Automatic (Delayed), running"; break;
        }
    }

    private static string PowerPlanName(string actionId) => actionId switch
    {
        "power.highperformance" => "High performance",
        "power.ultimate" => "Ultimate Performance",
        _ => "Balanced",
    };

    private async Task StreamDemoOutputAsync(string actionId, CancellationToken cancellationToken)
    {
        string[] lines = actionId switch
        {
            "dism.restorehealth" =>
            [
                "> DISM /Online /Cleanup-Image /RestoreHealth",
                "Deployment Image Servicing and Management tool",
                "Version: 10.0.26100.1",
                "Image Version: 10.0.26100.2314",
                "[==========================100.0%==========================]",
                "The restore operation completed successfully.",
                "The operation completed successfully.",
            ],
            "sfc.scannow" =>
            [
                "> sfc /scannow",
                "Beginning system scan.  This process will take some time.",
                "Beginning verification phase of system scan.",
                "Verification 100% complete.",
                "Windows Resource Protection did not find any integrity violations.",
            ],
            "defrag.optimize" =>
            [
                "Optimizing all fixed drives (TRIM for SSDs, defragment for hard disks)…",
                "Invoking retrim on (C:)...",
                "Retrim:  100% complete.",
                "Invoking defragmentation on Games (D:)...",
                "Pre-Optimization Report: Total fragmented space = 3%",
                "Post Defragmentation Report: Total fragmented space = 0%",
                "The operation completed successfully.",
            ],
            "network.reset" =>
            [
                "> netsh winsock reset",
                "Successfully reset the Winsock Catalog.",
                "You must restart the computer in order to complete the reset.",
                "> netsh int ip reset",
                "Resetting Interface, OK!",
                "Resetting Neighbor, OK!",
                "Resetting Path, OK!",
                "> ipconfig /flushdns",
                "Successfully flushed the DNS Resolver Cache.",
            ],
            _ => [],
        };

        foreach (var line in lines)
        {
            await Task.Delay(180, cancellationToken).ConfigureAwait(false);
            OutputLine?.Invoke(this, line);
        }
    }

    private IOptimizationActionProvider? FindProvider(string actionId) => _providers.FirstOrDefault(p => p.Handles(actionId));
}
