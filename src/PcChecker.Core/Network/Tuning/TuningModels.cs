using PcChecker.Core.Optimization;

namespace PcChecker.Core.Network.Tuning;

/// <summary>
/// One advanced driver property of a network adapter. <see cref="Key"/> is the registry keyword (for example
/// RoamAggressiveness), <see cref="CurrentValue"/> and <see cref="RecommendedValue"/> are registry values, and
/// <see cref="AllowedValues"/> pairs each registry value with the label the driver shows.
/// </summary>
public sealed record AdapterSetting(
    string AdapterName,
    string Key,
    string DisplayName,
    string? CurrentValue,
    IReadOnlyList<(string Value, string Label)> AllowedValues,
    string? RecommendedValue,
    string Explanation,
    RiskLevel Risk)
{
    /// <summary>The driver's label for a registry value, or the value itself.</summary>
    public string LabelFor(string? value) =>
        value is null ? "—" : AllowedValues.FirstOrDefault(a => a.Value == value).Label ?? value;

    public string CurrentLabel => LabelFor(CurrentValue);

    public string? RecommendedLabel => RecommendedValue is null ? null : LabelFor(RecommendedValue);

    /// <summary>True when PC Checker recommends a value and the adapter uses a different one.</summary>
    public bool DiffersFromRecommendation => RecommendedValue is not null && !string.Equals(RecommendedValue, CurrentValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>Settings without a list of allowed values (free text) are shown read-only.</summary>
    public bool IsEditable => AllowedValues.Count > 0;
}

/// <summary>A reversible network tweak on the Network page.</summary>
public sealed record TuningAction(string Id, string Title, string Description, RiskLevel Risk, bool AdvancedOnly, bool RequiresReconnect, bool RequiresRestart);

/// <summary>A saved Wi-Fi network (profile). <see cref="Priority"/> is 1 for the network Windows tries first.</summary>
public sealed record WifiProfileInfo(string Name, string Interface, bool AutoConnect, bool Metered, string Authentication, int Priority);

/// <summary>Raw output of <c>Get-NetAdapterAdvancedProperty</c> for one property.</summary>
public sealed record AdapterAdvancedProperty(
    string AdapterName,
    string DisplayName,
    string? DisplayValue,
    string RegistryKeyword,
    string? RegistryValue,
    IReadOnlyList<string> ValidDisplayValues,
    IReadOnlyList<string> ValidRegistryValues);

/// <summary>What the advisor knows about the PC and the Wi-Fi around it.</summary>
public sealed record AdvisorContext(bool IsWifi, bool IsLaptop, bool SameSsidOn5GhzVisible, bool SameSsidOn6GhzVisible, bool Band24Congested, int AccessPointsForSsid);

/// <summary>A public DNS provider PC Checker can switch to.</summary>
public sealed record DnsProvider(string Id, string Name, string Primary, string Secondary, string DohTemplate, string Note)
{
    public IReadOnlyList<string> Servers => [Primary, Secondary];
}

public enum QuickImproveKind
{
    Action,
    AdapterSetting,
}

/// <summary>One line of the "Quick improve Wi-Fi" checklist.</summary>
public sealed record QuickImproveItem(
    QuickImproveKind Kind,
    string Id,
    string? AdapterName,
    string? Key,
    string Title,
    string CurrentText,
    string TargetText,
    string? TargetValue,
    string Reason,
    RiskLevel Risk,
    bool Selected,
    bool MayDisconnect,
    bool RequiresRestart);
