using PcChecker.Core.WindowsFeatures;

namespace PcChecker.Core.Debloat;

/// <summary>Everything about the PC that state detection needs besides the registry, read once per refresh.</summary>
public sealed record DebloatSnapshot(
    int? OsBuild,
    IReadOnlyDictionary<string, bool>? TaskStates,
    IReadOnlySet<string>? InstalledApps,
    IReadOnlyDictionary<string, FeatureState> Features,
    IReadOnlyDictionary<string, bool?> Detections,
    IReadOnlyList<string> Printers)
{
    public static DebloatSnapshot Empty(int? build = null) =>
        new(build, null, null, new Dictionary<string, FeatureState>(), new Dictionary<string, bool?>(), []);
}

/// <summary>State of one operation.</summary>
public enum OperationState
{
    Applied,
    NotApplied,
    NotApplicable,
    Unknown,
}

/// <summary>
/// Pure state detection: compares each operation's target with the live value read through
/// <see cref="IRegistryAccessor"/> and the <see cref="DebloatSnapshot"/>, then combines them into a tweak state
/// (Applied, Not applied, Partial, Not available on this Windows, Unknown).
/// </summary>
public static class DebloatStateEvaluator
{
    public static TweakState Evaluate(DebloatTweak tweak, IRegistryAccessor registry, DebloatSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!DebloatRules.IsSupported(tweak, snapshot.OsBuild))
        {
            return new TweakState(tweak.Id, TweakStatus.NotApplicable, DebloatRules.UnsupportedReason(tweak, snapshot.OsBuild));
        }

        if (tweak.Operations.Count == 0)
        {
            return TweakState.Unknown(tweak.Id, "Checked by the Optimize page");
        }

        if (DebloatRules.RequirementFailure(tweak, registry) is string missing)
        {
            return new TweakState(tweak.Id, TweakStatus.NotApplicable, missing);
        }

        if (tweak.OnlyWithoutApp is { } requiredAbsent && snapshot.InstalledApps is not null && DebloatRules.IsAppInstalled(requiredAbsent, snapshot.InstalledApps))
        {
            return new TweakState(tweak.Id, TweakStatus.NotApplicable, DebloatRules.AppStillInstalledText(requiredAbsent));
        }

        var states = tweak.Operations.Select(op => EvaluateOperation(op, registry, snapshot)).ToList();
        var status = Aggregate(states.Select(s => s.State));
        string detail = status switch
        {
            TweakStatus.Applied when tweak.Operations.All(o => o is AppxRemovalOp) => "Not installed",
            TweakStatus.NotApplied when tweak.Operations.All(o => o is AppxRemovalOp) => "Installed",
            TweakStatus.Applied => "Applied",
            TweakStatus.NotApplied => states.Count == 1 && states[0].Detail is not null ? states[0].Detail! : "Not applied",
            TweakStatus.Partial => $"{states.Count(s => s.State == OperationState.Applied)} of {states.Count(s => s.State is OperationState.Applied or OperationState.NotApplied)} changes in place",
            TweakStatus.NotApplicable => states.Select(s => s.Detail).FirstOrDefault(d => d is not null) ?? "Not present on this PC",
            _ => states.Select(s => s.Detail).FirstOrDefault(d => d is not null) ?? "Could not be checked",
        };

        if (tweak.Precondition == DebloatPrecondition.NoPhysicalPrinters && status == TweakStatus.NotApplied
            && DebloatRules.PreconditionFailure(tweak, snapshot.Printers) is string reason)
        {
            detail = reason;
        }

        return new TweakState(tweak.Id, status, detail);
    }

    /// <summary>
    /// Combines operation states: operations that do not exist on this PC are ignored; all applied → Applied, none →
    /// Not applied, a mix → Partial; nothing known → Unknown; nothing present → Not applicable.
    /// </summary>
    public static TweakStatus Aggregate(IEnumerable<OperationState> states)
    {
        var list = states.ToList();
        var relevant = list.Where(s => s != OperationState.NotApplicable).ToList();
        if (relevant.Count == 0)
        {
            return list.Count == 0 ? TweakStatus.Unknown : TweakStatus.NotApplicable;
        }

        int applied = relevant.Count(s => s == OperationState.Applied);
        int notApplied = relevant.Count(s => s == OperationState.NotApplied);
        if (applied == 0 && notApplied == 0)
        {
            return TweakStatus.Unknown;
        }

        if (notApplied == 0)
        {
            return relevant.Count == applied ? TweakStatus.Applied : TweakStatus.Partial;
        }

        return applied == 0 ? TweakStatus.NotApplied : TweakStatus.Partial;
    }

    public static (OperationState State, string? Detail) EvaluateOperation(DebloatOperation operation, IRegistryAccessor registry, DebloatSnapshot snapshot) =>
        operation switch
        {
            RegistryValueOp reg => EvaluateRegistry(reg, registry),
            RegistryKeyRemovalOp keyOp => EvaluateKeyRemoval(keyOp, registry),
            ServiceOp svc => EvaluateService(svc, registry),
            ScheduledTaskOp task => snapshot.TaskStates is null
                ? (OperationState.Unknown, "Scheduled tasks could not be read")
                : snapshot.TaskStates.TryGetValue(task.Path, out bool enabled)
                    ? (enabled != task.DisableWhenApplied ? OperationState.Applied : OperationState.NotApplied, enabled ? "Task enabled" : "Task disabled")
                    : (OperationState.NotApplicable, "Task not present"),
            AppxRemovalOp app => snapshot.InstalledApps is null
                ? (OperationState.Unknown, "Installed apps could not be read")
                : DebloatRules.IsAppInstalled(app.PackageName, snapshot.InstalledApps)
                    ? (OperationState.NotApplied, "Installed")
                    : (OperationState.Applied, "Not installed"),
            OptionalFeatureOp feature => snapshot.Features.TryGetValue(feature.FeatureName, out var state)
                ? state switch
                {
                    FeatureState.NotPresent => (OperationState.NotApplicable, "Not present on this PC"),
                    FeatureState.Unknown => (OperationState.Unknown, "Feature state unknown"),
                    FeatureState.Enabled => (feature.DisableWhenApplied ? OperationState.NotApplied : OperationState.Applied, "Feature on"),
                    _ => (feature.DisableWhenApplied ? OperationState.Applied : OperationState.NotApplied, "Feature off"),
                }
                : (OperationState.Unknown, "Feature state unknown"),
            PowerShellOp ps => snapshot.Detections.TryGetValue(ps.Key, out bool? detected) && detected is bool d
                ? (d ? OperationState.Applied : OperationState.NotApplied, null)
                : (OperationState.Unknown, "Could not be checked"),
            _ => (OperationState.Unknown, null),
        };

    private static (OperationState, string?) EvaluateRegistry(RegistryValueOp op, IRegistryAccessor registry)
    {
        RegistryData? current;
        try
        {
            current = registry.Read(op.Hive, op.Path, op.Name);
        }
        catch (Exception)
        {
            return (OperationState.Unknown, "Registry value could not be read");
        }

        bool applied = RegistryValueCodec.Matches(current, op.Kind, op.AppliedValue);
        return (applied ? OperationState.Applied : OperationState.NotApplied, current is null ? "Windows default" : null);
    }

    private static (OperationState, string?) EvaluateKeyRemoval(RegistryKeyRemovalOp op, IRegistryAccessor registry)
    {
        try
        {
            return registry.KeyExists(op.Hive, op.Path) ? (OperationState.NotApplied, "Windows default") : (OperationState.Applied, null);
        }
        catch (Exception)
        {
            return (OperationState.Unknown, "Registry key could not be read");
        }
    }

    private static (OperationState, string?) EvaluateService(ServiceOp op, IRegistryAccessor registry)
    {
        var mode = ReadStartMode(registry, op.Name);
        if (mode is null)
        {
            return (OperationState.NotApplicable, "Service not installed");
        }

        return ServiceStartModes.Satisfies(mode.Value, op.AppliedMode)
            ? (OperationState.Applied, ServiceStartModes.Display(mode.Value))
            : (OperationState.NotApplied, ServiceStartModes.Display(mode.Value));
    }

    /// <summary>Reads a service's start type from HKLM\SYSTEM\CurrentControlSet\Services; null when the service does not exist.</summary>
    public static DebloatStartMode? ReadStartMode(IRegistryAccessor registry, string serviceName)
    {
        try
        {
            var path = ServiceStartModes.RegistryPath(serviceName);
            var start = registry.Read(DebloatHive.LocalMachine, path, "Start");
            if (start is null || !RegistryValueCodec.TryParseDword(start.Value, out uint startValue))
            {
                return null;
            }

            var delayed = registry.Read(DebloatHive.LocalMachine, path, "DelayedAutostart");
            uint? delayedValue = delayed is not null && RegistryValueCodec.TryParseDword(delayed.Value, out uint d) ? d : null;
            return ServiceStartModes.FromRegistry(startValue, delayedValue);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
