using System.Collections;
using System.Globalization;
using System.Text;

namespace PcChecker.Core.Hardware;

/// <summary>
/// Pure helpers that turn a WMI-style property bag (<c>IDictionary&lt;string, object?&gt;</c>) into display strings.
/// DMTF dates ("20240101120000.000000+000") become ISO text, arrays are joined, nulls are dropped.
/// </summary>
public static class WmiPropertyMapper
{
    /// <summary>Converts every non-null property to a string. Keys keep their original casing; output is sorted by key.</summary>
    public static IReadOnlyDictionary<string, string> ToStringDictionary(IDictionary<string, object?> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in properties)
        {
            var text = FormatValue(key, value);
            if (text is not null)
            {
                result[key] = text;
            }
        }

        return result;
    }

    /// <summary>Formats a single value for display; null and empty values yield null.</summary>
    public static string? FormatValue(string key, object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                if (string.IsNullOrWhiteSpace(s))
                {
                    return null;
                }

                return LooksLikeDmtfDate(s) && TryParseDmtfDate(s, out var dt) ? dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : s.Trim();
            case bool b:
                return b ? "Yes" : "No";
            case DateTime d:
                return d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case byte[] bytes:
                return bytes.Length == 0 ? null : Convert.ToHexString(bytes);
            case IEnumerable enumerable:
                var parts = new List<string>();
                foreach (var item in enumerable)
                {
                    var t = FormatValue(key, item);
                    if (t is not null)
                    {
                        parts.Add(t);
                    }
                }

                return parts.Count == 0 ? null : string.Join(", ", parts);
            case IFormattable f:
                return f.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString();
        }
    }

    /// <summary>Parses a DMTF datetime (yyyyMMddHHmmss.ffffff±UUU) to local time.</summary>
    public static bool TryParseDmtfDate(string? text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length < 14)
        {
            return false;
        }

        var core = text[..14];
        if (!DateTime.TryParseExact(core, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        // Offset in minutes follows the +/- sign; "***" means unknown (treat as local).
        int signIndex = text.IndexOfAny(['+', '-'], 14);
        if (signIndex > 0 && signIndex + 1 < text.Length && int.TryParse(text[(signIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            var offset = TimeSpan.FromMinutes(text[signIndex] == '-' ? -minutes : minutes);
            value = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), offset).LocalDateTime;
            return true;
        }

        value = DateTime.SpecifyKind(parsed, DateTimeKind.Local);
        return true;
    }

    /// <summary>Reads a string property from a bag, trimming whitespace; null when missing or blank.</summary>
    public static string? GetString(IDictionary<string, object?> bag, string key)
    {
        if (!bag.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        var text = value is string s ? s : FormatValue(key, value);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public static long? GetInt64(IDictionary<string, object?> bag, string key)
    {
        if (!bag.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            long l => l,
            ulong ul => ul > long.MaxValue ? long.MaxValue : (long)ul,
            int i => i,
            uint ui => ui,
            short sh => sh,
            ushort ush => ush,
            byte by => by,
            double d => (long)d,
            float f => (long)f,
            decimal m => (long)m,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    public static int? GetInt32(IDictionary<string, object?> bag, string key)
    {
        var v = GetInt64(bag, key);
        return v is null ? null : (int)Math.Clamp(v.Value, int.MinValue, int.MaxValue);
    }

    public static bool? GetBool(IDictionary<string, object?> bag, string key)
    {
        if (!bag.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            string s when s is "1" => true,
            string s when s is "0" => false,
            int i => i != 0,
            _ => null,
        };
    }

    public static DateTime? GetDate(IDictionary<string, object?> bag, string key)
    {
        if (!bag.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            DateTime d => d,
            string s when TryParseDmtfDate(s, out var dmtf) => dmtf,
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) => parsed,
            _ => null,
        };
    }

    public static IReadOnlyList<string> GetStringList(IDictionary<string, object?> bag, string key)
    {
        if (!bag.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        if (value is string single)
        {
            return string.IsNullOrWhiteSpace(single) ? [] : [single.Trim()];
        }

        if (value is IEnumerable enumerable)
        {
            var list = new List<string>();
            foreach (var item in enumerable)
            {
                var t = item?.ToString();
                if (!string.IsNullOrWhiteSpace(t))
                {
                    list.Add(t.Trim());
                }
            }

            return list;
        }

        return [value.ToString() ?? string.Empty];
    }

    /// <summary>Turns a MAC address or serial into a masked form ("AA:BB:**:**:**:**").</summary>
    public static string Redact(string value, int keep = 4)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= keep)
        {
            return new string('*', value?.Length ?? 0);
        }

        var sb = new StringBuilder(value.Length);
        sb.Append(value, 0, keep);
        for (int i = keep; i < value.Length; i++)
        {
            sb.Append(char.IsLetterOrDigit(value[i]) ? '*' : value[i]);
        }

        return sb.ToString();
    }

    private static bool LooksLikeDmtfDate(string s) =>
        s.Length >= 21 && s[14] == '.' && char.IsDigit(s[0]) && char.IsDigit(s[13]) && (s[^4] is '+' or '-' or '*');
}
