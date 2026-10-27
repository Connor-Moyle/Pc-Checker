using System.Globalization;

namespace PcChecker.Core.Battery;

/// <summary>
/// Parses the ISO 8601 durations the battery report uses (<c>PT1H30M</c>, <c>P2DT4H</c>, <c>PT45.5S</c>, <c>P1W</c>).
/// Years and months have no fixed length, so durations that use them are refused rather than guessed.
/// </summary>
public static class IsoDuration
{
    /// <summary>The duration, or null when <paramref name="text"/> is empty or not a duration this parser accepts.</summary>
    public static TimeSpan? Parse(string? text) => TryParse(text, out var value) ? value : null;

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().ToUpperInvariant();
        bool negative = s.StartsWith('-');
        if (negative)
        {
            s = s[1..];
        }

        if (s.Length < 3 || s[0] != 'P')
        {
            return false;
        }

        double totalSeconds = 0;
        bool inTime = false, anyComponent = false;
        int i = 1;
        var order = "WD"; // allowed date designators, in order
        var timeOrder = "HMS";
        int lastDate = -1, lastTime = -1;
        while (i < s.Length)
        {
            if (s[i] == 'T')
            {
                if (inTime || i == s.Length - 1)
                {
                    return false;
                }

                inTime = true;
                i++;
                continue;
            }

            int start = i;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] is '.' or ','))
            {
                i++;
            }

            if (i == start || i >= s.Length)
            {
                return false;
            }

            var number = s[start..i].Replace(',', '.');
            if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            {
                return false;
            }

            char unit = s[i++];
            if (!inTime)
            {
                int index = order.IndexOf(unit);
                if (index < 0 || index <= lastDate)
                {
                    return false; // Y and M (months) are refused; designators must be in order and appear once
                }

                lastDate = index;
                totalSeconds += unit == 'W' ? amount * 7 * 86400 : amount * 86400;
            }
            else
            {
                int index = timeOrder.IndexOf(unit);
                if (index < 0 || index <= lastTime)
                {
                    return false;
                }

                lastTime = index;
                totalSeconds += unit switch
                {
                    'H' => amount * 3600,
                    'M' => amount * 60,
                    _ => amount,
                };
            }

            anyComponent = true;
        }

        if (!anyComponent || !double.IsFinite(totalSeconds) || totalSeconds > TimeSpan.MaxValue.TotalSeconds / 2)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(negative ? -totalSeconds : totalSeconds);
        return true;
    }

    /// <summary>Writes <paramref name="value"/> as an ISO 8601 duration (<c>PT1H30M</c>); used for the demo report.</summary>
    public static string Format(TimeSpan value)
    {
        var sign = value < TimeSpan.Zero ? "-" : string.Empty;
        value = value.Duration();
        var text = sign + "P";
        if (value.Days > 0)
        {
            text += value.Days.ToString(CultureInfo.InvariantCulture) + "D";
        }

        if (value.Hours == 0 && value.Minutes == 0 && value.Seconds == 0)
        {
            return value.Days > 0 ? text : text + "T0S";
        }

        text += "T";
        if (value.Hours > 0)
        {
            text += value.Hours.ToString(CultureInfo.InvariantCulture) + "H";
        }

        if (value.Minutes > 0)
        {
            text += value.Minutes.ToString(CultureInfo.InvariantCulture) + "M";
        }

        if (value.Seconds > 0)
        {
            text += value.Seconds.ToString(CultureInfo.InvariantCulture) + "S";
        }

        return text;
    }
}
