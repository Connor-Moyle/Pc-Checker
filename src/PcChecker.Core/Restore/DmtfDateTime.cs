using System.Globalization;

namespace PcChecker.Core.Restore;

/// <summary>
/// Parses WMI/DMTF timestamps ("yyyyMMddHHmmss.ffffff±UUU", e.g. <c>20240115103000.000000+060</c>) without depending on
/// System.Management, so restore point times can be converted (and tested) anywhere.
/// </summary>
public static class DmtfDateTime
{
    /// <summary>Tries to parse a DMTF timestamp. Missing fields ("*") default to their minimum; a "***" offset means local time.</summary>
    public static bool TryParse(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.Length < 14)
        {
            return false;
        }

        if (!TryField(value, 0, 4, 1, out int year) || !TryField(value, 4, 2, 1, out int month) || !TryField(value, 6, 2, 1, out int day)
            || !TryField(value, 8, 2, 0, out int hour) || !TryField(value, 10, 2, 0, out int minute) || !TryField(value, 12, 2, 0, out int second))
        {
            return false;
        }

        int microseconds = 0;
        int offsetMinutes = 0;
        bool localTime = true;

        int dot = value.IndexOf('.', 14);
        int signIndex = value.IndexOfAny(['+', '-'], 14);
        if (dot >= 0)
        {
            int end = signIndex > dot ? signIndex : value.Length;
            var fraction = value[(dot + 1)..end];
            if (fraction.Length > 0 && !fraction.Contains('*'))
            {
                fraction = fraction.Length > 6 ? fraction[..6] : fraction.PadRight(6, '0');
                if (!int.TryParse(fraction, NumberStyles.None, CultureInfo.InvariantCulture, out microseconds))
                {
                    return false;
                }
            }
        }

        if (signIndex >= 0 && signIndex < value.Length - 1)
        {
            var offsetText = value[(signIndex + 1)..];
            if (!offsetText.Contains('*') && int.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out int offset))
            {
                offsetMinutes = value[signIndex] == '-' ? -offset : offset;
                localTime = false;
            }
        }

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        try
        {
            var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified).AddTicks(microseconds * 10L);
            result = localTime
                ? new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local))
                : new DateTimeOffset(local, TimeSpan.FromMinutes(offsetMinutes));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Parses a DMTF timestamp or returns <paramref name="fallback"/>.</summary>
    public static DateTimeOffset ParseOrDefault(string? value, DateTimeOffset fallback) => TryParse(value, out var parsed) ? parsed : fallback;

    private static bool TryField(string value, int start, int length, int fallback, out int number)
    {
        var text = value.Substring(start, length);
        if (text.Contains('*'))
        {
            number = fallback;
            return true;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
}
