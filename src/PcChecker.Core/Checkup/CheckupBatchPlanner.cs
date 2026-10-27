namespace PcChecker.Core.Checkup;

/// <summary>
/// Turns the ticked items into the calls "Fix selected" makes, one step per owning service, in a fixed order: quick
/// and reversible things first (junk, old drivers, startup apps, optimizations, recommended settings), installs last
/// because they take longest and may ask for a restart. Decides whether one shared restore point should come first. Pure.
/// </summary>
public static class CheckupBatchPlanner
{
    /// <summary>The order the steps run in.</summary>
    public static IReadOnlyList<CheckupFixKind> Order { get; } =
    [
        CheckupFixKind.CleanSafeJunk,
        CheckupFixKind.RemoveOldDrivers,
        CheckupFixKind.DisableStartupItem,
        CheckupFixKind.ApplyOptimization,
        CheckupFixKind.ApplyRecommendedSettings,
        CheckupFixKind.InstallDriverUpdates,
        CheckupFixKind.InstallWindowsUpdates,
    ];

    /// <summary>
    /// When a step would make a restore point on its own: Safe cleanup and startup toggles never do, the debloat batch
    /// always does, and everything else follows the user's restore point setting.
    /// </summary>
    public static CheckupRestorePointNeed RestorePointNeed(CheckupFixKind kind) => kind switch
    {
        CheckupFixKind.CleanSafeJunk or CheckupFixKind.DisableStartupItem or CheckupFixKind.None => CheckupRestorePointNeed.None,
        CheckupFixKind.ApplyRecommendedSettings => CheckupRestorePointNeed.Always,
        _ => CheckupRestorePointNeed.Policy,
    };

    /// <summary>
    /// False for steps that cannot stop part-way once started (driver installs and removals, as on their pages); the
    /// run stops before such a step instead.
    /// </summary>
    public static bool CanStopPartWay(CheckupFixKind kind) => kind is not (CheckupFixKind.InstallDriverUpdates or CheckupFixKind.RemoveOldDrivers);

    /// <summary>
    /// Builds the plan for <paramref name="ticked"/>. Items that cannot be selected (no fix, or Advanced outside Advanced
    /// mode) are listed in <see cref="CheckupFixPlan.Skipped"/>; repeats are dropped. <paramref name="restorePointsOn"/>
    /// is the user's "create a restore point before changes" setting.
    /// </summary>
    public static CheckupFixPlan Plan(IEnumerable<CheckupItem> ticked, bool advancedMode, bool restorePointsOn)
    {
        ArgumentNullException.ThrowIfNull(ticked);
        var skipped = new List<CheckupSkippedItem>();
        var chosen = new List<CheckupItem>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ticked)
        {
            if (!ids.Add(item.Id))
            {
                continue;
            }

            if (!CheckupSelection.CanSelect(item, advancedMode))
            {
                skipped.Add(new CheckupSkippedItem(item, CheckupSelection.WhyNotSelectable(item, advancedMode) ?? "Cannot be fixed from here."));
                continue;
            }

            chosen.Add(item);
        }

        var steps = new List<CheckupFixStep>();
        foreach (var kind in Order)
        {
            var items = chosen.Where(i => i.Fix == kind).ToList();
            if (items.Count > 0)
            {
                steps.Add(new CheckupFixStep(kind, items, RestorePointNeed(kind), CanStopPartWay(kind)));
            }
        }

        int separate = steps.Sum(s => s.RestorePoint switch
        {
            CheckupRestorePointNeed.Always => 1,
            // Optimizations are applied one by one, and each would make its own restore point.
            CheckupRestorePointNeed.Policy when restorePointsOn => s.Kind == CheckupFixKind.ApplyOptimization ? s.Targets.Count : 1,
            _ => 0,
        });

        return new CheckupFixPlan(steps, skipped, SharedRestorePoint: separate > 0) { SeparateRestorePoints = separate };
    }
}
