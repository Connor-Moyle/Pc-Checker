using PcChecker.Core.Common;

namespace PcChecker.Core.Health;

/// <summary>Pure rules for the "Security basics" section: Defender state decoding, firewall parsing and BitLocker wording.</summary>
public static class SecurityRules
{
    /// <summary>One antivirus product as reported by <c>root\SecurityCenter2 AntiVirusProduct</c>.</summary>
    public sealed record AntivirusProduct(string Name, int ProductState)
    {
        public bool Enabled => IsEnabled(ProductState);
        public bool UpToDate => IsUpToDate(ProductState);
    }

    /// <summary>
    /// The product state is a bit field: bits 12..15 hold the "on/off" code (0x10 = enabled, 0x00/0x01 = disabled/snoozed),
    /// bits 4..7 hold the signature state (0x00 = up to date, 0x10 = out of date).
    /// </summary>
    public static bool IsEnabled(int productState) => ((productState >> 12) & 0xF) == 0x1;

    public static bool IsUpToDate(int productState) => ((productState >> 4) & 0xF) == 0x0;

    /// <summary>Describes the antivirus situation as one health item.</summary>
    public static HealthItem AntivirusItem(IReadOnlyList<AntivirusProduct> products)
    {
        ArgumentNullException.ThrowIfNull(products);
        if (products.Count == 0)
        {
            return new HealthItem("Security", HealthStatus.Warning, "No antivirus product is registered",
                "Windows Security Center does not list any antivirus. Windows Defender is usually on unless something turned it off.",
                "Open Windows Security and make sure Virus & threat protection is on.", HealthItem.NoMetrics);
        }

        var active = products.Where(p => p.Enabled).ToList();
        var metrics = products.ToDictionary(p => p.Name, p => (p.Enabled ? "On" : "Off") + (p.UpToDate ? ", up to date" : ", out of date"));
        if (active.Count == 0)
        {
            return new HealthItem("Security", HealthStatus.Critical, "Antivirus protection is turned off",
                $"{Join(products.Select(p => p.Name))} is installed but not running.",
                "Turn real-time protection back on in Windows Security, or in the antivirus program you use.", metrics);
        }

        var stale = active.Where(p => !p.UpToDate).ToList();
        if (stale.Count > 0)
        {
            return new HealthItem("Security", HealthStatus.Warning, "Antivirus definitions are out of date",
                $"{Join(stale.Select(p => p.Name))} is running but its virus definitions are old.",
                "Run an update from the antivirus program (Windows Security → Virus & threat protection → Check for updates).", metrics);
        }

        return new HealthItem("Security", HealthStatus.Good, "Antivirus is on and up to date",
            $"{Join(active.Select(p => p.Name))} is protecting this PC.", null, metrics);
    }

    /// <summary>Parses <c>netsh advfirewall show allprofiles</c> output into profile → enabled.</summary>
    public static IReadOnlyDictionary<string, bool> ParseNetshProfiles(string? output)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        string? currentProfile = null;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.EndsWith("Profile Settings:", StringComparison.OrdinalIgnoreCase))
            {
                currentProfile = line[..line.IndexOf(" Profile Settings:", StringComparison.OrdinalIgnoreCase)].Trim();
                continue;
            }

            if (currentProfile is not null && line.StartsWith("State", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["State".Length..].Trim();
                result[currentProfile] = value.StartsWith("ON", StringComparison.OrdinalIgnoreCase);
            }
        }

        return result;
    }

    /// <summary>Describes the firewall profiles as one item. Null/empty means the state could not be read.</summary>
    public static HealthItem FirewallItem(IReadOnlyDictionary<string, bool>? profiles)
    {
        if (profiles is null || profiles.Count == 0)
        {
            return new HealthItem("Security", HealthStatus.Unknown, "Firewall state could not be read",
                "The Windows Firewall profiles could not be queried.", "Open Windows Security → Firewall & network protection to check.", HealthItem.NoMetrics);
        }

        var metrics = profiles.ToDictionary(p => p.Key + " profile", p => p.Value ? "On" : "Off");
        var off = profiles.Where(p => !p.Value).Select(p => p.Key).ToList();
        if (off.Count == profiles.Count)
        {
            return new HealthItem("Security", HealthStatus.Critical, "Windows Firewall is off on every network",
                "No firewall profile is active, so nothing filters incoming connections.",
                "Turn the firewall on in Windows Security → Firewall & network protection.", metrics);
        }

        if (off.Count > 0)
        {
            return new HealthItem("Security", HealthStatus.Warning, $"Windows Firewall is off for the {Join(off)} profile{(off.Count > 1 ? "s" : string.Empty)}",
                "Some networks are not filtered. This is sometimes done on purpose for gaming or servers, but it lowers protection.",
                "Turn the firewall on for every profile unless you have a specific reason not to.", metrics);
        }

        return new HealthItem("Security", HealthStatus.Good, "Windows Firewall is on", "Every network profile is protected.", null, metrics);
    }

    /// <summary>Describes BitLocker drive encryption; off is informational, not a health fault.</summary>
    public static HealthItem BitLockerItem(IReadOnlyList<(string Drive, int ProtectionStatus)> volumes, bool isPortable)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        if (volumes.Count == 0)
        {
            return new HealthItem("Security", HealthStatus.Unknown, "Drive encryption status unavailable",
                "BitLocker is not available on this edition of Windows, or the query was blocked.", null, HealthItem.NoMetrics);
        }

        var metrics = volumes.ToDictionary(v => v.Drive, v => v.ProtectionStatus switch { 1 => "Encrypted", 0 => "Not encrypted", _ => "Unknown" });
        var unprotected = volumes.Where(v => v.ProtectionStatus == 0).Select(v => v.Drive).ToList();
        if (unprotected.Count == 0)
        {
            return new HealthItem("Security", HealthStatus.Good, "Drives are encrypted with BitLocker",
                "Your data stays private if the PC or drive is lost.", null, metrics);
        }

        var status = isPortable ? HealthStatus.Warning : HealthStatus.Good;
        return new HealthItem("Security", status, $"BitLocker is off on {Join(unprotected)}",
            isPortable
                ? "A laptop can be lost or stolen; without encryption anyone can read the files on it."
                : "Drive encryption is optional on a desktop. Turn it on if the PC holds sensitive files.",
            isPortable ? "Turn on BitLocker in Settings → Privacy & security → Device encryption." : null, metrics);
    }

    private static string Join(IEnumerable<string> names) => string.Join(", ", names);
}
