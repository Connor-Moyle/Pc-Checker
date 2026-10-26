using System.Text.Json;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.WindowsFeatures;

/// <summary>Row shape of <c>Get-WindowsOptionalFeature -Online | Select FeatureName, State</c> after ConvertTo-Json.</summary>
public sealed class OptionalFeatureRow
{
    public string? FeatureName { get; set; }
    public JsonElement State { get; set; }
}

/// <summary>Row shape of <c>Get-WindowsCapability -Online | Select Name, State</c>.</summary>
public sealed class CapabilityRow
{
    public string? Name { get; set; }
    public JsonElement State { get; set; }
}

/// <summary>Row shape of <c>Get-AppxPackage -AllUsers | Select Name, PackageFullName, NonRemovable, Version, IsFramework, SignatureKind</c>.</summary>
public sealed class AppxPackageRow
{
    public string? Name { get; set; }
    public string? PackageFullName { get; set; }
    public string? PackageFamilyName { get; set; }

    /// <summary>Kept as a raw element: Windows PowerShell 5.1 emits System.Version as an object, newer hosts as a string.</summary>
    public JsonElement Version { get; set; }
    public string? Publisher { get; set; }
    public JsonElement NonRemovable { get; set; }
    public JsonElement IsFramework { get; set; }
    public JsonElement SignatureKind { get; set; }
}

/// <summary>Row shape of <c>Get-AppxProvisionedPackage -Online | Select DisplayName, PackageName</c>.</summary>
public sealed class ProvisionedPackageRow
{
    public string? DisplayName { get; set; }
    public string? PackageName { get; set; }
}

/// <summary>Pure mapping from cmdlet rows (plus the catalogs) to <see cref="WindowsFeature"/> records.</summary>
public static class FeatureMapper
{
    public const string UnknownFeatureDescription = "Windows does not describe this feature. Leave it unless you know what it does.";
    public const string UnknownCapabilityDescription = "An optional Windows component that is not in our catalog.";
    public const string UnknownAppDescription = "A Microsoft app that is not in our catalog.";
    public const string ProvisionedNote = "Also pre-installed for new user accounts on this PC.";

    public static WindowsFeature MapOptionalFeature(string name, FeatureState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entry = OptionalFeatureCatalog.Find(name);
        return entry is null
            ? new WindowsFeature(name, OptionalFeatureCatalog.Humanize(name), UnknownFeatureDescription, FeatureKind.OptionalFeature, state, RiskLevel.Moderate, true, null, null, true)
            : new WindowsFeature(name, entry.DisplayName, entry.Description, FeatureKind.OptionalFeature, state, entry.Risk, true, entry.Recommendation, null, entry.AdvancedOnly);
    }

    public static WindowsFeature MapCapability(string name, FeatureState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entry = CapabilityCatalog.Find(name);
        var display = CapabilityCatalog.FriendlyName(name);
        return entry is null
            ? new WindowsFeature(name, display, UnknownCapabilityDescription, FeatureKind.Capability, state, RiskLevel.Moderate, true, null, null, true)
            : new WindowsFeature(name, display, entry.Description, FeatureKind.Capability, state, entry.Risk, true, entry.Recommendation, null, entry.AdvancedOnly);
    }

    public static WindowsFeature MapInboxApp(string name, string? packageFullName, bool nonRemovable, bool isProvisioned)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entry = InboxAppCatalog.Find(name);
        bool removable = InboxAppCatalog.IsRemovable(name, nonRemovable);
        string? recommendation = isProvisioned ? ProvisionedNote : null;
        return entry is null
            ? new WindowsFeature(name, InboxAppCatalog.FriendlyName(name), UnknownAppDescription, FeatureKind.InboxApp, FeatureState.Enabled, RiskLevel.Moderate, removable, recommendation, packageFullName, true)
            : new WindowsFeature(name, entry.FriendlyName, entry.Note, FeatureKind.InboxApp, FeatureState.Enabled, entry.Risk, removable, recommendation, packageFullName, entry.AdvancedOnly);
    }

    /// <summary>Maps the cmdlet rows, skipping blanks and duplicate names, sorted by display name.</summary>
    public static IReadOnlyList<WindowsFeature> MapOptionalFeatures(IEnumerable<OptionalFeatureRow>? rows)
    {
        var list = new List<WindowsFeature>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.FeatureName) || !seen.Add(row.FeatureName))
            {
                continue;
            }

            list.Add(MapOptionalFeature(row.FeatureName.Trim(), FeatureStateParser.ParseFeatureState(FeatureStateParser.StateText(row.State))));
        }

        return Sort(list);
    }

    public static IReadOnlyList<WindowsFeature> MapCapabilities(IEnumerable<CapabilityRow>? rows, bool includeNotPresent = false)
    {
        var list = new List<WindowsFeature>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.Name) || !seen.Add(row.Name))
            {
                continue;
            }

            var state = FeatureStateParser.ParseCapabilityState(FeatureStateParser.StateText(row.State));
            if (state == FeatureState.NotPresent && !includeNotPresent)
            {
                continue;
            }

            list.Add(MapCapability(row.Name.Trim(), state));
        }

        return Sort(list);
    }

    /// <summary>Maps Appx rows to inbox apps: frameworks are skipped, names are de-duplicated, provisioned packages are flagged.</summary>
    public static IReadOnlyList<WindowsFeature> MapInboxApps(IEnumerable<AppxPackageRow>? rows, IEnumerable<ProvisionedPackageRow>? provisioned)
    {
        var provisionedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in provisioned ?? [])
        {
            if (!string.IsNullOrWhiteSpace(p.DisplayName))
            {
                provisionedNames.Add(p.DisplayName.Trim());
            }
        }

        var list = new List<WindowsFeature>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.Name) || FeatureStateParser.ParseBool(row.IsFramework) || !seen.Add(row.Name))
            {
                continue;
            }

            var name = row.Name.Trim();
            list.Add(MapInboxApp(name, row.PackageFullName, FeatureStateParser.ParseBool(row.NonRemovable), provisionedNames.Contains(name)));
        }

        return Sort(list);
    }

    private static IReadOnlyList<WindowsFeature> Sort(List<WindowsFeature> list) =>
        list.OrderBy(f => f.AdvancedOnly).ThenBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
}

/// <summary>Beginner-friendly badge text for risk levels and states, shared by the page and reports.</summary>
public static class FeatureLabels
{
    public static string RiskText(RiskLevel risk, FeatureKind kind) => (risk, kind) switch
    {
        (RiskLevel.Safe, FeatureKind.InboxApp) => "Safe to remove",
        (RiskLevel.Moderate, FeatureKind.InboxApp) => "Think twice",
        (RiskLevel.Advanced, FeatureKind.InboxApp) => "Not recommended",
        (RiskLevel.Safe, _) => "Safe",
        (RiskLevel.Moderate, _) => "Think twice",
        _ => "Advanced",
    };

    public static string StateText(FeatureState state, FeatureKind kind) => (state, kind) switch
    {
        (FeatureState.Enabled, FeatureKind.OptionalFeature) => "Enabled",
        (FeatureState.Disabled, FeatureKind.OptionalFeature) => "Disabled",
        (FeatureState.Enabled, FeatureKind.Capability) => "Installed",
        (FeatureState.Disabled, FeatureKind.Capability) => "Not installed",
        (FeatureState.NotPresent, FeatureKind.Capability) => "Not available",
        (FeatureState.Enabled, FeatureKind.InboxApp) => "Installed",
        (FeatureState.NotPresent, FeatureKind.InboxApp) => "Removed",
        (FeatureState.Disabled, FeatureKind.InboxApp) => "Removed",
        (FeatureState.NotPresent, _) => "Not present",
        _ => "Unknown",
    };

    /// <summary>Verb for the primary action on the item, or null when nothing applies.</summary>
    public static string? ActionVerb(WindowsFeature feature) => feature.Kind switch
    {
        FeatureKind.InboxApp => feature.State == FeatureState.Enabled && feature.Removable ? "Remove" : null,
        FeatureKind.Capability => feature.State switch
        {
            FeatureState.Enabled => "Remove",
            FeatureState.Disabled or FeatureState.NotPresent => "Install",
            _ => null,
        },
        _ => feature.State switch
        {
            FeatureState.Enabled => "Disable",
            FeatureState.Disabled or FeatureState.NotPresent => "Enable",
            _ => null,
        },
    };
}
