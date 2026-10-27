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

    /// <summary>
    /// The capture-book entry for <paramref name="tweak"/>. Tweaks with per-user values that go to another account's
    /// hive (PC Checker elevated as a different administrator) are kept per account ("id@SID"), so two people who sign
    /// in on the same PC each get their own original values back.
    /// </summary>
    public string CaptureId(DebloatTweak tweak)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        return CaptureIdFor(tweak, tweak.Operations.OfType<RegistryValueOp>().Any(o => o.Hive == DebloatHive.CurrentUser) ? SafeScopeId() : null);
    }

    /// <summary>"id" for the app's own account, "id@scope" for another account's hive.</summary>
    public static string CaptureIdFor(DebloatTweak tweak, string? userScopeId)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        return string.IsNullOrWhiteSpace(userScopeId) ? tweak.Id : $"{tweak.Id}@{userScopeId}";
    }

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
        var appsTask = ops.OfType<AppxRemovalOp>().Any() || supported.Any(t => t.OnlyWithoutApp is not null) ? _system.GetInstalledAppsAsync(cancellationToken) : Task.FromResult<IReadOnlySet<string>?>(null);

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

        if (DebloatRules.RequirementFailure(tweak, _system.Registry) is string requirementMissing)
        {
            return Result(tweak, TweakOutcome.Skipped, requirementMissing + ": nothing to change.");
        }

        if (tweak.OnlyWithoutApp is { } requiredAbsent)
        {
            var installed = await InstalledAppsAsync(allUsers: false, cancellationToken).ConfigureAwait(false);
            if (installed is null || DebloatRules.IsAppInstalled(requiredAbsent, installed))
            {
                return Result(tweak, TweakOutcome.Skipped, installed is null
                    ? "Installed apps could not be read, so this was left alone."
                    : DebloatRules.AppStillInstalledText(requiredAbsent));
            }
        }

        var tasks = await ReadTasksAsync(tweak, cancellationToken).ConfigureAwait(false);
        var (capture, before) = await CaptureAsync(tweak, tasks, cancellationToken).ConfigureAwait(false);
        string captureId = CaptureId(tweak);
        bool newCapture;
        try
        {
            newCapture = tweak.Reversible && _captures.TryAdd(captureId, capture);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without the saved values the change could not be reverted, so nothing is changed.
            return Result(tweak, TweakOutcome.Failed,
                $"Nothing was changed: the undo information could not be saved to {DebloatCaptureBook.FileName} ({ex.Message}). Check that the disk is not full and that security software is not blocking PC Checker's settings folder.");
        }

        int changed = 0, inPlace = 0, missing = 0;
        var errors = new List<string>();
        foreach (var op in tweak.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (outcome, message) = await ApplyOperationAsync(op, tasks, cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case OpOutcome.Changed: changed++; break;
                case OpOutcome.AlreadyInPlace: inPlace++; break;
                case OpOutcome.NotPresent: missing++; break;
                default: errors.Add(message ?? op.Describe()); break;
            }
        }

        string after = string.Join("; ", tweak.Operations.Select(o => o.Describe()));
        if (errors.Count == 0 && changed > 0)
        {
            return Result(tweak, TweakOutcome.Applied, missing == 0 ? "Applied." : $"Applied ({missing} part{(missing == 1 ? " is" : "s are")} not present on this PC).", before, after);
        }

        // Keep the capture when something was already in place: it records the state before the user chose Apply, so a
        // later Revert puts back exactly that (for example Recall stays off when it was off) instead of the defaults.
        if (changed == 0 && inPlace == 0 && newCapture)
        {
            _captures.Remove(captureId); // best effort; a leftover entry is harmless
        }

        if (errors.Count == 0 && inPlace > 0)
        {
            return Result(tweak, TweakOutcome.AlreadyApplied, "Already applied: nothing needed to change.", before, after);
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

        string captureId = CaptureId(tweak);
        var capture = _captures.Get(captureId);
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

        string? saveNote = _captures.Remove(captureId);
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

                case RegistryKeyRemovalOp keyOp:
                {
                    bool exists = SafeKeyExists(keyOp.Hive, keyOp.Path);
                    capture[keyOp.Key] = exists ? "1" : null;
                    before.Add($"{keyOp.Path}={(exists ? "present" : "absent")}");
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

    /// <summary>What happened to one operation when a tweak was applied.</summary>
    private enum OpOutcome
    {
        Changed,
        AlreadyInPlace,
        NotPresent,
        Failed,
    }

    /// <summary>
    /// Applies one operation. An operation whose target is already in place (with the type Windows reads) is not
    /// written again, so a tweak that was already applied is reported as such instead of as a fresh change.
    /// </summary>
    private async Task<(OpOutcome Outcome, string? Message)> ApplyOperationAsync(DebloatOperation op, IReadOnlyDictionary<string, bool>? tasks, CancellationToken cancellationToken)
    {
        switch (op)
        {
            case RegistryValueOp reg:
            {
                if (RegistryValueCodec.Matches(SafeRead(reg), reg.Kind, reg.AppliedValue))
                {
                    return (OpOutcome.AlreadyInPlace, null);
                }

                var (ok, message) = Try(() =>
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
                return (ok ? OpOutcome.Changed : OpOutcome.Failed, message);
            }

            case RegistryKeyRemovalOp keyOp:
            {
                if (!SafeKeyExists(keyOp.Hive, keyOp.Path))
                {
                    return (OpOutcome.AlreadyInPlace, null);
                }

                var (ok, message) = Try(() => _system.Registry.DeleteKeyTree(keyOp.Hive, keyOp.Path), keyOp.Hive, keyOp.Path);
                return (ok ? OpOutcome.Changed : OpOutcome.Failed, message);
            }

            case ServiceOp svc:
            {
                var mode = DebloatStateEvaluator.ReadStartMode(_system.Registry, svc.Name);
                if (mode is null)
                {
                    return (OpOutcome.NotPresent, null);
                }

                if (ServiceStartModes.Satisfies(mode.Value, svc.AppliedMode))
                {
                    return (OpOutcome.AlreadyInPlace, null);
                }

                return FromResult(await _system.SetServiceStartModeAsync(svc.Name, svc.AppliedMode, cancellationToken).ConfigureAwait(false));
            }

            case ScheduledTaskOp task:
                if (tasks is not null)
                {
                    if (!tasks.TryGetValue(task.Path, out bool enabled))
                    {
                        return (OpOutcome.NotPresent, null);
                    }

                    if (enabled == !task.DisableWhenApplied)
                    {
                        return (OpOutcome.AlreadyInPlace, null);
                    }
                }

                return FromResult(await _system.SetTaskEnabledAsync(task.Path, !task.DisableWhenApplied, cancellationToken).ConfigureAwait(false));

            case AppxRemovalOp app:
            {
                // A removal for all users also acts on other accounts and the provisioned package, so the app counts as
                // present when any of those still has it, even if the current account removed it already.
                var installed = await InstalledAppsAsync(RemoveAppsForAllUsers, cancellationToken).ConfigureAwait(false);
                if (installed is not null && !DebloatRules.IsAppInstalled(app.PackageName, installed))
                {
                    return (OpOutcome.NotPresent, null);
                }

                return FromResult(await _system.RemoveAppAsync(app, RemoveAppsForAllUsers, cancellationToken).ConfigureAwait(false));
            }

            case OptionalFeatureOp feature:
            {
                var state = await SafeAsync(() => _system.GetOptionalFeatureStateAsync(feature.FeatureName, cancellationToken), FeatureState.Unknown).ConfigureAwait(false);
                if (state == FeatureState.NotPresent)
                {
                    return (OpOutcome.NotPresent, null);
                }

                if (state == (feature.DisableWhenApplied ? FeatureState.Disabled : FeatureState.Enabled))
                {
                    return (OpOutcome.AlreadyInPlace, null);
                }

                return FromResult(await _system.SetOptionalFeatureAsync(feature, enabled: !feature.DisableWhenApplied, cancellationToken).ConfigureAwait(false));
            }

            case PowerShellOp ps:
            {
                // The detection script says whether the change is already there (for example OneDrive already removed).
                if (await SafeAsync(() => _system.DetectAsync(ps, cancellationToken), null).ConfigureAwait(false) == true)
                {
                    return (OpOutcome.AlreadyInPlace, null);
                }

                return FromResult(await _system.RunScriptAsync(ps.ApplyScript, cancellationToken).ConfigureAwait(false));
            }

            default:
                return (OpOutcome.Failed, "Unknown operation type.");
        }
    }

    // ---- revert --------------------------------------------------------------------------------------------------

    private async Task<(bool? Outcome, string? Message)> RevertOperationAsync(DebloatOperation op, IReadOnlyDictionary<string, string?>? capture,
        IReadOnlyDictionary<string, bool>? tasks, CancellationToken cancellationToken)
    {
        switch (op)
        {
            case RegistryValueOp reg:
            {
                var (ok, message) = Try(() => RevertRegistry(reg, capture), reg);
                return (ok, message);
            }

            case RegistryKeyRemovalOp keyOp:
            {
                // Recreate the key unless it was captured as absent; with no capture, Windows' default is "present".
                bool existedBefore = capture is null || !capture.TryGetValue(keyOp.Key, out var saved) || saved is not null;
                if (!existedBefore || SafeKeyExists(keyOp.Hive, keyOp.Path))
                {
                    return (null, null);
                }

                return Try(() => _system.Registry.Write(keyOp.Hive, keyOp.Path, string.Empty, new RegistryData(DebloatValueKind.String, keyOp.RestoredDefaultValue)),
                    keyOp.Hive, keyOp.Path);
            }

            case ServiceOp svc:
            {
                if (DebloatStateEvaluator.ReadStartMode(_system.Registry, svc.Name) is null)
                {
                    return (null, null);
                }

                var target = capture is not null && capture.TryGetValue(svc.Key, out var saved) && ServiceStartModes.TryParse(saved, out var parsed) ? parsed : svc.DefaultMode;
                return ToRevert(await _system.SetServiceStartModeAsync(svc.Name, target, cancellationToken).ConfigureAwait(false));
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
                return ToRevert(await _system.SetTaskEnabledAsync(task.Path, enable, cancellationToken).ConfigureAwait(false));
            }

            case OptionalFeatureOp feature:
            {
                var state = await SafeAsync(() => _system.GetOptionalFeatureStateAsync(feature.FeatureName, cancellationToken), FeatureState.Unknown).ConfigureAwait(false);
                if (state == FeatureState.NotPresent)
                {
                    return (null, null);
                }

                // Only a captured Enabled or Disabled counts: Unknown (the query failed or timed out) or NotPresent says
                // nothing about the original state, so those fall back to the state the tweak changed away from.
                bool enable = capture is not null && capture.TryGetValue(feature.Key, out var saved) && Enum.TryParse<FeatureState>(saved, true, out var previous)
                              && previous is FeatureState.Enabled or FeatureState.Disabled
                    ? previous == FeatureState.Enabled
                    : feature.DisableWhenApplied;
                return ToRevert(await _system.SetOptionalFeatureAsync(feature, enable, cancellationToken).ConfigureAwait(false));
            }

            case PowerShellOp { RevertScript: not null } ps:
                return ToRevert(await _system.RunScriptAsync(ps.RevertScript, cancellationToken).ConfigureAwait(false));

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

    private static (bool Ok, string? Message) Try(Action action, RegistryValueOp reg) => Try(action, reg.Hive, reg.Path);

    private static (bool Ok, string? Message) Try(Action action, DebloatHive hive, string path)
    {
        try
        {
            action();
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var where = $"{RegistryValueCodec.HiveName(hive)}\\{path}";
            return (false, ex is UnauthorizedAccessException or global::System.Security.SecurityException
                ? $"Windows blocked the change to {where} (access denied)."
                : $"Could not change {where}: {ex.Message}");
        }
    }

    private static (OpOutcome, string?) FromResult(OperationResult result) => (result.Success ? OpOutcome.Changed : OpOutcome.Failed, result.Success ? null : result.Message);

    private static (bool?, string?) ToRevert(OperationResult result) => (result.Success, result.Success ? null : result.Message);

    private string? SafeScopeId()
    {
        try
        {
            return _system.CurrentUserScopeId;
        }
        catch (Exception)
        {
            return null;
        }
    }

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
