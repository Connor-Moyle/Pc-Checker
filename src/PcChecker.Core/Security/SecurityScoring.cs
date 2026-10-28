using PcChecker.Core.Common;

namespace PcChecker.Core.Security;

/// <summary>The ids of the checks on the Security page.</summary>
public static class SecurityCheckIds
{
    public const string Antivirus = "antivirus";
    public const string Firewall = "firewall";
    public const string WindowsVersion = "windows-version";
    public const string UpdatesPaused = "updates-paused";
    public const string Uac = "uac";
    public const string SmartScreen = "smartscreen";
    public const string Smb1 = "smb1";
    public const string Encryption = "encryption";
    public const string SecureBoot = "secure-boot";
    public const string Tpm = "tpm";
    public const string MemoryIntegrity = "memory-integrity";
    public const string CredentialGuard = "credential-guard";
    public const string Activation = "activation";
    public const string RemoteDesktop = "remote-desktop";
    public const string Guest = "guest";
    public const string PendingRestart = "pending-restart";
}

/// <summary>
/// The overall security score. Every check has a weight; a red item costs its full weight, a yellow one half (rounded
/// up), green and unknown items cost nothing, and the score never drops below zero. The first things to fix are the
/// red items before the yellow ones, the heaviest first, at most three. Pure.
/// </summary>
public static class SecurityScoring
{
    public const int MaxTopFixes = 3;

    private static readonly Dictionary<string, int> Weights = new(StringComparer.Ordinal)
    {
        [SecurityCheckIds.Antivirus] = 25,
        [SecurityCheckIds.Firewall] = 20,
        [SecurityCheckIds.WindowsVersion] = 15,
        [SecurityCheckIds.Uac] = 10,
        [SecurityCheckIds.Smb1] = 10,
        [SecurityCheckIds.SmartScreen] = 8,
        [SecurityCheckIds.Encryption] = 8,
        [SecurityCheckIds.UpdatesPaused] = 6,
        [SecurityCheckIds.SecureBoot] = 6,
        [SecurityCheckIds.Activation] = 5,
        [SecurityCheckIds.MemoryIntegrity] = 5,
        [SecurityCheckIds.Guest] = 5,
        [SecurityCheckIds.Tpm] = 4,
        [SecurityCheckIds.RemoteDesktop] = 4,
        [SecurityCheckIds.PendingRestart] = 3,
        [SecurityCheckIds.CredentialGuard] = 2,
    };

    /// <summary>The weight of a check id; unknown ids weigh 2.</summary>
    public static int WeightOf(string id) => Weights.TryGetValue(id, out var w) ? w : 2;

    /// <summary>What one check costs the score.</summary>
    public static int Penalty(SecurityCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.Status switch
        {
            HealthStatus.Critical => check.Weight,
            HealthStatus.Warning => (check.Weight + 1) / 2,
            _ => 0,
        };
    }

    public static SecurityScore Score(IReadOnlyList<SecurityCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        int value = Math.Max(0, 100 - checks.Sum(Penalty));
        bool anyCritical = checks.Any(c => c.Status == HealthStatus.Critical);
        var status = anyCritical || value < 70 ? HealthStatus.Critical : value < 90 ? HealthStatus.Warning : HealthStatus.Good;
        string grade = status switch
        {
            HealthStatus.Good => "Well protected",
            HealthStatus.Warning => "Mostly protected",
            _ => "Needs attention",
        };

        return new SecurityScore(value, grade, status, TopFixes(checks));
    }

    /// <summary>Red items before yellow ones, heaviest first, at most <see cref="MaxTopFixes"/>.</summary>
    public static IReadOnlyList<SecurityCheck> TopFixes(IEnumerable<SecurityCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return checks
            .Where(c => c.Status is HealthStatus.Critical or HealthStatus.Warning)
            .OrderByDescending(c => c.Status == HealthStatus.Critical)
            .ThenByDescending(c => c.Weight)
            .ThenBy(c => c.Title, StringComparer.Ordinal)
            .Take(MaxTopFixes)
            .ToList();
    }
}
