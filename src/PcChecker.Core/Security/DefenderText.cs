using PcChecker.Core.Common;

namespace PcChecker.Core.Security;

/// <summary>Plain words for Defender's numeric codes and the age rules for definitions and scans. Pure.</summary>
public static class DefenderText
{
    /// <summary>Definitions this many days old turn the item yellow; twice as old turns it red.</summary>
    public const int DefinitionWarningDays = 3;

    /// <summary>No scan for this many days turns the scan line yellow.</summary>
    public const int ScanWarningDays = 14;

    /// <summary>Defender reports "never" as the largest unsigned 32-bit number.</summary>
    public const long NeverAge = uint.MaxValue;

    /// <summary><c>MSFT_MpThreat.SeverityID</c>.</summary>
    public static string Severity(int severityId) => severityId switch
    {
        1 => "Low",
        2 => "Moderate",
        4 => "High",
        5 => "Severe",
        _ => "Unknown",
    };

    /// <summary><c>MSFT_MpThreatDetection.ThreatStatusID</c>.</summary>
    public static string ThreatStatus(int statusId) => statusId switch
    {
        1 => "Detected",
        2 => "Cleaned",
        3 => "Quarantined",
        4 => "Removed",
        5 => "Allowed",
        6 => "Blocked",
        102 => "Quarantine failed",
        103 => "Removal failed",
        104 => "Allow failed",
        105 => "Abandoned",
        107 => "Block failed",
        _ => "Unknown",
    };

    /// <summary>True when the threat was not dealt with and still needs the user (detected, allowed or a failed action).</summary>
    public static bool NeedsAttention(int statusId) => statusId is 1 or 102 or 103 or 105 or 107;

    /// <summary>Green while fresh, yellow after <see cref="DefinitionWarningDays"/>, red after twice that.</summary>
    public static HealthStatus DefinitionsStatus(int? ageDays) => ageDays switch
    {
        null => HealthStatus.Unknown,
        < 0 => HealthStatus.Unknown,
        <= DefinitionWarningDays => HealthStatus.Good,
        <= DefinitionWarningDays * 2 => HealthStatus.Warning,
        _ => HealthStatus.Critical,
    };

    /// <summary>A Defender age value (days) or null when it means "never" or is out of range.</summary>
    public static int? Age(long? raw) => raw is null or < 0 or >= NeverAge ? null : (int)Math.Min(raw.Value, int.MaxValue);

    /// <summary>"today", "yesterday", "5 days ago", or "never".</summary>
    public static string Ago(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is not { } time || time.Year < 2000)
        {
            return "never";
        }

        int days = (int)Math.Floor((now.Date - time.ToOffset(now.Offset).Date).TotalDays);
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            _ => $"{days} days ago",
        };
    }

    /// <summary>
    /// A threat resource without Defender's type prefix ("file:_C:\x.exe" → "C:\x.exe", "regkey:_HKLM\…" → "HKLM\…").
    /// </summary>
    public static string? CleanResource(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return null;
        }

        var text = resource.Trim();
        int marker = text.IndexOf(":_", StringComparison.Ordinal);
        return marker is > 0 and < 16 ? text[(marker + 2)..] : text;
    }

    /// <summary>True when Defender is the active antivirus (not passive behind another product).</summary>
    public static bool IsActive(DefenderStatus? status) =>
        status is not null && status.AntivirusEnabled != false
        && (status.RunningMode is null || status.RunningMode.Equals("Normal", StringComparison.OrdinalIgnoreCase));
}
