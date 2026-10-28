namespace PcChecker.Core.Security;

/// <summary>
/// Turns the raw registry and WMI values the Security page reads into on/off answers. Only numbers and fixed
/// identifiers are compared, never translated text, so the answers are the same on every Windows language. Pure.
/// </summary>
public static class SecurityValueRules
{
    /// <summary>
    /// SmartScreen for apps and files. The policy <c>EnableSmartScreen</c> (0/1) wins; otherwise
    /// <c>Explorer\SmartScreenEnabled</c> is "Off", "Warn", "Prompt" or "RequireAdmin". Missing means the Windows default (on).
    /// </summary>
    public static bool SmartScreenApps(int? policyEnabled, string? explorerValue)
    {
        if (policyEnabled is { } policy)
        {
            return policy != 0;
        }

        return !string.Equals(explorerValue?.Trim(), "Off", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// SmartScreen in Microsoft Edge: the Edge policy <c>SmartScreenEnabled</c> wins, then the user's setting; missing
    /// means on. Null when Edge is not installed.
    /// </summary>
    public static bool? SmartScreenEdge(bool edgeInstalled, int? policyEnabled, int? userEnabled)
    {
        if (!edgeInstalled)
        {
            return null;
        }

        if (policyEnabled is { } policy)
        {
            return policy != 0;
        }

        return userEnabled is not 0;
    }

    /// <summary><c>HKLM\SYSTEM\CurrentControlSet\Control\PEFirmwareType</c>: 1 is legacy BIOS, 2 is UEFI.</summary>
    public static bool IsLegacyBios(int? firmwareType) => firmwareType == 1;

    /// <summary><c>SecureBoot\State\UEFISecureBootEnabled</c>: 1 on, 0 off, missing unknown.</summary>
    public static bool? SecureBootOn(int? uefiSecureBootEnabled) => uefiSecureBootEnabled switch
    {
        null => null,
        0 => false,
        _ => true,
    };

    /// <summary>The <c>Win32_DeviceGuard</c> service id for Credential Guard.</summary>
    public const uint CredentialGuardService = 1;

    /// <summary>The <c>Win32_DeviceGuard</c> service id for memory integrity (hypervisor-enforced code integrity).</summary>
    public const uint MemoryIntegrityService = 2;

    /// <summary>True when <paramref name="serviceId"/> is in <c>SecurityServicesRunning</c>; null when the list could not be read.</summary>
    public static bool? IsRunning(IEnumerable<uint>? securityServicesRunning, uint serviceId) =>
        securityServicesRunning?.Contains(serviceId);

    /// <summary>Memory integrity from the registry when Device Guard WMI is missing: <c>…\Scenarios\HypervisorEnforcedCodeIntegrity\Enabled</c>.</summary>
    public static bool? MemoryIntegrityFromRegistry(int? enabled) => enabled switch
    {
        null => null,
        0 => false,
        _ => true,
    };

    /// <summary><c>Terminal Server\fDenyTSConnections</c>: 0 means Remote Desktop accepts connections.</summary>
    public static bool? RemoteDesktopOn(int? denyConnections) => denyConnections switch
    {
        null => null,
        0 => true,
        _ => false,
    };

    /// <summary>The built-in Guest account is the local account whose SID ends in -501 (its name is translated, its SID is not).</summary>
    public static bool IsGuestSid(string? sid) =>
        sid is not null && sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) && sid.EndsWith("-501", StringComparison.Ordinal);

    /// <summary>
    /// <c>MSFT_NetFirewallProfile.Enabled</c> is a GpoBoolean: 0 false, 1 true, 2 not configured (the Windows
    /// default, which is on).
    /// </summary>
    public static bool? FirewallEnabled(int? gpoBoolean) => gpoBoolean switch
    {
        0 => false,
        1 or 2 => true,
        _ => null,
    };

    /// <summary><c>Win32_OptionalFeature.InstallState</c>: 1 enabled, 2 disabled, 3 absent; anything else unknown.</summary>
    public static bool? OptionalFeatureOn(int? installState) => installState switch
    {
        1 => true,
        2 or 3 => false,
        _ => null,
    };

    /// <summary>Defender's <c>MAPSReporting</c>: 0 means cloud-delivered protection is off.</summary>
    public static bool? CloudProtectionOn(int? mapsReporting) => mapsReporting switch
    {
        null => null,
        0 => false,
        _ => true,
    };
}
