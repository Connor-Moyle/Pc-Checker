using System.Globalization;
using PcChecker.Core.Common;

namespace PcChecker.Core.Security;

/// <summary>
/// Turns the raw <see cref="SecurityFacts"/> into the traffic-light checks on the Security page, most important first.
/// A value that could not be read gives a grey item and never counts against the score. Pure.
/// </summary>
public static partial class SecurityCheckBuilder
{
    public static IReadOnlyList<SecurityCheck> Build(SecurityFacts facts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var checks = new List<SecurityCheck>
        {
            Antivirus(facts, now),
            Firewall(facts),
            WindowsVersion(facts, DateOnly.FromDateTime(now.Date)),
            UpdatesPaused(facts, now),
            Uac(facts),
            SmartScreen(facts),
            Smb1(facts),
            Encryption(facts),
            SecureBoot(facts),
            Tpm(facts),
            MemoryIntegrity(facts),
            CredentialGuard(facts),
            Activation(facts),
            RemoteDesktop(facts),
            Guest(facts),
            PendingRestart(facts),
        };
        return checks;
    }

    public static SecurityCheck Antivirus(SecurityFacts facts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string title = "Virus protection";
        const string meaning = "Antivirus checks files and programs as they run and blocks known malware.";
        if (SecurityCenterState.ActiveThirdParty(facts.Antivirus) is { } other)
        {
            var state = other.State;
            return new SecurityCheck(SecurityCheckIds.Antivirus, title,
                state.UpToDate ? HealthStatus.Good : HealthStatus.Warning,
                state.UpToDate ? $"{other.Name} is on and up to date" : $"{other.Name} is on, but its definitions are out of date",
                meaning + " Another antivirus is in charge, so Microsoft Defender steps aside; that is normal.",
                SecurityLinks.VirusProtection)
            {
                Details = [$"Active antivirus: {other.Name}", "Open the antivirus program itself to update it or run a scan."],
            };
        }

        var defender = facts.Defender;
        if (defender is null)
        {
            bool noneRegistered = facts.Antivirus.Count > 0 && facts.Antivirus.All(p => !p.State.Enabled);
            return new SecurityCheck(SecurityCheckIds.Antivirus, title,
                noneRegistered ? HealthStatus.Critical : HealthStatus.Unknown,
                noneRegistered ? "No antivirus is switched on" : "Could not read the antivirus status",
                meaning, SecurityLinks.VirusProtection);
        }

        var parts = new List<HealthStatus>();
        string summary;
        SecurityFix fix = SecurityFix.None;
        if (defender.AntivirusEnabled == false || defender.RealTimeProtection == false)
        {
            parts.Add(HealthStatus.Critical);
            summary = defender.AntivirusEnabled == false
                ? "Microsoft Defender is switched off and no other antivirus is on"
                : "Microsoft Defender's real-time protection is off";
        }
        else
        {
            summary = "Microsoft Defender is on";
        }

        var definitions = DefenderText.DefinitionsStatus(defender.DefinitionAgeDays);
        parts.Add(definitions);
        if (definitions is HealthStatus.Warning or HealthStatus.Critical)
        {
            summary += $", but its definitions are {defender.DefinitionAgeDays} days old";
            fix = SecurityFix.UpdateDefinitions;
        }
        else if (definitions == HealthStatus.Good)
        {
            summary += " and up to date";
        }

        if (defender.TamperProtection == false)
        {
            parts.Add(HealthStatus.Warning);
        }

        if (defender.CloudProtection == false)
        {
            parts.Add(HealthStatus.Warning);
        }

        var details = new List<string>
        {
            "Real-time protection: " + OnOff(defender.RealTimeProtection),
            "Tamper protection: " + OnOff(defender.TamperProtection) + (defender.TamperProtection == false ? " (stops malware from switching Defender off)" : string.Empty),
            "Cloud-delivered protection: " + OnOff(defender.CloudProtection),
            "Definitions: " + (defender.DefinitionVersion ?? "unknown")
                + (defender.DefinitionsUpdated is { } updated ? $", updated {DefenderText.Ago(updated, now)}" : string.Empty),
            "Last quick scan: " + DefenderText.Ago(defender.LastQuickScan, now),
            "Last full scan: " + DefenderText.Ago(defender.LastFullScan, now),
            "Engine " + (defender.EngineVersion ?? "unknown") + ", platform " + (defender.PlatformVersion ?? "unknown"),
        };
        if (defender.RunningMode is { } mode && !mode.Equals("Normal", StringComparison.OrdinalIgnoreCase))
        {
            details.Add($"Running mode: {mode}");
        }

        return new SecurityCheck(SecurityCheckIds.Antivirus, title, Worst(parts), summary, meaning, SecurityLinks.VirusProtection, fix)
        {
            Details = details,
        };
    }

    public static SecurityCheck Firewall(SecurityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string title = "Firewall";
        const string meaning = "The firewall blocks unwanted connections from the network and the internet.";
        if (SecurityCenterState.ActiveThirdParty(facts.Firewalls) is { } other)
        {
            return new SecurityCheck(SecurityCheckIds.Firewall, title, HealthStatus.Good, $"{other.Name} is protecting this PC",
                meaning + " Another firewall is in charge, so Windows Firewall may be off; that is normal.", SecurityLinks.Firewall);
        }

        if (facts.FirewallProfiles.Count == 0 || facts.FirewallProfiles.All(p => p.Enabled is null))
        {
            return new SecurityCheck(SecurityCheckIds.Firewall, title, HealthStatus.Unknown, "Could not read the firewall status", meaning, SecurityLinks.Firewall);
        }

        var off = facts.FirewallProfiles.Where(p => p.Enabled == false).Select(p => p.Profile).ToList();
        var details = facts.FirewallProfiles.Select(p => $"{ProfileName(p.Profile)}: {OnOff(p.Enabled)}").ToList();
        if (off.Count == 0)
        {
            return new SecurityCheck(SecurityCheckIds.Firewall, title, HealthStatus.Good, "Windows Firewall is on for every network type", meaning, SecurityLinks.Firewall)
            {
                Details = details,
            };
        }

        bool onlyDomain = off.All(p => p.Equals("Domain", StringComparison.OrdinalIgnoreCase));
        string list = string.Join(" and ", off.Select(ProfileName));
        return new SecurityCheck(SecurityCheckIds.Firewall, title, onlyDomain ? HealthStatus.Warning : HealthStatus.Critical,
            $"Windows Firewall is off for {list}", meaning, SecurityLinks.Firewall, SecurityFix.TurnOnFirewall, string.Join(",", off))
        {
            Details = details,
        };
    }

    /// <summary>"Domain" → "work networks (domain)", "Private" → "home networks (private)", "Public" → "public networks".</summary>
    public static string ProfileName(string profile) => profile.ToLowerInvariant() switch
    {
        "domain" => "work networks (domain)",
        "private" => "home networks (private)",
        "public" => "public networks",
        _ => profile,
    };

    public static SecurityCheck WindowsVersion(SecurityFacts facts, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string title = "Windows version";
        const string meaning = "Microsoft sends security fixes only to supported versions of Windows.";
        if (facts.Windows is not { } windows || windows.Build <= 0)
        {
            return new SecurityCheck(SecurityCheckIds.WindowsVersion, title, HealthStatus.Unknown, "Could not read the Windows version", meaning, SecurityLinks.WindowsUpdateSettings);
        }

        var info = WindowsServicing.Lookup(windows.Build, windows.DisplayVersion, windows.EditionId);
        var status = WindowsServicing.Status(info, today);
        string edition = EditionName(windows);
        string build = windows.Revision > 0 ? $"{windows.Build}.{windows.Revision}" : windows.Build.ToString(CultureInfo.InvariantCulture);
        string label = $"{info.VersionName} {edition}".TrimEnd();
        string summary = info.EndOfServicing is not { } end
            ? $"{label}: check Microsoft's lifecycle page for its end of support"
            : status switch
            {
                HealthStatus.Critical => $"{label} stopped getting security updates on {Date(end)}",
                HealthStatus.Warning => $"{label} gets security updates until {Date(end)}, update soon",
                _ => $"{label} gets security updates until {Date(end)}",
            };
        var details = new List<string> { $"Build {build}" };
        if (status is HealthStatus.Critical or HealthStatus.Warning)
        {
            details.Add("Install the newest Windows version from Windows Update to keep getting fixes.");
        }

        if (info.VersionName.StartsWith("Windows 10", StringComparison.Ordinal) && status == HealthStatus.Critical)
        {
            details.Add("PCs enrolled in Extended Security Updates still get fixes; check Windows Update.");
        }

        if (!info.IsKnown)
        {
            details.Add(WindowsServicing.LifecycleUrl);
        }

        return new SecurityCheck(SecurityCheckIds.WindowsVersion, title, status, summary, meaning, SecurityLinks.WindowsUpdateSettings)
        {
            Details = details,
        };
    }

    public static SecurityCheck UpdatesPaused(SecurityFacts facts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);
        const string title = "Automatic updates";
        const string meaning = "Paused updates mean security fixes wait until the pause ends.";
        if (facts.UpdatesPausedUntil is { } until && until > now)
        {
            return new SecurityCheck(SecurityCheckIds.UpdatesPaused, title, HealthStatus.Warning,
                $"Windows updates are paused until {Date(DateOnly.FromDateTime(until.ToLocalTime().Date))}", meaning, SecurityLinks.WindowsUpdatePage);
        }

        return new SecurityCheck(SecurityCheckIds.UpdatesPaused, title, HealthStatus.Good, "Windows installs updates automatically", meaning, SecurityLinks.WindowsUpdatePage);
    }

    /// <summary>"Core" → "Home", "Professional…" → "Pro", "Enterprise…" → "Enterprise", "Education…" → "Education".</summary>
    public static string EditionName(WindowsVersionInfo windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        string id = windows.EditionId?.Trim() ?? string.Empty;
        return id switch
        {
            _ when id.StartsWith("Core", StringComparison.OrdinalIgnoreCase) => "Home",
            _ when id.StartsWith("Professional", StringComparison.OrdinalIgnoreCase) => "Pro",
            _ when id.StartsWith("EnterpriseS", StringComparison.OrdinalIgnoreCase) => "Enterprise LTSC",
            _ when id.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) => "Enterprise",
            _ when id.StartsWith("Education", StringComparison.OrdinalIgnoreCase) => "Education",
            _ => id,
        };
    }

    internal static string OnOff(bool? value) => value switch
    {
        true => "on",
        false => "off",
        null => "unknown",
    };

    internal static string Date(DateOnly date) => date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The worst of several statuses; unknown parts are ignored unless everything is unknown.</summary>
    public static HealthStatus Worst(IEnumerable<HealthStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var known = statuses.Where(s => s != HealthStatus.Unknown).ToList();
        if (known.Count == 0)
        {
            return HealthStatus.Unknown;
        }

        return known.Contains(HealthStatus.Critical) ? HealthStatus.Critical
            : known.Contains(HealthStatus.Warning) ? HealthStatus.Warning
            : HealthStatus.Good;
    }
}
