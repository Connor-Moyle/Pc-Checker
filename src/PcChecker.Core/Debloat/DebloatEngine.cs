using PcChecker.Core.Common;
using PcChecker.Core.WindowsFeatures;

namespace PcChecker.Core.Debloat;

/// <summary>
/// Applies and reverts the operations of catalog tweaks on an <see cref="IDebloatSystem"/>: build and precondition
/// gating, capture of the previous values before anything changes, operation by operation application with a
/// per-tweak result, and revert from the captured values (or the documented Windows default when nothing was captured).
/// It does not create restore points or confirm with the user; <see cref="WindowsDebloatService"/> wraps it in
/// <c>IChangeGuard</c>. Delegated tweaks (Optimize page actions) are not handled here.
/// </summary>
public sealed class DebloatEngine
{
    private const string Enabled = "enabled";
    private const string Disabled = "disabled";

    private readonly IDebloatSystem _system;
    private readonly DebloatCaptureBook _captures;

    public DebloatEngine(IDebloatSystem system, DebloatCaptureBook captures)
    {
        _system = system;
        _captures = captures;
    }

    public IDebloatSystem System => _system;
    public DebloatCaptureBook Captures => _captures;

    private bool _inBatch;
    private IReadOnlySet<string>? _batchInstalledApps;
    private bool _batchInstalledAppsForAllUsers;

    /// <summary>
    /// Starts a batch: the installed-app list is read once for the whole batch instead of once per app removal (each
    /// read is a PowerShell query). Call <see cref="EndBatch"/> when the batch is done.
    /// </summary>
    public void BeginBatch()
    {
        _inBatch = true;
        _batchInstalledApps = null;
    }

    public void EndBatch()
    {
        _inBatch = false;
        _batchInstalledApps = null;
    }

    /// <summary>When true, app removal also removes the app for other users and new accounts.</summary>
    public bool RemoveAppsForAllUsers { get; set; }

    /// <summary>Capture-file key that remembers whether a key existed before (for operations that delete a key on revert).</summary>
    public static string KeyExistenceKey(RegistryValueOp op) => $"key:{RegistryValueCodec.HiveName(op.Hive)}\\{op.DeleteKeyOnRevert}";

    /// <summary>Reads tasks, apps, features, detections and printers needed by <paramref name="tweaks"/> in one pass.</summary>
    public async Task<DebloatSnapshot> ReadSnapshotAsync(IEnumerable<DebloatTweak> tweaks, CancellationToken cancellationToken)
    {
        int? build = _system.OsBuild;
        var supported = tweaks.Where(t => DebloatRules.IsSupported(t, build)).ToList();
        var ops = supported.SelectMany(t => t.Operations).ToList();

        var taskPaths = ops.OfType<ScheduledTaskOp>().Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tasksTask = taskPaths.Count > 0 ? _system.GetTaskStatesAsync(taskPaths, cancellationToken) : Task.FromResult<IReadOnlyDictionary<string, bool>?>(null);
        var appsTask = ops.OfType<AppxRemovalOp>().Any() ? _system.GetInstalledAppsAsync(cancellationToken) : Task.FromResult<IReadOnlySet<string>?>(null);

        var features = new Dictionary<string, FeatureState>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in ops.OfType<OptionalFeatureOp>().DistinctBy(f => f.FeatureName, StringComparer.OrdinalIgnoreCase))
        {
            features[feature.FeatureName] = await SafeAsync(() => _system.GetOptionalFeatureStateAsync(feature.FeatureName, cancellationToken), FeatureState.Unknown).ConfigureAwait(false);
        }

        var detections = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        foreach (var ps in ops.OfType<PowerShellOp>())
        {
            detections[ps.Key] = await SafeAsync(() => _system.DetectAsync(ps, cancellationToken), null).ConfigureAwait(false);
        }

        var printers = supported.Any(t => t.Precondition == DebloatPrecondition.NoPhysicalPrinters) ? SafePrinters() : [];
        var taskStates = await SafeAsync(() => tasksTask, null).ConfigureAwait(false);
        var apps = await SafeAsync(() => appsTask, null).ConfigureAwait(false);
        return new DebloatSnapshot(build, taskStates, apps, features, detections, printers);
    }

    /// <summary>States of the given (non-delegated) tweaks.</summary>
    public async Task<IReadOnlyDictionary<string, TweakState>> GetStatesAsync(IEnumerable<DebloatTweak> tweaks, CancellationToken cancellationToken)
    {
        var list = tweaks.Where(t => t.DelegatesToOptimizationId is null).ToList();
        var snapshot = await ReadSnapshotAsync(list, cancellationToken).ConfigureAwait(false);
        return list.ToDictionary(t => t.Id, t => DebloatStateEvaluator.Evaluate(t, _system.Registry, snapshot), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Applies one tweak: gate, capture, then every operation. Never throws except for cancellation.</summary>
    public async Task<TweakRunResult> ApplyAsync(DebloatTweak tweak, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        if (tweak.DelegatesToOptimizationId is not null)
        {
            return Result(tweak, TweakOutcome.Skipped, "Handled by the Optimize page.");
        }

        if (!DebloatRules.IsSupported(tweak, _system.OsBuild))
        {
            return Result(tweak, TweakOutcome.Skipped, DebloatRules.UnsupportedReason(tweak, _system.OsBuild) + ".");
        }

        if (DebloatRules.PreconditionFailure(tweak, SafePrinters()) is string blocked)
        {
            return Result(tweak, TweakOutcome.Skipped, blocked);
        }

        var tasks = await ReadTasksAsync(tweak, cancellationToken).ConfigureAwait(false);
        var (capture, before) = await CaptureAsync(tweak, tasks, cancellationToken).ConfigureAwait(false);
        bool newCapture;
        try
        {
            newCapture = tweak.Reversible && _captures.TryAdd(tweak.Id, capture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result(tweak, TweakOutcome.Failed, $"Nothing was changed: the undo information could not be saved ({ex.Message}).");
        }

        int changed = 0, skipped = 0;
        var errors = new List<string>();
        foreach (var op in tweak.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (outcome, message) = await ApplyOperationAsync(op, tasks, cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case true: changed++; break;
                case null: skipped++; break;
                default: errors.Add(message ?? op.Describe()); break;
            }
        }

        string after = string.Join("; ", tweak.Operations.Select(o => o.Describe()));
        if (errors.Count == 0 && changed > 0)
        {
            return Result(tweak, TweakOutcome.Applied, skipped == 0 ? "Applied." : $"Applied ({skipped} part{(skipped == 1 ? " is" : "s are")} not present on this PC).", before, after);
        }

        if (changed == 0 && newCapture)
        {
            _captures.Remove(tweak.Id); // best effort; a leftover entry is harmless
        }

        if (errors.Count == 0)
        {
            return Result(tweak, TweakOutcome.Skipped, "Nothing to change: the items this tweak targets are not present on this PC.", before, after);
        }

        var prefix = changed > 0 ? $"{changed} of {changed + errors.Count} changes made. " : string.Empty;
        return Result(tweak, TweakOutcome.Failed, prefix + errors[0], before, after);
    }

    /// <summary>Reverts one tweak from its captured values, or the Windows defaults when nothing was captured.</summary>
    public async Task<TweakRunResult> RevertAsync(DebloatTweak tweak, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        if (tweak.DelegatesToOptimizationId is not null)
        {
            return Result(tweak, TweakOutcome.Skipped, "Handled by the Optimize page.");
        }

        if (!tweak.Reversible)
        {
            return Result(tweak, TweakOutcome.Skipped, UndoHint(tweak));
        }

        var capture = _captures.Get(tweak.Id);
        var tasks = await ReadTasksAsync(tweak, cancellationToken).ConfigureAwait(false);
        int changed = 0;
        var errors = new List<string>();
        foreach (var op in tweak.Operations.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (outcome, message) = await RevertOperationAsync(op, capture, tasks, cancellationToken).ConfigureAwait(false);
            if (outcome == true)
            {
                changed++;
            }
            else if (outcome == false)
            {
                errors.Add(message ?? op.Describe());
            }
        }

        if (errors.Count > 0)
        {
            return Result(tweak, TweakOutcome.Failed, (changed > 0 ? $"{changed} of {changed + errors.Count} values restored. " : string.Empty) + errors[0]);
        }

        string? saveNote = _captures.Remove(tweak.Id);
        string reverted = capture is null
            ? "Reverted to the Windows default (no values had been saved before the change)."
            : "Restored the values saved before the change.";
        return Result(tweak, TweakOutcome.Reverted, saveNote is null ? reverted : $"{reverted} {saveNote}");
    }

    /// <summary>How to undo a one-way tweak by hand.</summary>
    public static string UndoHint(DebloatTweak tweak)
    {
        var app = tweak.Operations.OfType<AppxRemovalOp>().FirstOrDefault();
        if (app is not null)
        {
            return $"Removed apps cannot be put back from here. Reinstall {app.FriendlyName} from the Microsoft Store.";
        }

        return tweak.Operations.OfType<PowerShellOp>().Select(p => p.UndoHint).FirstOrDefault(h => h is not null) ?? "This change cannot be undone from here.";
    }

    // ---- capture -------------------------------------------------------------------------------------------------

    private async Task<(Dictionary<string, string?> Capture, string Before)> CaptureAsync(DebloatTweak tweak, IReadOnlyDictionary<string, bool>? tasks, CancellationToken cancellationToken)
    {
        var capture = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var before = new List<string>();
        foreach (var op in tweak.Operations)
        {
            switch (op)
            {
                case RegistryValueOp reg:
                {
                    var current = SafeRead(reg);
                    capture[reg.Key] = RegistryValueCodec.Encode(current);
                    if (reg.DeleteKeyOnRevert is not null)
                    {
                        capture[KeyExistenceKey(reg)] = SafeKeyExists(reg.Hive, reg.DeleteKeyOnRevert) ? "1" : null;
                    }

                    before.Add($"{reg.Name}={RegistryValueCodec.Describe(current)}");
                    break;
                }

                case ServiceOp svc:
                {
                    var mode = DebloatStateEvaluator.ReadStartMode(_system.Registry, svc.Name);
                    capture[svc.Key] = mode?.ToString();
                    before.Add($"{svc.Name}={(mode is null ? "not installed" : ServiceStartModes.Display(mode.Value))}");
                    break;
                }

                case ScheduledTaskOp task when tasks is not null && tasks.TryGetValue(task.Path, out bool enabled):
                    capture[task.Key] = enabled ? Enabled : Disabled;
                    before.Add($"{task.Path}={(enabled ? Enabled : Disabled)}");
                    break;

                case OptionalFeatureOp feature:
                {
                    var state = await SafeAsync(() => _system.GetOptionalFeatureStateAsync(feature.FeatureName, cancellationToken), FeatureState.Unknown).ConfigureAwait(false);
                    capture[feature.Key] = state.ToString();
                    before.Add($"{feature.FeatureName}={state}");
                    break;
                }
            }
        }

        return (capture, string.Join("; ", before));
    }

    // ---- apply ---------------------------------------------------------------------------------------------------

    /// <summary>true = changed, null = skipped (not present), false = failed with message.</summary>
    private async Task<(bool? Outcome, string? Message)> ApplyOperationAsync(DebloatOperation op, IReadOnlyDictionary<string, bool>? tasks, CancellationToken cancellationToken)
    {
        switch (op)
        {
            case RegistryValueOp reg:
                return Try(() =>
                {
                    if (reg.AppliedValue is null)
                    {
                        _system.Registry.DeleteValue(reg.Hive, reg.Path, reg.Name);
                    }
                    else
                    {
                        _system.Registry.Write(reg.Hive, reg.Path, reg.Name, new RegistryData(reg.Kind, reg.AppliedValue));
                    }
                }, reg);

            case ServiceOp svc:
                if (DebloatStateEvaluator.ReadStartMode(_system.Registry, svc.Name) is null)
                {
                    return (null, null);
                }

                return FromResult(await _system.SetServiceStartModeAsync(svc.Name, svc.AppliedMode, cancellationToken).ConfigureAwait(false));

            case ScheduledTaskOp task:
                if (tasks is not null && !tasks.ContainsKey(task.Path))
                {
                    return (null, null);
                }

                return FromResult(await _system.SetTaskEnabledAsync(task.Path, !task.DisableWhenApplied, cancellationToken).ConfigureAwait(false));

            case AppxRemovalOp app:
            {
                // A removal for all users also acts on other accounts and the provisioned package, so the app counts as
                // present when any of those still has it, even if the current account removed it already.
                var installed = await InstalledAppsAsync(RemoveAppsForAllUsers, cancellationToken).ConfigureAwait(false);
                if (installed is not null && !installed.Contains(app.PackageName))
                {
                    return (null, null);
                }

                return FromResult(await _system.RemoveAppAsync(app, RemoveAppsForAllUsers, cancellationToken).ConfigureAwait(false));
            }

            case OptionalFeatureOp feature:
            {
                var state = await SafeAsync(() => _system.GetOptionalFeatureStateAsync(feature.FeatureName, cancellationToken), FeatureState.Unknown).ConfigureAwait(false);
                if (state == FeatureState.NotPresent)
                {
                    return (null, null);
                }

                return FromResult(await _system.SetOptionalFeatureAsync(feature, enabled: !feature.DisableWhenApplied, cancellationToken).ConfigureAwait(false));
            }

            case PowerShellOp ps:
                return FromResult(await _system.RunScriptAsync(ps.ApplyScript, cancellationToken).ConfigureAwait(false));

            default:
                return (false, "Unknown operation type.");
        }
    }

    // ---- revert --------------------------------------------------------------------------------------------------

    private async Task<(bool? Outcome, string? Message)> RevertOperationAsync(DebloatOperation op, IReadOnlyDictionary<string, string?>? capture,
        IReadOnlyDictionary<string, bool>? tasks, CancellationToken cancellationToken)
    {
        switch (op)
        {
            case RegistryValueOp reg:
                return Try(() => RevertRegistry(reg, capture), reg);

            case ServiceOp svc:
            {
                if (DebloatStateEvaluator.ReadStartMode(_system.Registry, svc.Name) is null)
                {
                    return (null, null);
                }

                var target = capture is not null && capture.TryGetValue(svc.Key, out var saved) && ServiceStartModes.TryParse(saved, out var parsed) ? parsed : svc.DefaultMode;
                return FromResult(await _system.SetServiceStartModeAsync(svc.Name, target, cancellationToken).ConfigureAwait(false));
            }

            case ScheduledTaskOp task:
            {
                if (tasks is not null && !tasks.ContainsKey(task.Path))
                {
                    return (null, null);
                }

                bool enable = capture is not null && capture.TryGetValue(task.Key, out var saved) && saved is not null
                    ? saved.Equals(Enabled, StringComparison.OrdinalIgnoreCase)
                    : task.DisableWhenApplied;
                return FromResult(await _system.SetTaskEnabledAsync(task.Path, enable, cancellationToken).ConfigureAwait(false));
            }

            case OptionalFeatureOp feature:
            {
                var state = await SafeAsync(() => _system.GetOptionalFeatureStateAsync(feature.FeatureName, cancellationToken), FeatureState.Unknown).ConfigureAwait(false);
                if (state == FeatureState.NotPresent)
                {
                    return (null, null);
                }

                bool enable = capture is not null && capture.TryGetValue(feature.Key, out var saved) && Enum.TryParse<FeatureState>(saved, true, out var previous)
                    ? previous == FeatureState.Enabled
                    : feature.DisableWhenApplied;
                return FromResult(await _system.SetOptionalFeatureAsync(feature, enable, cancellationToken).ConfigureAwait(false));
            }

            case PowerShellOp { RevertScript: not null } ps:
                return FromResult(await _system.RunScriptAsync(ps.RevertScript, cancellationToken).ConfigureAwait(false));

            default:
                return (null, null);
        }
    }

    private void RevertRegistry(RegistryValueOp reg, IReadOnlyDictionary<string, string?>? capture)
    {
        if (capture is not null && capture.TryGetValue(reg.Key, out var saved))
        {
            if (reg.DeleteKeyOnRevert is not null && capture.TryGetValue(KeyExistenceKey(reg), out var existed) && existed is null)
            {
                _system.Registry.DeleteKeyTree(reg.Hive, reg.DeleteKeyOnRevert);
                return;
            }

            var previous = RegistryValueCodec.Decode(saved);
            if (previous is null)
            {
                _system.Registry.DeleteValue(reg.Hive, reg.Path, reg.Name);
            }
            else
            {
                _system.Registry.Write(reg.Hive, reg.Path, reg.Name, previous);
            }

            return;
        }

        if (reg.DefaultValue is null)
        {
            if (reg.DeleteKeyOnRevert is not null)
            {
                _system.Registry.DeleteKeyTree(reg.Hive, reg.DeleteKeyOnRevert);
            }
            else
            {
                _system.Registry.DeleteValue(reg.Hive, reg.Path, reg.Name);
            }
        }
        else
        {
            _system.Registry.Write(reg.Hive, reg.Path, reg.Name, new RegistryData(reg.Kind, reg.DefaultValue));
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private async Task<IReadOnlySet<string>?> InstalledAppsAsync(bool allUsers, CancellationToken cancellationToken)
    {
        if (_inBatch && _batchInstalledApps is not null && _batchInstalledAppsForAllUsers == allUsers)
        {
            return _batchInstalledApps;
        }

        var installed = await SafeAsync(() => allUsers ? _system.GetInstalledAppsForAllUsersAsync(cancellationToken) : _system.GetInstalledAppsAsync(cancellationToken), null)
            .ConfigureAwait(false);
        if (_inBatch)
        {
            _batchInstalledApps = installed;
            _batchInstalledAppsForAllUsers = allUsers;
        }

        return installed;
    }

    private async Task<IReadOnlyDictionary<string, bool>?> ReadTasksAsync(DebloatTweak tweak, CancellationToken cancellationToken)
    {
        var paths = tweak.Operations.OfType<ScheduledTaskOp>().Select(t => t.Path).ToList();
        return paths.Count == 0 ? null : await SafeAsync(() => _system.GetTaskStatesAsync(paths, cancellationToken), null).ConfigureAwait(false);
    }

    private RegistryData? SafeRead(RegistryValueOp reg)
    {
        try
        {
            return _system.Registry.Read(reg.Hive, reg.Path, reg.Name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool SafeKeyExists(DebloatHive hive, string path)
    {
        try
        {
            return _system.Registry.KeyExists(hive, path);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private IReadOnlyList<string> SafePrinters()
    {
        try
        {
            return _system.GetPrinterNames();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static (bool?, string?) Try(Action action, RegistryValueOp reg)
    {
        try
        {
            action();
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var where = $"{RegistryValueCodec.HiveName(reg.Hive)}\\{reg.Path}";
            return (false, ex is UnauthorizedAccessException or global::System.Security.SecurityException
                ? $"Windows blocked the change to {where} (access denied)."
                : $"Could not change {where}: {ex.Message}");
        }
    }

    private static (bool?, string?) FromResult(OperationResult result) => (result.Success ? true : false, result.Success ? null : result.Message);

    private static async Task<T> SafeAsync<T>(Func<Task<T>> read, T fallback)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static TweakRunResult Result(DebloatTweak tweak, TweakOutcome outcome, string message, string? before = null, string? after = null) =>
        new(tweak.Id, tweak.Title, outcome, message, before, after);
}
