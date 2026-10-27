using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Debloat;

/// <summary>
/// Shared batch logic of the Windows and Demo debloat services. The whole batch runs inside ONE guarded change, so the
/// restore point is created before any tweak in it. Inside it, tweaks with their own registry/service/task/script
/// operations run first (each captured before it is changed), then the tweaks done by another PC Checker service
/// (Optimize page actions, app and Windows feature removal). Cancelling stops between tweaks, never in the middle of
/// one. Every tweak gets its own result, and derived classes journal one record per tweak.
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

    public virtual string? UserScopeNote => Engine.System.CurrentUserNote;

    public virtual string SystemDrive => "C:";

    /// <summary>Default: no restore point problem known (demo mode and tests).</summary>
    public virtual Task<DebloatRestorePointCheck> CheckRestorePointAsync(IReadOnlyList<string> tweakIds, bool revert, CancellationToken cancellationToken) =>
        Task.FromResult(DebloatRestorePointCheck.Fine);

    public abstract Task<OperationResult> TurnOnSystemProtectionAsync(CancellationToken cancellationToken);

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

    /// <summary>
    /// States of <paramref name="tweakIds"/> and of the tweaks that touch the same settings (so a tweak whose value was
    /// overwritten by another one also shows its new state). Used after a run instead of re-reading everything.
    /// </summary>
    public virtual async Task<IReadOnlyDictionary<string, TweakState>> GetStatesAsync(IEnumerable<string> tweakIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tweakIds);
        var wanted = DebloatRules.WithRelated(tweakIds, Tweaks);
        var states = new Dictionary<string, TweakState>(StringComparer.OrdinalIgnoreCase);
        var own = await Task.Run(() => Engine.GetStatesAsync(wanted.Where(t => t.DelegatesToOptimizationId is null), cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var (id, state) in own)
        {
            states[id] = state;
        }

        foreach (var tweak in wanted.Where(t => t.DelegatesToOptimizationId is not null))
        {
            states[tweak.Id] = await GetDelegatedStateSafeAsync(tweak, cancellationToken).ConfigureAwait(false);
        }

        return states;
    }

    public Task<DebloatRunResult> ApplyAsync(IReadOnlyList<string> tweakIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        RunBatchOffUiAsync(tweakIds, revert: false, DebloatRunOptions.Default, progress, cancellationToken);

    public Task<DebloatRunResult> RevertAsync(IReadOnlyList<string> tweakIds, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        RunBatchOffUiAsync(tweakIds, revert: true, DebloatRunOptions.Default, progress, cancellationToken);

    public Task<DebloatRunResult> ApplyAsync(IReadOnlyList<string> tweakIds, DebloatRunOptions options, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        RunBatchOffUiAsync(tweakIds, revert: false, options ?? DebloatRunOptions.Default, progress, cancellationToken);

    public Task<DebloatRunResult> RevertAsync(IReadOnlyList<string> tweakIds, DebloatRunOptions options, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        RunBatchOffUiAsync(tweakIds, revert: true, options ?? DebloatRunOptions.Default, progress, cancellationToken);

    /// <summary>
    /// Runs the batch on a worker thread. Without a restore point to await first (Continue without), the guard calls the
    /// work synchronously, and the registry captures, capture-file saves and user-hive lookup would otherwise block the
    /// UI thread (and freeze the busy overlay) until the first truly asynchronous step. Progress still reaches the page
    /// through the caller's <see cref="IProgress{T}"/>. The token is not passed to Task.Run so a cancel before the
    /// start is reported per tweak like any other cancel.
    /// </summary>
    private Task<DebloatRunResult> RunBatchOffUiAsync(IReadOnlyList<string> tweakIds, bool revert, DebloatRunOptions options, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => RunBatchAsync(tweakIds, revert, options, progress, cancellationToken));

    public abstract Task<OperationResult> RestartExplorerAsync(CancellationToken cancellationToken);

    public abstract OperationResult OpenSettings(string settingsUri);

    public string ExportSelection(IEnumerable<string> ids) => DebloatSelectionFormat.Export(ids);

    public (IReadOnlyList<string> Ids, IReadOnlyList<string> Unmapped) ImportSelection(string json)
    {
        var result = DebloatSelectionFormat.Import(json, Tweaks.Select(t => t.Id));
        return (result.Ids, result.Unmapped);
    }

    /// <summary>
    /// Runs the whole batch inside the derived class's change guard (restore point, journal, log). With
    /// <paramref name="createRestorePoint"/> false (the user chose to continue without one) no restore point is attempted.
    /// </summary>
    protected abstract Task<OperationResult<IReadOnlyList<TweakRunResult>>> RunGuardedAsync(string description, IReadOnlyList<DebloatTweak> tweaks, bool revert,
        bool createRestorePoint, Func<CancellationToken, Task<IReadOnlyList<TweakRunResult>>> work, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

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

    /// <summary>The message of a tweak left out because the user chose to continue without a restore point.</summary>
    public const string NeedsRestorePointMessage =
        "Not run: this change cannot be undone from this page, so it only runs with a restore point. Turn on System Protection and apply it again.";

    private async Task<DebloatRunResult> RunBatchAsync(IReadOnlyList<string> tweakIds, bool revert, DebloatRunOptions options, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tweakIds);
        var results = new List<TweakRunResult>();
        var tweaks = new List<DebloatTweak>();
        foreach (var id in tweakIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (DebloatCatalog.Find(id) is not { } tweak)
            {
                results.Add(new TweakRunResult(id, id, TweakOutcome.Skipped, "Not a tweak this version of PC Checker knows."));
            }
            else if (options.WithoutRestorePoint && DebloatRules.NeedsRestorePoint(tweak, revert))
            {
                results.Add(new TweakRunResult(tweak.Id, tweak.Title, TweakOutcome.Skipped, NeedsRestorePointMessage));
            }
            else
            {
                tweaks.Add(tweak);
            }
        }

        if (tweaks.Count == 0)
        {
            return results.Count == 0 ? DebloatRunResult.Empty : Finish(results, revert, null);
        }

        string verb = revert ? "Reverting" : "Applying";
        int total = tweaks.Count;

        // In-place tweaks first (each is captured and reversible), then the ones done through another service. The whole
        // batch is ONE guarded change, so the restore point is created before anything in it, app removals included.
        var ordered = tweaks.Where(t => !t.IsExternal).Concat(tweaks.Where(t => t.IsExternal)).ToList();
        var description = $"{(revert ? "Revert" : "Apply")} {ordered.Count} debloat tweak{(ordered.Count == 1 ? string.Empty : "s")}";
        string? guardNote = null;
        bool started = false;

        Engine.BeginBatch();
        OperationResult<IReadOnlyList<TweakRunResult>> guarded;
        try
        {
            guarded = await RunGuardedAsync(description, ordered, revert, !options.WithoutRestorePoint, async ct =>
            {
                started = true;
                var batch = new List<TweakRunResult>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var tweak = ordered[i];
                    if (ct.IsCancellationRequested)
                    {
                        // Stop between tweaks: the ones already done stay done (and can be reverted), the rest are not started.
                        batch.AddRange(ordered.Skip(i).Select(t => new TweakRunResult(t.Id, t.Title, TweakOutcome.Skipped, "Cancelled before it started.")));
                        break;
                    }

                    progress?.Report(new ProgressUpdate($"{verb} tweaks", 100.0 * i / total, tweak.Title));

                    // A started tweak always runs to the end so it is never left half applied.
                    batch.Add(await RunTweakAsync(tweak, revert, CancellationToken.None).ConfigureAwait(false));
                }

                return batch;
            }, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Engine.EndBatch();
        }

        string? blockedReason = null;
        if (guarded.Value is { } batchResults)
        {
            results.AddRange(batchResults);
            int noteStart = guarded.Message.IndexOf("(Note:", StringComparison.Ordinal);
            guardNote = noteStart >= 0 ? guarded.Message[noteStart..].Trim('(', ')', ' ') : null;
        }
        else if (!started)
        {
            // The guard refused to start the batch: its required restore point could not be created. Nothing changed;
            // the page asks whether to turn on System Protection, continue without a restore point, or cancel.
            blockedReason = guarded.Message;
            results.AddRange(ordered.Select(t => new TweakRunResult(t.Id, t.Title, TweakOutcome.Skipped,
                "Not started: Windows could not create the restore point this batch needs. Nothing was changed.")));
        }
        else
        {
            results.AddRange(ordered.Select(t => new TweakRunResult(t.Id, t.Title, TweakOutcome.Failed, guarded.Message)));
        }

        // Tell running programs (taskbar, File Explorer, apps reading policies) that settings changed, so whatever can
        // pick them up without a restart does so.
        if (results.Any(r => r.Outcome is TweakOutcome.Applied or TweakOutcome.Reverted && DebloatCatalog.Find(r.TweakId) is { DelegatesToOptimizationId: null }))
        {
            await Task.Run(Engine.System.NotifySettingsChanged, CancellationToken.None).ConfigureAwait(false);
        }

        foreach (var result in results)
        {
            if (DebloatCatalog.Find(result.TweakId) is { } tweak)
            {
                await RecordAsync(tweak, result, revert).ConfigureAwait(false);
            }
        }

        progress?.Report(new ProgressUpdate("Done", 100));
        return Finish(results, revert, guardNote) with { RestorePointBlockedReason = blockedReason };
    }

    private async Task<TweakRunResult> RunTweakAsync(DebloatTweak tweak, bool revert, CancellationToken cancellationToken)
    {
        try
        {
            if (tweak.DelegatesToOptimizationId is not { } optimizationId)
            {
                // The engine reports failures as results; this catch only keeps an unexpected error from failing the
                // whole batch (and marking tweaks that were already applied as failed).
                return revert ? await Engine.RevertAsync(tweak, cancellationToken).ConfigureAwait(false) : await Engine.ApplyAsync(tweak, cancellationToken).ConfigureAwait(false);
            }

            // An Optimize page action that is already in effect is not run again, so it is reported as such rather than
            // as a fresh change that seems to do nothing.
            if (!revert && (await GetDelegatedStateSafeAsync(tweak, cancellationToken).ConfigureAwait(false)).Status == TweakStatus.Applied)
            {
                return new TweakRunResult(tweak.Id, tweak.Title, TweakOutcome.AlreadyApplied, "Already applied: nothing needed to change.");
            }

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
        bool explorer = succeeded.Any(t => DebloatRestartRules.For(t) == DebloatVisibility.RestartExplorer);
        bool reboot = succeeded.Any(t => DebloatRestartRules.For(t) == DebloatVisibility.RestartWindows);
        int ok = succeeded.Count;
        int failed = results.Count(r => r.Outcome == TweakOutcome.Failed);
        int skipped = results.Count(r => r.Outcome == TweakOutcome.Skipped);
        int already = results.Count(r => r.Outcome == TweakOutcome.AlreadyApplied);
        var parts = new List<string> { $"{ok} {(revert ? "reverted" : "applied")}" };
        if (already > 0)
        {
            parts.Add($"{already} already applied");
        }

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

        return new DebloatRunResult(results, explorer, reboot, summary) { Note = string.IsNullOrWhiteSpace(note) ? null : note };
    }
}
