using PcChecker.Core.Common;
using PcChecker.Core.Debloat;

namespace PcChecker.Core.Privacy;

/// <summary>
/// The quick privacy settings on the Privacy page. Their state comes from the Debloat page's own tweak checks (so
/// nothing is read or changed twice) and the button opens that tweak on the Debloat page. Pure.
/// </summary>
public static class PrivacyQuickStatusRules
{
    public const string ActivityHistory = "privacy.activity";
    public const string AdvertisingId = "privacy.advertisingid";
    public const string DiagnosticData = "privacy.telemetry";

    /// <summary>The tweaks shown, in page order.</summary>
    public static IReadOnlyList<string> TweakIds { get; } = [ActivityHistory, AdvertisingId, DiagnosticData];

    public static PrivacyQuickStatus Map(string tweakId, TweakStatus status)
    {
        var (title, explanation) = tweakId switch
        {
            ActivityHistory => ("Activity history", "Windows keeps a list of the apps, files and sites you used and can upload it."),
            AdvertisingId => ("Advertising ID", "Apps use a per-user ID to show you personalised ads."),
            DiagnosticData => ("Diagnostic data", "How much usage and crash data Windows sends to Microsoft."),
            _ => (tweakId, string.Empty),
        };

        // For each tweak, "Applied" is the private choice.
        var (text, health) = (tweakId, status) switch
        {
            (_, TweakStatus.NotApplicable) => ("Not used on this PC", HealthStatus.Unknown),
            (_, TweakStatus.Unknown) => ("Could not be checked", HealthStatus.Unknown),
            (DiagnosticData, TweakStatus.Applied) => ("Required data only", HealthStatus.Good),
            (DiagnosticData, TweakStatus.NotApplied) => ("Optional data may be sent", HealthStatus.Warning),
            (DiagnosticData, TweakStatus.Partial) => ("Partly limited", HealthStatus.Warning),
            (_, TweakStatus.Applied) => ("Off", HealthStatus.Good),
            (_, TweakStatus.NotApplied) => ("On", HealthStatus.Warning),
            (_, TweakStatus.Partial) => ("Partly off", HealthStatus.Warning),
            _ => ("Could not be checked", HealthStatus.Unknown),
        };

        return new PrivacyQuickStatus(tweakId, title, text, health, explanation);
    }
}
