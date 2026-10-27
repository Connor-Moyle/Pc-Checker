namespace PcChecker.Core.Optimization;

/// <summary>How risky an action or cleanup category is.</summary>
public enum RiskLevel
{
    Safe,
    Moderate,
    Advanced,
}

public sealed record OptimizationAction(
    string Id,
    string Title,
    string Description,
    string Category,
    RiskLevel Risk,
    bool Reversible,
    bool RequiresRestart,
    bool AdvancedOnly);

/// <summary>Current state of an action on this machine. <see cref="IsApplied"/> is null when unknown.</summary>
public sealed record ActionState(string ActionId, bool? IsApplied, string? CurrentValue);

/// <summary>One rule for how risky an optimization looks, shared by the row button and its confirmation dialog.</summary>
public static class OptimizationRisk
{
    /// <summary>The temp-file entry only opens the Cleanup page, so it never changes anything by itself.</summary>
    public const string NavigationOnlyActionId = "tempfiles.clean";

    /// <summary>
    /// True when an action is advanced or cannot be undone (one-off repairs and tools): its button is styled Danger and
    /// its confirmation uses the Danger dialog.
    /// </summary>
    public static bool IsDanger(OptimizationAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Id.Equals(NavigationOnlyActionId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return action.Risk == RiskLevel.Advanced || !action.Reversible;
    }
}
