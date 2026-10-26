using PcChecker.Core.Optimization;

namespace PcChecker.Core.WindowsFeatures;

public enum FeatureKind
{
    OptionalFeature,
    Capability,
    InboxApp,
}

public enum FeatureState
{
    Enabled,
    Disabled,
    NotPresent,
    Unknown,
}

public sealed record WindowsFeature(
    string Name,
    string DisplayName,
    string? Description,
    FeatureKind Kind,
    FeatureState State,
    RiskLevel Risk,
    bool Removable,
    string? Recommendation,
    string? PackageFullName,
    bool AdvancedOnly);
