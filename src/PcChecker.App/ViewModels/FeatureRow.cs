using PcChecker.Core.Optimization;
using PcChecker.Core.WindowsFeatures;

namespace PcChecker.App.ViewModels;

/// <summary>Display wrapper for one <see cref="WindowsFeature"/> row: badge text, which buttons apply and search matching.</summary>
public sealed class FeatureRow
{
    public FeatureRow(WindowsFeature feature)
    {
        Feature = feature ?? throw new ArgumentNullException(nameof(feature));
        StateText = FeatureLabels.StateText(feature.State, feature.Kind);
        RiskText = FeatureLabels.RiskText(feature.Risk, feature.Kind);
        Group = feature.Kind == FeatureKind.Capability ? CapabilityCatalog.GroupFor(feature.Name) : string.Empty;
    }

    public WindowsFeature Feature { get; }

    public string Name => Feature.Name;
    public string DisplayName => Feature.DisplayName;
    public string? Description => Feature.Description;
    public string? Recommendation => Feature.Recommendation;
    public string? PackageFullName => Feature.PackageFullName;
    public FeatureKind Kind => Feature.Kind;
    public FeatureState State => Feature.State;
    public RiskLevel Risk => Feature.Risk;
    public bool AdvancedOnly => Feature.AdvancedOnly;
    public bool Removable => Feature.Removable;

    public string StateText { get; }
    public string RiskText { get; }
    public string Group { get; }

    public bool IsOn => Feature.State == FeatureState.Enabled;
    public bool CanTurnOn => Feature.Kind != FeatureKind.InboxApp && Feature.State is FeatureState.Disabled or FeatureState.NotPresent;
    public bool CanTurnOff => Feature.Kind != FeatureKind.InboxApp && Feature.State == FeatureState.Enabled;
    public bool CanRemove => Feature.Kind == FeatureKind.InboxApp && Feature.State == FeatureState.Enabled && Feature.Removable;
    public bool IsRemoved => Feature.Kind == FeatureKind.InboxApp && Feature.State != FeatureState.Enabled;
    public bool IsProtected => Feature.Kind == FeatureKind.InboxApp && !Feature.Removable && Feature.State == FeatureState.Enabled;
    public bool HasRecommendation => !string.IsNullOrWhiteSpace(Feature.Recommendation);

    public string TurnOnText => Feature.Kind == FeatureKind.Capability ? "Install" : "Enable";
    public string TurnOffText => Feature.Kind == FeatureKind.Capability ? "Remove" : "Disable";

    /// <summary>Technical name shown under the friendly one (package family for apps).</summary>
    public string TechnicalName => Feature.Kind == FeatureKind.InboxApp && Feature.PackageFullName is { } full ? full : Feature.Name;

    public bool Matches(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return Contains(Feature.DisplayName, search) || Contains(Feature.Name, search) || Contains(Feature.Description, search)
            || Contains(Feature.PackageFullName, search) || Contains(StateText, search) || Contains(RiskText, search);
    }

    private static bool Contains(string? text, string search) =>
        !string.IsNullOrEmpty(text) && text.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
}
