using PcChecker.Core.Common;
using PcChecker.Core.Debloat;
using PcChecker.Core.Drivers;
using PcChecker.Core.Optimization;
using PcChecker.Core.Performance;
using PcChecker.Core.Startup;

namespace PcChecker.Core.Checkup;

/// <summary>Startup, speed check, recommended settings and old drivers.</summary>
public static partial class CheckupItemMapper
{
    /// <summary>At most this many startup apps are suggested; the Startup page lists the rest.</summary>
    public const int MaxStartupSuggestions = 5;

    /// <summary>The key a startup fix target uses: name, location and kind, so a same-named entry elsewhere is never touched.</summary>
    public static string StartupKey(StartupItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return $"{item.Kind}|{item.Location}|{item.Name}";
    }

    /// <summary>The key an old-driver fix target uses (kind and id, as the removal plan matches them).</summary>
    public static string OldDriverKey(DriverCleanupItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return $"{item.Kind}|{item.Id}";
    }

    /// <summary>
    /// One item per enabled high-impact startup app (Run keys and the Startup folder; tasks and Winlogon entries are left
    /// to the Startup page). Turning one off is reversible, but which ones you need is your call, so none is pre-ticked.
    /// </summary>
    public static IReadOnlyList<CheckupItem> FromStartup(IEnumerable<CheckupStartupEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .Where(e => e.Item.Enabled && e.Item.Kind is StartupKinds.Registry or StartupKinds.Folder && e.Insight.Impact == StartupImpactRules.High)
            .OrderBy(e => e.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxStartupSuggestions)
            .Select(e => new CheckupItem(
                "startup." + StartupKey(e.Item),
                CheckupSource.Startup,
                Severity.Low,
                $"Stop {e.Item.Name} starting with Windows",
                e.Insight.ImpactReason,
                "Faster sign-in and more free memory. You can still open it yourself, and turn it back on at any time.",
                RiskLevel.Safe,
                CheckupFixKind.DisableStartupItem,
                [StartupKey(e.Item)])
            {
                SuggestByDefault = false,
                Topic = "startup:" + e.Item.Name,
            })
            .ToList();
    }

    /// <summary>
    /// One item per speed check finding (Info-level hints are left to the page). A finding gets a fix when one of its
    /// actions is a reversible optimization; its risk is the action's (Advanced when either is Advanced-only). Such a
    /// fix names the setting it changes (<see cref="CheckupItem.FixAction"/>) and is never ticked for the user.
    /// "Programs start with Windows" findings are dropped when <paramref name="skipStartupCount"/> is true, because the
    /// startup apps are listed one by one.
    /// </summary>
    public static IReadOnlyList<CheckupItem> FromPerformance(PerformanceReport report, IReadOnlyList<OptimizationAction> actions, bool skipStartupCount)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(actions);
        var items = new List<CheckupItem>();
        foreach (var finding in report.Findings)
        {
            if (finding.Severity <= Severity.Info || (skipStartupCount && finding.Id.StartsWith("startup.", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var action = FixFor(finding, actions);
            var risk = action is null
                ? (finding.AdvancedOnly ? RiskLevel.Advanced : RiskLevel.Safe)
                : (finding.AdvancedOnly || action.AdvancedOnly ? RiskLevel.Advanced : action.Risk);
            items.Add(new CheckupItem(
                "performance." + finding.Id,
                CheckupSource.Performance,
                finding.Severity,
                finding.Title,
                finding.Detail,
                action is null ? $"Avoids: {LowerFirst(finding.Impact)}." : $"{action.Title}. Avoids: {LowerFirst(finding.Impact)}.",
                risk,
                action is null ? CheckupFixKind.None : CheckupFixKind.ApplyOptimization,
                action is null ? [] : [action.Id])
            {
                Topic = PerformanceTopic(finding.Id),
                MayNeedRestart = action?.RequiresRestart ?? false,
                FixAction = action?.Title,

                // The fix is one setting that helps with the finding (Storage Sense for a full drive, lighter visuals
                // for low memory), not the finding's own cure, so the user picks it knowingly; it is never pre-ticked.
                SuggestByDefault = action is null,
            });
        }

        return items;
    }

    /// <summary>
    /// The optimization that fixes a finding: the first of its actions that exists, can be reverted and changes
    /// something (the "clean temporary files" entry only opens the Cleanup page). Null when there is none.
    /// </summary>
    public static OptimizationAction? FixFor(SlowdownFinding finding, IReadOnlyList<OptimizationAction> actions)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(actions);
        foreach (var id in finding.ActionIds)
        {
            if (id.Equals(OptimizationRisk.NavigationOnlyActionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var action = actions.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (action is { Reversible: true })
            {
                return action;
            }
        }

        return null;
    }

    /// <summary>
    /// One item for the Safe settings of the Recommended debloat preset that are not applied yet (or only partly). They
    /// are personal choices (Copilot, Bing in search), so the item is never pre-ticked.
    /// </summary>
    public static IReadOnlyList<CheckupItem> FromDebloat(IReadOnlyList<DebloatTweak> recommended, IReadOnlyDictionary<string, TweakState> states)
    {
        ArgumentNullException.ThrowIfNull(recommended);
        ArgumentNullException.ThrowIfNull(states);
        var todo = recommended
            .Where(t => t.Risk == RiskLevel.Safe && !t.AdvancedOnly)
            .Where(t => states.TryGetValue(t.Id, out var s) && s.Status is TweakStatus.NotApplied or TweakStatus.Partial)
            .ToList();
        if (todo.Count == 0)
        {
            return [];
        }

        var names = string.Join(", ", todo.Take(3).Select(t => LowerFirst(t.Title))) + (todo.Count > 3 ? $" and {todo.Count - 3} more" : string.Empty);
        return
        [
            new CheckupItem("debloat.recommended", CheckupSource.Debloat, Severity.Low,
                todo.Count == 1 ? "Apply 1 recommended privacy setting" : $"Apply {todo.Count} recommended privacy settings",
                $"From the Recommended preset on the Debloat page: {names}.",
                "Fewer ads and suggestions and less data sent to Microsoft. Every setting can be reverted on the Debloat page.",
                RiskLevel.Safe, CheckupFixKind.ApplyRecommendedSettings, todo.Select(t => t.Id).ToList())
            {
                SuggestByDefault = false,
                Magnitude = Math.Min(todo.Count, 10),
                MayNeedRestart = todo.Any(t => t.RequiresRestart),
            },
        ];
    }

    /// <summary>
    /// One item for the old driver packages marked "Safe to remove" (a newer version is installed and nothing uses
    /// them). Everything else the scan found stays on the Old drivers page.
    /// </summary>
    public static IReadOnlyList<CheckupItem> FromOldDrivers(DriverCleanupScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var safe = scan.Items
            .Where(i => i.Status == DriverCleanupStatus.SafeToRemove && DriverCleanupRules.CanSelect(i, advancedMode: false))
            .ToList();
        if (safe.Count == 0)
        {
            return [];
        }

        long bytes = DriverCleanupRules.TotalSize(safe);
        return
        [
            new CheckupItem("olddrivers.safe", CheckupSource.OldDrivers, Severity.Low,
                safe.Count == 1 ? "Remove 1 old driver package" : $"Remove {safe.Count} old driver packages",
                "Newer versions of these drivers are installed and nothing uses the old ones. Each one is backed up before it is removed.",
                bytes > 0 ? $"About {ByteSize.Format(bytes)} freed in the driver store." : "A tidier driver store.",
                RiskLevel.Safe, CheckupFixKind.RemoveOldDrivers, safe.Select(OldDriverKey).ToList())
            {
                Magnitude = bytes / (double)ByteSize.Gigabyte,
            },
        ];
    }

    private static string? PerformanceTopic(string findingId) => findingId switch
    {
        "updates.reboot" => RestartTopic,
        "updates.stale" => PendingUpdatesTopic,
        _ => null,
    };

    private static string LowerFirst(string text) =>
        string.IsNullOrEmpty(text) || (text.Length > 1 && char.IsUpper(text[1])) ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
