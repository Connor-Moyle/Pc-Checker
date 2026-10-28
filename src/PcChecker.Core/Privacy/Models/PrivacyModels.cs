using PcChecker.Core.Common;

namespace PcChecker.Core.Privacy;

/// <summary>The three sensitive capabilities whose recent use Windows records.</summary>
public enum CapabilityKind
{
    Camera,
    Microphone,
    Location,
}

/// <summary>The <c>Value</c> string Windows stores for a capability ("Allow", "Deny" or "Prompt").</summary>
public enum ConsentValue
{
    Unknown,
    Allow,
    Deny,
    Prompt,
}

/// <summary>Which hive a consent record lives in.</summary>
public enum ConsentScope
{
    CurrentUser,
    Machine,
}

/// <summary>
/// One app that used (or asked for) the camera, microphone or location, from
/// <c>…\CapabilityAccessManager\ConsentStore\&lt;capability&gt;</c>. <see cref="KeyName"/> is the registry key name: a
/// package family name for Store apps, or the program path with '#' instead of '\' under <c>NonPackaged</c>.
/// </summary>
public sealed record CapabilityAppUsage(
    CapabilityKind Capability,
    string KeyName,
    string DisplayName,
    string? Path,
    bool IsPackaged,
    DateTimeOffset? LastStart,
    DateTimeOffset? LastStop,
    bool InUseNow,
    ConsentValue Value,
    ConsentScope Scope)
{
    /// <summary>How long the last use lasted, or null while in use or unknown.</summary>
    public TimeSpan? Duration => ConsentStoreParser.Duration(LastStart, LastStop, InUseNow);

    /// <summary>True when this app's own access can be turned off here (Store apps that are not already denied).</summary>
    public bool CanRevoke => ConsentStoreParser.CanRevoke(this);
}

/// <summary>One capability: the device-wide and app-wide switches and the apps that used it.</summary>
public sealed record CapabilityStatus(
    CapabilityKind Capability,
    ConsentValue DeviceAccess,
    ConsentValue AppsAccess,
    ConsentValue DesktopAppsAccess,
    IReadOnlyList<CapabilityAppUsage> Apps)
{
    public string AccessText => ConsentStoreParser.AccessText(DeviceAccess, AppsAccess, DesktopAppsAccess);

    public bool InUseNow => Apps.Any(a => a.InUseNow);
}

/// <summary>Everything the Privacy page reads.</summary>
public sealed record PrivacySnapshot(IReadOnlyList<CapabilityStatus> Capabilities, IReadOnlyList<string> Warnings, string? UserNote);

/// <summary>A quick privacy setting shown with a link to the Debloat tweak that changes it.</summary>
public sealed record PrivacyQuickStatus(string TweakId, string Title, string StateText, HealthStatus Status, string Explanation);
