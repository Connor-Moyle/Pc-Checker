using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Debloat;

/// <summary>
/// Shared batch logic of the Windows and Demo debloat services. A batch runs in two phases:
/// <list type="number">
/// <item>Tweaks with their own registry/service/task/script operations run inside ONE guarded change (restore point
/// first), each captured before it is changed.</item>
/// <item>Tweaks done by another PC Checker service (Optimize page actions, app and Windows feature removal) run after
/// that, one by one, through that service, which guards and journals them itself.</item>
/// </list>
/// Every tweak gets its own result, and derived classes journal one record per tweak.
/// </summary>
public abstract class DebloatServiceBase : IDebloatService
{
    protected DebloatServiceBase(DebloatEngine engine)
    {
        Engine = engine;
    }

    protected DebloatEngine Engine { get; }

    public IReadOnlyList<DebloatTweak> Tweaks => DebloatCatalog.All;
    public IReadOnlyList<DebloatPreset> Presets => DebloatPresets.All;
    public IReadOnlyList<DebloatUnsafeItem> NotOffered => DebloatPresets.NotOffered;
    public IReadOnlyList<DebloatNote> Notes => DebloatPresets.Notes;

    public bool RemoveAppsForAllUsers
    {
        get => Engine.RemoveAppsForAllUsers;
        set => Engine.RemoveAppsForAllUsers = value;
    }

    public virtual async Task<IReadOnlyDictionary<string, TweakState>> GetStatesAsync(CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, TweakState>(StringComparer.OrdinalIgnoreCase);
        var own = await Task.Run(() => Engine.GetStatesAsync(Tweaks, cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var (id, state) in own)
        {
            states[id] = state;
        }

        foreach (var tweak in Tweaks.Where(t => t.DelegatesToOptimizationId is not null))
        {
            states[tweak.Id] = await GetDelegatedStateSafeAsync(tweak, cancellationToken).ConfigureAwait(false);
        }

        return states;
    }

    public Task<DebloatRunResult> ApplyAsync(IReadOnlyList<string> tweakIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        RunBatchAsync(tweakIds, revert: false, progress, cancellationToken);

    public Task<DebloatRunResult> RevertAsync(IReadOnlyList<string> tweakIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        RunBatchAsync(tweakIds, revert: true, progress, cancellationToken);

    public abstract Task<OperationResult> RestartExplorerAsync(CancellationToken cancellationToken);

    public abstract OperationResult OpenSettings(string settingsUri);

    public string ExportSelection(IEnumerable<string> ids) => DebloatSelectionFormat.Export(ids);

    public (IReadOnlyList<string> Ids, IReadOnlyList<string> Unmapped) ImportSelection(string json)
    {
        var result = DebloatSelectionFormat.Import(json, Tweaks.Select(t => t.Id));
        return (result.Ids, result.Unmapped);
    }

    /// <summary>Runs the in-place batch inside the derived class's change guard (restore point, journal, log).</summary>
    protected abstract Task<OperationResult<IReadOnlyList<TweakRunResult>>> RunGuardedAsync(string description, IReadOnlyList<DebloatTweak> tweaks, bool revert,
        Func<CancellationToken, Task<IReadOnlyList<TweakRunResult>>> work, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Applies or reverts an Optimize page action this tweak delegates to.</summary>
    protected abstract Task<OperationResult> RunDelegatedAsync(string optimizationId, bool revert, CancellationToken cancellationToken);

    protected abstract Task<ActionState> GetDelegatedStateAsync(string optimizationId, CancellationToken cancellationToken);

    /// <summary>Records one journal entry per tweak (default: nothing).</summary>
    protected virtual Task RecordAsync(DebloatTweak tweak, TweakRunResult result, bool revert) => Task.CompletedTask;

    private async Task<TweakState> GetDelegatedStateSafeAsync(DebloatTweak tweak, CancellationToken cancellationToken)
    {
        try
        {
            var state = await GetDelegatedStateAsync(tweak.DelegatesToOptimizationId!, cancellationToken).ConfigureAwait(false);
            var status = state.IsApplied switch { true => TweakStatus.Applied, false => TweakStatus.NotApplied, _ => TweakStatus.Unknown };
            return new TweakState(tweak.Id, status, state.CurrentValue);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return TweakState.Unknown(tweak.Id, ex.Message);
        }
    }

    private async Task<DebloatRunResult> RunBatchAsync(IReadOnlyList<string> tweakIds, bool revert, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tweakIds);
        var results = new List<TweakRunResult>();
        var tweaks = new List<DebloatTweak>();
        foreach (var id in tweakIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (DebloatCatalog.Find(id) is { } tweak)
            {
                tweaks.Add(tweak);
            }
            else
            {
                results.Add(new TweakRunResult(id, id, TweakOutcome.Skipped, "Not a tweak this version of PC Checker knows."));
            }
        }

        if (tweaks.Count == 0)
        {
            return results.Count == 0 ? DebloatRunResult.Empty : Finish(results, revert, null);
        }

        string verb = revert ? "Reverting" : "Applying";
        int total = tweaks.Count, done = 0;
        var local = tweaks.Where(t => !t.IsExternal).ToList();
        var external = tweaks.Where(t => t.IsExternal).ToList();
        string? guardNote = null;

        if (local.Count > 0)
        {
            var description = $"{(revert ? "Revert" : "Apply")} {local.Count} debloat tweak{(local.Count == 1 ? string.Empty : "s")}";
            var guarded = await RunGuardedAsync(description, local, revert, async ct =>
            {
                var batch = new List<TweakRunResult>();
                foreach (var tweak in local)
                {
                    progress?.Report(new ProgressUpdate($"{verb} tweaks", 100.0 * done++ / total, tweak.Title));
                    batch.Add(revert ? await Engine.RevertAsync(tweak, ct).ConfigureAwait(false) : await Engine.ApplyAsync(tweak, ct).ConfigureAwait(false));
                }

                return batch;
            }, progress, cancellationToken).ConfigureAwait(false);

            if (guarded.Value is { } batchResults)
            {
                results.AddRange(batchResults);
                int noteStart = guarded.Message.IndexOf("(Note:", StringComparison.Ordinal);
                guardNote = noteStart >= 0 ? guarded.Message[noteStart..].Trim('(', ')', ' ') : null;
            }
            else
            {
                results.AddRange(local.Select(t => new TweakRunResult(t.Id, t.Title, TweakOutcome.Failed, guarded.Message)));
            }
        }

        foreach (var tweak in external)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ProgressUpdate($"{verb} tweaks", 100.0 * done++ / total, tweak.Title));
            results.Add(await RunExternalAsync(tweak, revert, cancellationToken).ConfigureAwait(false));
        }

        foreach (var result in results)
        {
            if (DebloatCatalog.Find(result.TweakId) is { } tweak)
            {
                await RecordAsync(tweak, result, revert).ConfigureAwait(false);
            }
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        return Finish(results, revert, guardNote);
    }

    private async Task<TweakRunResult> RunExternalAsync(DebloatTweak tweak, bool revert, CancellationToken cancellationToken)
    {
        if (tweak.DelegatesToOptimizationId is not { } optimizationId)
        {
            return revert ? await Engine.RevertAsync(tweak, cancellationToken).ConfigureAwait(false) : await Engine.ApplyAsync(tweak, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var result = await RunDelegatedAsync(optimizationId, revert, cancellationToken).ConfigureAwait(false);
            var outcome = result.Success ? (revert ? TweakOutcome.Reverted : TweakOutcome.Applied) : TweakOutcome.Failed;
            return new TweakRunResult(tweak.Id, tweak.Title, outcome, result.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TweakRunResult(tweak.Id, tweak.Title, TweakOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Builds the batch summary and the Explorer/restart flags from the per-tweak results.</summary>
    public static DebloatRunResult Finish(IReadOnlyList<TweakRunResult> results, bool revert, string? note)
    {
        var succeeded = results.Where(r => r.Outcome is TweakOutcome.Applied or TweakOutcome.Reverted).Select(r => DebloatCatalog.Find(r.TweakId)).OfType<DebloatTweak>().ToList();
        bool explorer = succeeded.Any(t => t.RestartsExplorer);
        bool reboot = succeeded.Any(t => t.RequiresRestart);
        int ok = succeeded.Count;
        int failed = results.Count(r => r.Outcome == TweakOutcome.Failed);
        int skipped = results.Count(r => r.Outcome == TweakOutcome.Skipped);
        var parts = new List<string> { $"{ok} {(revert ? "reverted" : "applied")}" };
        if (failed > 0)
        {
            parts.Add($"{failed} failed");
        }

        if (skipped > 0)
        {
            parts.Add($"{skipped} skipped");
        }

        var summary = string.Join(", ", parts) + ".";
        if (!string.IsNullOrWhiteSpace(note))
        {
            summary += " " + note;
        }

        return new DebloatRunResult(results, explorer, reboot, summary);
    }
}
