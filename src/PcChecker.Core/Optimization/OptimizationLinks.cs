namespace PcChecker.Core.Optimization;

/// <summary>
/// Which catalog actions share a setting, so that applying or reverting one changes what the others report.
/// Power plans are mutually exclusive, fast startup on/off are opposites, turning hibernation off also turns fast
/// startup off, and the visual-effects presets rewrite the transparency and animation values. The Optimize page
/// re-probes every linked row after an action completes instead of only the row that was clicked.
/// </summary>
public static class OptimizationLinks
{
    private static readonly string[] PowerPlans = ["power.highperformance", "power.balanced", "power.ultimate"];
    private static readonly string[] FastStartup = ["faststartup.disable", "faststartup.enable"];
    private static readonly string[] Visuals = ["visuals.performance", "visuals.balanced", "transparency.disable", "animations.disable"];

    private static readonly IReadOnlyDictionary<string, string[]> Links = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["power.highperformance"] = PowerPlans,
        ["power.balanced"] = PowerPlans,
        ["power.ultimate"] = PowerPlans,
        ["faststartup.disable"] = [.. FastStartup, "hibernation.disable"],
        ["faststartup.enable"] = [.. FastStartup, "hibernation.disable"],
        ["hibernation.disable"] = ["hibernation.disable", .. FastStartup],
        ["visuals.performance"] = Visuals,
        ["visuals.balanced"] = Visuals,
        ["transparency.disable"] = ["transparency.disable", "visuals.performance", "visuals.balanced"],
        ["animations.disable"] = ["animations.disable", "visuals.performance", "visuals.balanced"],
        ["telemetry.minimal"] = ["telemetry.minimal"],
    };

    /// <summary>
    /// The ids whose state should be re-checked after <paramref name="actionId"/> was applied or reverted.
    /// Always includes <paramref name="actionId"/> itself; the order is stable and has no duplicates.
    /// </summary>
    public static IReadOnlyList<string> AffectedBy(string actionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        var result = new List<string> { actionId };
        if (Links.TryGetValue(actionId, out var linked))
        {
            foreach (var id in linked)
            {
                if (!result.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(id);
                }
            }
        }

        if (OptimizationCatalog.Opposites.TryGetValue(actionId, out var opposite) && !result.Contains(opposite, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(opposite);
        }

        return result;
    }

    /// <summary>True when the two actions cannot both be "applied" at the same time.</summary>
    public static bool AreExclusive(string first, string second)
    {
        if (first.Equals(second, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return (PowerPlans.Contains(first, StringComparer.OrdinalIgnoreCase) && PowerPlans.Contains(second, StringComparer.OrdinalIgnoreCase))
               || (FastStartup.Contains(first, StringComparer.OrdinalIgnoreCase) && FastStartup.Contains(second, StringComparer.OrdinalIgnoreCase))
               || (OptimizationCatalog.Opposites.TryGetValue(first, out var opposite) && opposite.Equals(second, StringComparison.OrdinalIgnoreCase));
    }
}
