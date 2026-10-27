using PcChecker.Core.Optimization;

namespace PcChecker.Core.Checkup;

/// <summary>
/// Which to-do items can be ticked for "Fix selected" and which are ticked for you. Only Safe fixes are ever ticked
/// automatically; Moderate ones must be ticked by hand, and Advanced ones only in Advanced mode. Pure.
/// </summary>
public static class CheckupSelection
{
    /// <summary>True when the item has an automatic fix the user may tick.</summary>
    public static bool CanSelect(CheckupItem item, bool advancedMode)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.HasFix && (item.Risk != RiskLevel.Advanced || advancedMode);
    }

    /// <summary>True for Safe fixes that suit everyone; startup apps and personal settings are left for the user to pick.</summary>
    public static bool IsSelectedByDefault(CheckupItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.HasFix && item.Risk == RiskLevel.Safe && item.SuggestByDefault;
    }

    /// <summary>The ids ticked after a check-up.</summary>
    public static IReadOnlyList<string> DefaultSelection(IEnumerable<CheckupItem> items, bool advancedMode)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Where(i => CanSelect(i, advancedMode) && IsSelectedByDefault(i)).Select(i => i.Id).ToList();
    }

    /// <summary>Why an item cannot be ticked (shown as the check box tooltip), or null when it can.</summary>
    public static string? WhyNotSelectable(CheckupItem item, bool advancedMode)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.HasFix)
        {
            return "This one needs you: open its page to deal with it.";
        }

        return item.Risk == RiskLevel.Advanced && !advancedMode
            ? "An advanced change. Turn on Advanced mode to fix it from here, or open its page."
            : null;
    }
}
