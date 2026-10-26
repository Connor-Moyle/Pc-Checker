using System.Globalization;

namespace PcChecker.Core.Performance;

/// <summary>Pure parsers for the command-line and registry text the performance analyzer reads.</summary>
public static class PerformanceTextParsers
{
    /// <summary>
    /// Reads <c>fsutil behavior query DisableDeleteNotify</c>. Returns true when TRIM is enabled
    /// (DisableDeleteNotify = 0), false when disabled, null when the text cannot be read. Prefers the NTFS line
    /// on Windows 10/11 ("NTFS DisableDeleteNotify = 0  (Disabled)").
    /// </summary>
    public static bool? ParseTrimEnabled(string? fsutilOutput)
    {
        if (string.IsNullOrWhiteSpace(fsutilOutput))
        {
            return null;
        }

        bool? fallback = null;
        foreach (var raw in fsutilOutput.Split('\n'))
        {
            var line = raw.Trim();
            int idx = line.IndexOf("DisableDeleteNotify", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            int eq = line.IndexOf('=', idx);
            if (eq < 0)
            {
                continue;
            }

            var valueText = line[(eq + 1)..].Trim();
            int end = 0;
            while (end < valueText.Length && char.IsDigit(valueText[end]))
            {
                end++;
            }

            if (end == 0 || !int.TryParse(valueText[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                continue;
            }

            bool enabled = value == 0;
            if (line.StartsWith("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                return enabled;
            }

            fallback ??= enabled;
        }

        return fallback;
    }

    /// <summary>Converts the BootTime value (milliseconds) of event 100 into seconds with one decimal.</summary>
    public static double? BootTimeToSeconds(long? bootTimeMs) =>
        bootTimeMs is { } ms && ms > 0 ? Math.Round(ms / 1000.0, 1) : null;

    /// <summary>Parses the first integer found in a line of text (e.g. the BootTime printed by PowerShell).</summary>
    public static bool TryParseBootTimeMs(string? text, out long bootTimeMs)
    {
        bootTimeMs = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int start = 0;
            while (start < line.Length && !char.IsDigit(line[start]))
            {
                start++;
            }

            int end = start;
            while (end < line.Length && char.IsDigit(line[end]))
            {
                end++;
            }

            if (end > start && long.TryParse(line[start..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out bootTimeMs))
            {
                return bootTimeMs > 0;
            }
        }

        return false;
    }

    /// <summary>
    /// Days since the Windows Update "LastSuccessTime" registry value (format <c>yyyy-MM-dd HH:mm:ss</c>, UTC).
    /// Returns null when the value is missing or unreadable.
    /// </summary>
    public static int? DaysSinceUpdateCheck(string? lastSuccessTime, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(lastSuccessTime))
        {
            return null;
        }

        var formats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff" };
        if (DateTime.TryParseExact(lastSuccessTime.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            var days = (now.UtcDateTime - parsed).TotalDays;
            return days < 0 ? 0 : (int)Math.Floor(days);
        }

        if (DateTimeOffset.TryParse(lastSuccessTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var loose))
        {
            var days = (now - loose).TotalDays;
            return days < 0 ? 0 : (int)Math.Floor(days);
        }

        return null;
    }

    /// <summary>Uptime in days from the milliseconds since boot.</summary>
    public static double UptimeDays(long millisecondsSinceBoot) =>
        millisecondsSinceBoot <= 0 ? 0 : Math.Round(millisecondsSinceBoot / 86_400_000.0, 2);

    /// <summary>Maps the <c>MSFT_PhysicalDisk.MediaType</c> code to a friendly name.</summary>
    public static string MediaTypeName(int? mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "Unknown",
    };

    /// <summary>Friendly uptime text: "3 days 4 h", "5 h 12 min", "42 min".</summary>
    public static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
        {
            return $"{(int)uptime.TotalDays} day{((int)uptime.TotalDays == 1 ? string.Empty : "s")} {uptime.Hours} h";
        }

        if (uptime.TotalHours >= 1)
        {
            return $"{(int)uptime.TotalHours} h {uptime.Minutes} min";
        }

        return $"{Math.Max(0, (int)uptime.TotalMinutes)} min";
    }
}
