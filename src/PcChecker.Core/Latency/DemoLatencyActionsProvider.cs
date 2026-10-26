using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Latency;

/// <summary>
/// Demo provider: remembers which latency fixes were applied in memory and never touches the system. The
/// <see cref="DemoLatencyService"/> reads the same state so a re-run after "Fix" shows the finding gone.
/// </summary>
public sealed class DemoLatencyActionsProvider : IOptimizationActionProvider
{
    private readonly HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase)
    {
        LatencyActionIds.HagsOff, // the sample machine starts with GPU scheduling off
    };

    public IReadOnlyList<OptimizationAction> Actions => LatencyActionCatalog.All;

    public bool Handles(string actionId) => LatencyActionCatalog.Handles(actionId);

    /// <summary>True when the demo machine currently has this action applied.</summary>
    public bool IsApplied(string actionId)
    {
        lock (_applied)
        {
            return _applied.Contains(actionId);
        }
    }

    public async Task<ActionState> GetStateAsync(string id, CancellationToken cancellationToken)
    {
        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        if (LatencyActionCatalog.Find(id) is null)
        {
            return new ActionState(id, null, "Unknown action");
        }

        if (id == LatencyActionIds.TimerResolutionHint)
        {
            return new ActionState(id, null, "Current 1.00 ms (finest 0.50 ms)");
        }

        bool applied = IsApplied(id);
        return new ActionState(id, applied, applied ? "Applied" : "Not applied");
    }

    public async Task<OperationResult> ApplyAsync(string id, CancellationToken cancellationToken)
    {
        var action = LatencyActionCatalog.Find(id);
        if (action is null)
        {
            return OperationResult.Fail($"The Latency module does not know the action '{id}'.");
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        if (action.Id == LatencyActionIds.TimerResolutionHint)
        {
            return OperationResult.Ok("Nothing was changed. Windows 11 raises the timer automatically for games that ask for it.");
        }

        lock (_applied)
        {
            _applied.Add(action.Id);
            // HAGS on/off are two sides of one switch.
            _applied.Remove(action.Id == LatencyActionIds.HagsOn ? LatencyActionIds.HagsOff : action.Id == LatencyActionIds.HagsOff ? LatencyActionIds.HagsOn : string.Empty);
        }

        string message = $"{action.Title}: done (demo).";
        if (action.RequiresRestart)
        {
            message += " Restart the PC for it to take effect.";
        }

        return OperationResult.Ok(message, action.RequiresRestart);
    }

    public async Task<OperationResult> RevertAsync(string id, CancellationToken cancellationToken)
    {
        var action = LatencyActionCatalog.Find(id);
        if (action is null)
        {
            return OperationResult.Fail($"The Latency module does not know the action '{id}'.");
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_applied)
        {
            _applied.Remove(action.Id);
        }

        return OperationResult.Ok($"{action.Title}: reverted (demo).", action.RequiresRestart);
    }
}
