using System.Globalization;
using System.Net;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Reports;

/// <summary>Formatting helpers shared by the HTML and Markdown report builders. Pure.</summary>
public static class ReportText
{
    public const string Dash = "—";

    /// <summary>HTML-encodes text so user data can never inject markup into the report.</summary>
    public static string Html(string? text) => string.IsNullOrEmpty(text) ? string.Empty : WebUtility.HtmlEncode(text);

    /// <summary>Encodes a value for a Markdown table cell: pipes and line breaks would break the table.</summary>
    public static string Markdown(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);
    }

    /// <summary>Joins the non-empty parts with spaces, or returns null when every part is empty.</summary>
    public static string? Join(params string?[] parts)
    {
        var text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return text.Length == 0 ? null : text;
    }

    /// <summary>Returns <paramref name="text"/>, else <paramref name="fallback"/>, else a dash.</summary>
    public static string Or(string? text, string? fallback = Dash) =>
        !string.IsNullOrWhiteSpace(text) ? text : !string.IsNullOrWhiteSpace(fallback) ? fallback : Dash;

    public static string Bytes(long? bytes) => ByteSize.Format(bytes);

    public static string Date(DateTime? date) =>
        date.HasValue ? date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Dash;

    public static string DateTime(DateTimeOffset? time) =>
        time.HasValue ? time.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : Dash;

    public static string Number(double? value, string format = "0.#", string? unit = null) =>
        value.HasValue && !double.IsNaN(value.Value)
            ? value.Value.ToString(format, CultureInfo.InvariantCulture) + (unit is null ? string.Empty : " " + unit)
            : Dash;

    public static string Number(long? value) => value.HasValue ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : Dash;

    public static string Number(int? value) => value.HasValue ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : Dash;

    public static string YesNo(bool? value) => value switch { true => "Yes", false => "No", _ => Dash };

    public static string Megahertz(int? mhz) => mhz.HasValue ? mhz.Value.ToString(CultureInfo.InvariantCulture) + " MHz" : Dash;

    public static string Percent(double? value) => value.HasValue ? value.Value.ToString("0", CultureInfo.InvariantCulture) + " %" : Dash;

    /// <summary>Plain-English status for a health score.</summary>
    public static string ScoreLabel(int score) => score switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 50 => "Needs attention",
        > 0 => "Problems found",
        _ => "Not checked",
    };

    /// <summary>Uptime as "3 days 4 h" / "5 h 12 min".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        var sb = new StringBuilder();
        if (span.TotalDays >= 1)
        {
            sb.Append((int)span.TotalDays).Append(span.TotalDays >= 2 ? " days " : " day ");
            sb.Append(span.Hours).Append(" h");
            return sb.ToString();
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours} h {span.Minutes} min";
        }

        return $"{Math.Max(0, (int)span.TotalMinutes)} min";
    }

    /// <summary>Safe file name stem for an exported report ("pc-checker-report-GAMING-DESKTOP-2026-10-26-1412").</summary>
    public static string SuggestedFileName(string? computerName, DateTimeOffset when)
    {
        var name = string.IsNullOrWhiteSpace(computerName) ? "pc" : computerName.Trim();
        var cleaned = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            cleaned.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        }

        return $"pc-checker-report-{cleaned}-{when.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}";
    }
}
