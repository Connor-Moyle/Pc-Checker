namespace PcChecker.Core.Security;

/// <summary>The decoded <c>productState</c> of a Security Center product.</summary>
public sealed record SecurityProductState(bool Enabled, bool Snoozed, bool UpToDate, int ProviderFlags);

/// <summary>
/// Decodes the undocumented but stable <c>productState</c> number that <c>root\SecurityCenter2</c> reports for
/// antivirus and firewall products. It holds three bytes: <c>0xPPSSDD</c>, where PP are the security provider flags
/// (1 firewall, 2 automatic updates, 4 antivirus, 8 antispyware…), SS the product state (0x10 on, 0x00 off, 0x01
/// snoozed) and DD the definition state (0x00 up to date, 0x10 out of date). Example: 397568 = 0x061100 is an antivirus
/// that is on and up to date. Pure.
/// </summary>
public static class SecurityCenterState
{
    /// <summary>The Security Center provider flag for antivirus products.</summary>
    public const int AntivirusProvider = 0x04;

    /// <summary>The Security Center provider flag for firewall products.</summary>
    public const int FirewallProvider = 0x01;

    public static SecurityProductState Decode(uint productState)
    {
        int provider = (int)((productState >> 16) & 0xFF);
        int state = (int)((productState >> 8) & 0xFF);
        int definitions = (int)(productState & 0xFF);
        bool enabled = (state & 0x10) != 0;
        bool snoozed = !enabled && (state & 0x01) != 0;
        bool upToDate = (definitions & 0x10) == 0;
        return new SecurityProductState(enabled, snoozed, upToDate, provider);
    }

    /// <summary>The exact display names Windows uses for its own Security Center entries (not translated).</summary>
    private static readonly HashSet<string> MicrosoftNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows Defender",
        "Microsoft Defender",
        "Microsoft Defender Antivirus",
        "Windows Firewall",
        "Windows Defender Firewall",
        "Microsoft Defender Firewall",
    };

    /// <summary>
    /// True for Microsoft Defender's own entry: its reporting path is a <c>windowsdefender://</c> link, or its name is
    /// exactly one of Windows' own names (the name is not translated by Windows). A name that only contains the word,
    /// such as "Bitdefender Antivirus", is a third-party product.
    /// </summary>
    public static bool IsMicrosoftProduct(string? name, string? reportingPath) =>
        (reportingPath?.TrimStart().StartsWith("windowsdefender:", StringComparison.OrdinalIgnoreCase) ?? false)
        || (name is not null && MicrosoftNames.Contains(name.Trim()));

    /// <summary>The third-party product that is switched on, if any; Microsoft's own entries are skipped.</summary>
    public static SecurityProduct? ActiveThirdParty(IEnumerable<SecurityProduct> products)
    {
        ArgumentNullException.ThrowIfNull(products);
        return products.FirstOrDefault(p => !p.IsMicrosoft && p.State.Enabled);
    }
}
