using PcChecker.Core.Common;

namespace PcChecker.Core.Security;

/// <summary>The four positions of the User Account Control slider, plus "turned off" and anything else.</summary>
public enum UacLevel
{
    Unknown,

    /// <summary>Top of the slider: notify for every change, on the secure desktop.</summary>
    AlwaysNotify,

    /// <summary>Windows default: notify when apps make changes, on the secure desktop (screen dims).</summary>
    Default,

    /// <summary>Notify when apps make changes, without dimming the screen.</summary>
    NoDim,

    /// <summary>Bottom of the slider: never notify (prompts are approved silently).</summary>
    NeverNotify,

    /// <summary>EnableLUA = 0: UAC is switched off completely; every program runs with full rights.</summary>
    Off,

    /// <summary>A combination the slider cannot show (set by policy or by hand).</summary>
    Custom,
}

/// <summary>
/// Maps the UAC registry values under <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System</c>
/// (<c>EnableLUA</c>, <c>ConsentPromptBehaviorAdmin</c>, <c>PromptOnSecureDesktop</c>) to the slider level, the way the
/// Control Panel does. Missing values take the Windows defaults (1, 5, 1). Pure.
/// </summary>
public static class UacLevels
{
    public static UacLevel Map(int? enableLua, int? consentPromptBehaviorAdmin, int? promptOnSecureDesktop)
    {
        int lua = enableLua ?? 1;
        int consent = consentPromptBehaviorAdmin ?? 5;
        int secure = promptOnSecureDesktop ?? 1;
        if (lua == 0)
        {
            return UacLevel.Off;
        }

        return (consent, secure) switch
        {
            (2, 1) => UacLevel.AlwaysNotify,
            (5, 1) => UacLevel.Default,
            (5, 0) => UacLevel.NoDim,
            (0, 0) => UacLevel.NeverNotify,
            _ => UacLevel.Custom,
        };
    }

    public static string Describe(UacLevel level) => level switch
    {
        UacLevel.AlwaysNotify => "Always notify",
        UacLevel.Default => "Notify when apps make changes (default)",
        UacLevel.NoDim => "Notify when apps make changes, without dimming",
        UacLevel.NeverNotify => "Never notify",
        UacLevel.Off => "Turned off",
        UacLevel.Custom => "Custom setting",
        _ => "Unknown",
    };

    /// <summary>Green for the top two slider positions, yellow for no dimming or a custom mix, red for never notify or off.</summary>
    public static HealthStatus Status(UacLevel level) => level switch
    {
        UacLevel.AlwaysNotify or UacLevel.Default => HealthStatus.Good,
        UacLevel.NoDim or UacLevel.Custom => HealthStatus.Warning,
        UacLevel.NeverNotify or UacLevel.Off => HealthStatus.Critical,
        _ => HealthStatus.Unknown,
    };
}
