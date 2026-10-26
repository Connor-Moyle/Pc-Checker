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
