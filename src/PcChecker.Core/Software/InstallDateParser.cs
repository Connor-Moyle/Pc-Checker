using System.Globalization;

namespace PcChecker.Core.Software;

/// <summary>Pure parser for the many date formats installers write to the registry's <c>InstallDate</c> value.</summary>
public static class InstallDateParser
{
    private static readonly string[] Formats =
    [
        "yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd", "yyyyMMddHHmmss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss",
        "MM/dd/yyyy", "M/d/yyyy", "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy",
    ];

    /// <summary>Returns the date, or null when the value is missing, malformed or outside a sensible range.</summary>
    public static DateTime? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact))
        {
            return InRange(exact) ? exact : null;
        }

        // Some installers write a Unix timestamp or DMTF-like digits; try the first eight digits as yyyyMMdd.
        if (text.Length > 8 && text.All(char.IsDigit)
            && DateTime.TryParseExact(text[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var prefix))
        {
            return InRange(prefix) ? prefix : null;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var loose))
        {
            return InRange(loose) ? loose : null;
        }

        return null;
    }

    private static bool InRange(DateTime date) =>
        date.Year >= 1995 && date <= DateTime.Now.AddDays(2);
}
