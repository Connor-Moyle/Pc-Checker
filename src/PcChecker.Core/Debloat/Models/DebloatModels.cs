using PcChecker.Core.Optimization;

namespace PcChecker.Core.Debloat;

/// <summary>
/// One debloat tweak: a named group of operations that is applied and reverted together. A tweak either carries its
/// own <see cref="Operations"/> or delegates to an existing optimization action (<see cref="DelegatesToOptimizationId"/>).
/// </summary>
public sealed record DebloatTweak(
    string Id,
    string Category,
    string Title,
    string Description,
    RiskLevel Risk,
    bool AdvancedOnly,
    bool RequiresRestart,
    IReadOnlyList<string> SourceTools,
    IReadOnlyList<DebloatOperation> Operations,
    string? DelegatesToOptimizationId,
    int? MinWindowsBuild,
    bool Windows11Only)
{
    /// <summary>Last build the tweak applies to (Windows 10-only tweaks use 19045); null for no upper limit.</summary>
    public int? MaxWindowsBuild { get; init; }

    /// <summary>True when File Explorer must restart before the change shows (taskbar, Start, context menu).</summary>
    public bool RestartsExplorer { get; init; }

    /// <summary>Extra caution shown on the row and in the confirmation (what stops working).</summary>
    public string? Warning { get; init; }

    /// <summary>A condition checked on this PC before applying.</summary>
    public DebloatPrecondition Precondition { get; init; } = DebloatPrecondition.None;

    /// <summary>
    /// Something that must be installed for the tweak to mean anything (a browser, Office, a vendor control panel).
    /// When none of its keys exist the row shows "Not available" with <see cref="DebloatRequirement.MissingText"/>
    /// and Apply skips it, so policies for absent software are never written.
    /// </summary>
    public DebloatRequirement? Requires { get; init; }

    /// <summary>
    /// A Store package that must be absent: the tweak only fills a gap the app leaves when it has been removed (for
    /// example the ms-gamebar link handler after removing Xbox Game Bar). While the app is installed the row shows
    /// "Not available" and Apply skips it.
    /// </summary>
    public string? OnlyWithoutApp { get; init; }

    /// <summary>False when at least one operation cannot be undone here (app removal, OneDrive uninstall).</summary>
    public bool Reversible => DelegatesToOptimizationId is not null || Operations.All(o => o.Reversible);

    /// <summary>True for tweaks whose work is done by another PC Checker service (Optimize page, Windows Features).</summary>
    public bool IsExternal => DelegatesToOptimizationId is not null || Operations.Any(o => o.IsExternal);
}

/// <summary>A safety condition evaluated before a tweak is applied.</summary>
public enum DebloatPrecondition
{
    None,

    /// <summary>Only apply when no physical printer is installed (print spooler and fax).</summary>
    NoPhysicalPrinters,
}

/// <summary>A registry key, used to detect installed software.</summary>
public sealed record DebloatKeyRef(DebloatHive Hive, string Path);

/// <summary>The tweak only applies when at least one of <see cref="AnyOf"/> exists; otherwise <see cref="MissingText"/> explains why.</summary>
public sealed record DebloatRequirement(string MissingText, IReadOnlyList<DebloatKeyRef> AnyOf);

/// <summary>How a tweak currently looks on this PC.</summary>
public enum TweakStatus
{
    Unknown,
    Applied,
    NotApplied,
    Partial,
    NotApplicable,
}

/// <summary>The detected state of one tweak, with a short plain-English detail.</summary>
public sealed record TweakState(string TweakId, TweakStatus Status, string? Detail)
{
    public static TweakState Unknown(string id, string? detail = null) => new(id, TweakStatus.Unknown, detail ?? "Could not be checked");
}

/// <summary>Outcome of applying or reverting one tweak.</summary>
public enum TweakOutcome
{
    Applied,
    Reverted,
    Failed,
    Skipped,
}

/// <summary>Per-tweak result of a batch, with the values before and after for the action journal.</summary>
public sealed record TweakRunResult(string TweakId, string Title, TweakOutcome Outcome, string Message, string? Before = null, string? After = null);

/// <summary>Result of a batch apply or revert.</summary>
public sealed record DebloatRunResult(
    IReadOnlyList<TweakRunResult> Results,
    bool RestartExplorerSuggested,
    bool RebootRequired,
    string Summary)
{
    public int Succeeded => Results.Count(r => r.Outcome is TweakOutcome.Applied or TweakOutcome.Reverted);
    public int Failed => Results.Count(r => r.Outcome == TweakOutcome.Failed);
    public int Skipped => Results.Count(r => r.Outcome == TweakOutcome.Skipped);

    public static DebloatRunResult Empty { get; } = new([], false, false, "Nothing to do.");
}

/// <summary>A named selection of tweaks (Recommended, Gaming, Privacy max, ...).</summary>
public sealed record DebloatPreset(string Id, string Name, string Description, IReadOnlyList<string> TweakIds, bool IsRecommended = false)
{
    /// <summary>The "Custom" preset carries no ids; choosing it clears the ticks so the user picks rows by hand.</summary>
    public bool IsCustom => TweakIds.Count == 0;
}

/// <summary>Something other tools offer that PC Checker deliberately does not, and why.</summary>
public sealed record DebloatUnsafeItem(string Title, string Reason, IReadOnlyList<string> OfferedBy);

/// <summary>A setting PC Checker reports on but leaves to Windows Settings (no documented policy, or a personal choice).</summary>
public sealed record DebloatNote(string Title, string Detail, string SettingsUri);

/// <summary>Result of importing a selection file: the tweak ids found and the names that have no equivalent here.</summary>
public sealed record DebloatImportResult(IReadOnlyList<string> Ids, IReadOnlyList<string> Unmapped, string Format);
