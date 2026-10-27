using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.Debloat;
using PcChecker.Core.Drivers;
using PcChecker.Core.Health;
using PcChecker.Core.Optimization;
using PcChecker.Core.Performance;
using PcChecker.Core.Restore;
using PcChecker.Core.Startup;
using PcChecker.Core.WindowsUpdate;

namespace PcChecker.Core.Checkup;

/// <summary>The page a check-up finding comes from (and where "Open" goes).</summary>
public enum CheckupSource
{
    Health,
    Cleanup,
    Drivers,
    WindowsUpdate,
    Startup,
    Performance,
    Debloat,
    OldDrivers,
}

/// <summary>The automatic fix behind a finding, done by the service that owns it. <see cref="None"/> means "Open the page".</summary>
public enum CheckupFixKind
{
    None,

    /// <summary>Delete the junk found in Safe cleanup categories (<see cref="ITempFileCleaner"/>).</summary>
    CleanSafeJunk,

    /// <summary>Stop one startup app from starting with Windows (<see cref="IStartupService"/>; reversible).</summary>
    DisableStartupItem,

    /// <summary>Download and install driver updates from Windows Update (<see cref="IDriverService"/>).</summary>
    InstallDriverUpdates,

    /// <summary>Install the recommended Windows updates (<see cref="IWindowsUpdateService"/>).</summary>
    InstallWindowsUpdates,

    /// <summary>Apply one reversible optimization (<see cref="IOptimizationService"/>).</summary>
    ApplyOptimization,

    /// <summary>Apply the Safe settings of the Recommended debloat preset that are not applied yet (<see cref="IDebloatService"/>).</summary>
    ApplyRecommendedSettings,

    /// <summary>Remove old driver packages that a newer version replaced (<see cref="IDriverCleanupService"/>).</summary>
    RemoveOldDrivers,
}

/// <summary>
/// One entry of the check-up to-do list. <see cref="Targets"/> names what the fix works on (cleanup category ids,
/// update ids, a startup item name, an optimization id, tweak ids or driver package names); the fix looks them up in the
/// scan results it came from, so nothing is acted on that the scan did not see.
/// </summary>
public sealed record CheckupItem(
    string Id,
    CheckupSource Source,
    Severity Severity,
    string Title,
    string Why,
    string Benefit,
    RiskLevel Risk,
    CheckupFixKind Fix,
    IReadOnlyList<string> Targets)
{
    /// <summary>Items that describe the same problem share a topic; the list keeps only the most useful one.</summary>
    public string? Topic { get; init; }

    /// <summary>False for fixes the user should choose one by one (startup apps, personal settings): never ticked for them.</summary>
    public bool SuggestByDefault { get; init; } = true;

    /// <summary>A size, count or share that ranks items of the same severity (bytes freed, updates waiting); 0 when none.</summary>
    public double Magnitude { get; init; }

    /// <summary>True when the fix asks for a restart to finish.</summary>
    public bool MayNeedRestart { get; init; }

    /// <summary>
    /// What "Fix" actually changes, when that is not what the title says (a speed check finding fixed by one setting):
    /// shown in the confirmation and the results so the user approves the change, not the problem. Null otherwise.
    /// </summary>
    public string? FixAction { get; init; }

    public bool HasFix => Fix != CheckupFixKind.None;
}

/// <summary>One part of the check-up that could not run, with the reason.</summary>
public sealed record CheckupGap(CheckupSource Source, string Reason);

/// <summary>
/// Everything one check-up read, as the owning services returned it. A part that failed or was skipped is null and
/// listed in <see cref="Gaps"/>.
/// </summary>
public sealed record CheckupScanResults(
    HealthReport? Health,
    CleanupScanResult? Cleanup,
    IReadOnlyList<string> CleanupCategoryIds,
    DriverScanResult? Drivers,
    DriverUpdateCheckResult? DriverUpdates,
    WindowsUpdateStatus? UpdateStatus,
    PendingUpdateScan? PendingUpdates,
    IReadOnlyList<CheckupStartupEntry>? Startup,
    PerformanceReport? Performance,
    IReadOnlyList<OptimizationAction> Optimizations,
    IReadOnlyList<DebloatTweak>? RecommendedTweaks,
    IReadOnlyDictionary<string, TweakState>? TweakStates,
    DriverCleanupScanResult? OldDrivers,
    IReadOnlyList<CheckupGap> Gaps,
    DateTimeOffset Captured)
{
    public static CheckupScanResults Empty(DateTimeOffset captured) =>
        new(null, null, [], null, null, null, null, null, null, [], null, null, null, [], captured);
}

/// <summary>A startup entry with its estimated impact (see <see cref="StartupImpactRules"/>).</summary>
public sealed record CheckupStartupEntry(StartupItem Item, StartupInsight Insight);

/// <summary>A finished check-up: what was read and the ranked to-do list built from it.</summary>
public sealed record CheckupReport(CheckupScanResults Results, IReadOnlyList<CheckupItem> Items)
{
    public DateTimeOffset Captured => Results.Captured;

    public int FixableCount => Items.Count(i => i.HasFix);
}

/// <summary>When a fix step makes a restore point if it runs on its own.</summary>
public enum CheckupRestorePointNeed
{
    /// <summary>Never (Safe junk cleanup, startup toggles): nothing hard to undo.</summary>
    None,

    /// <summary>When the user's restore point setting is on.</summary>
    Policy,

    /// <summary>Always (the debloat batch insists on one).</summary>
    Always,
}

/// <summary>One call to an owning service: the items it fixes and what the call needs.</summary>
public sealed record CheckupFixStep(CheckupFixKind Kind, IReadOnlyList<CheckupItem> Items, CheckupRestorePointNeed RestorePoint, bool CanStopPartWay)
{
    /// <summary>Every target of every item, in order and without repeats.</summary>
    public IReadOnlyList<string> Targets => Items.SelectMany(i => i.Targets).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>An item left out of a fix run, and why.</summary>
public sealed record CheckupSkippedItem(CheckupItem Item, string Reason);

/// <summary>The ordered steps for the ticked items, and whether one shared restore point should come first.</summary>
public sealed record CheckupFixPlan(IReadOnlyList<CheckupFixStep> Steps, IReadOnlyList<CheckupSkippedItem> Skipped, bool SharedRestorePoint)
{
    public static CheckupFixPlan Empty { get; } = new([], [], false);

    public int ItemCount => Steps.Sum(s => s.Items.Count);

    public bool IsEmpty => Steps.Count == 0;

    /// <summary>How many restore points the steps would make one by one (what the shared one saves).</summary>
    public int SeparateRestorePoints { get; init; }

    public bool MayNeedRestart => Steps.Any(s => s.Items.Any(i => i.MayNeedRestart));

    /// <summary>
    /// True when every step is one that never needs a restore point (Safe junk cleanup, startup toggles). When this is
    /// false and <see cref="SharedRestorePoint"/> is false too, the changes run without any restore point because the
    /// user turned restore points off.
    /// </summary>
    public bool OnlyLightChanges => Steps.All(s => s.RestorePoint == CheckupRestorePointNeed.None);
}

/// <summary>What happened to one item in a fix run.</summary>
public sealed record CheckupFixResult(CheckupItem Item, bool Success, string Message, bool RebootRequired = false);

/// <summary>The outcome of a fix run: one result per item, plus the shared restore point.</summary>
public sealed record CheckupFixReport(IReadOnlyList<CheckupFixResult> Results, ChangeBatchOutcome RestorePoint, bool Cancelled)
{
    public int Succeeded => Results.Count(r => r.Success);

    public int Failed => Results.Count(r => !r.Success);

    public bool RebootRequired => Results.Any(r => r.RebootRequired);
}
