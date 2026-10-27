using PcChecker.Core.Optimization;

namespace PcChecker.Core.Network.Tuning;

/// <summary>
/// Builds the "Quick improve Wi-Fi" checklist: every recommended change that differs from the current state.
/// Recommended actions and Safe adapter settings start ticked; preferences (DNS) and Moderate settings start unticked;
/// advanced actions are left to the user. Pure.
/// </summary>
public static class QuickImprovePlanner
{
    public static IReadOnlyList<QuickImproveItem> Plan(IReadOnlyList<(TuningAction Action, ActionState State)> actions, IReadOnlyList<AdapterSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(settings);
        var items = new List<QuickImproveItem>();

        foreach (var (action, state) in actions)
        {
            if (action.AdvancedOnly || state.IsApplied != false)
            {
                continue;
            }

            bool recommended = NetworkTuningCatalog.QuickImproveDefaults.Contains(action.Id);
            bool optional = NetworkTuningCatalog.QuickImproveOptional.Contains(action.Id);
            if (!recommended && !optional)
            {
                continue;
            }

            items.Add(new QuickImproveItem(QuickImproveKind.Action, action.Id, null, null, action.Title,
                state.CurrentValue ?? "Not applied", "Applied", null, action.Description, action.Risk,
                Selected: recommended, MayDisconnect: action.RequiresReconnect, RequiresRestart: action.RequiresRestart));
        }

        foreach (var setting in settings)
        {
            if (!setting.DiffersFromRecommendation || !AdapterSettingAdvisor.IsAllowed(setting, setting.RecommendedValue))
            {
                continue;
            }

            items.Add(new QuickImproveItem(QuickImproveKind.AdapterSetting, $"adapter:{setting.AdapterName}|{setting.Key}", setting.AdapterName, setting.Key,
                $"{setting.DisplayName} ({setting.AdapterName})", setting.CurrentLabel, setting.RecommendedLabel ?? setting.RecommendedValue!, setting.RecommendedValue,
                setting.Explanation, setting.Risk, Selected: setting.Risk == RiskLevel.Safe, MayDisconnect: true, RequiresRestart: false));
        }

        return items;
    }

    /// <summary>One-line summary for the checklist header.</summary>
    public static string Summarize(IReadOnlyList<QuickImproveItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return "Everything PC Checker can tune on this PC is already set the recommended way.";
        }

        int selected = items.Count(i => i.Selected);
        return $"{items.Count} recommended change{(items.Count == 1 ? string.Empty : "s")} found; {selected} ticked. Untick anything you want to keep as it is.";
    }
}
