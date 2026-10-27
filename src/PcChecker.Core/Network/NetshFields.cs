using System.Globalization;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Network;

/// <summary>One "<c>label : value</c>" line of netsh output, with its indentation.</summary>
public sealed record NetshField(int Indent, string Label, string Value, int Line);

/// <summary>
/// Shape-based helpers for netsh output. netsh prints localized labels ("Channel", "Kanal", "Canal"), so values are
/// recognised by what they look like (MAC address, percentage, "802.11ax", "5 GHz") first and by label only to break
/// ties. Pure; no Windows dependency.
/// </summary>
public static partial class NetshFields
{
    /// <summary>Splits output into fields. Lines without a "<c>&lt;spaces&gt;:</c>" separator (headers, blank lines) are skipped.</summary>
    public static IReadOnlyList<NetshField> Parse(string? output)
    {
        var fields = new List<NetshField>();
        if (string.IsNullOrEmpty(output))
        {
            return fields;
        }

        var lines = output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var match = FieldRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            int indent = match.Groups["indent"].Value.Replace("\t", "    ").Length;
            fields.Add(new NetshField(indent, match.Groups["label"].Value.Trim(), match.Groups["value"].Value.Trim(), i));
        }

        return fields;
    }

    /// <summary>
    /// Groups consecutive field lines into blocks; a blank or non-field line ends a block. Used for
    /// <c>show interfaces</c>, where every adapter is one block.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<NetshField>> Blocks(IReadOnlyList<NetshField> fields)
    {
        var blocks = new List<IReadOnlyList<NetshField>>();
        var current = new List<NetshField>();
        int lastLine = int.MinValue;
        foreach (var field in fields)
        {
            if (current.Count > 0 && field.Line != lastLine + 1)
            {
                blocks.Add(current);
                current = [];
            }

            current.Add(field);
            lastLine = field.Line;
        }

        if (current.Count > 0)
        {
            blocks.Add(current);
        }

        return blocks;
    }

    public static bool IsMac(string? value) => value is not null && MacRegex().IsMatch(value.Trim());

    /// <summary>"A4-42-3B-11-22-33" → "a4:42:3b:11:22:33".</summary>
    public static string NormalizeMac(string value) => value.Trim().Replace('-', ':').ToLowerInvariant();

    public static bool IsGuid(string? value) => value is not null && Guid.TryParse(value.Trim().Trim('{', '}'), out _);

    /// <summary>"78%" or "78 %" → 78.</summary>
    public static int? Percent(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var match = PercentRegex().Match(value.Trim());
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p is >= 0 and <= 100 ? p : null;
    }

    /// <summary>A bare integer ("36") or null.</summary>
    public static int? Integer(string? value) =>
        value is not null && IntegerRegex().IsMatch(value.Trim()) && int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

    /// <summary>A single number with "." or "," as the decimal separator ("866.7", "866,7", "1201").</summary>
    public static double? Number(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = value.Trim();
        if (!NumberRegex().IsMatch(text))
        {
            return null;
        }

        return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
    }

    /// <summary>"802.11ax" (radio standard) or null.</summary>
    public static string? Radio(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var match = RadioRegex().Match(value.Trim());
        return match.Success ? match.Value : null;
    }

    /// <summary>"5 GHz", "2,4 GHz", "6GHz" → "5 GHz" / "2.4 GHz" / "6 GHz"; null when the value is not a band.</summary>
    public static string? Band(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var match = BandRegex().Match(value.Trim());
        if (!match.Success)
        {
            return null;
        }

        var number = match.Groups[1].Value.Replace(',', '.');
        return number switch
        {
            "2.4" or "2.5" => WifiBands.Band24,
            "5" => WifiBands.Band5,
            "6" => WifiBands.Band6,
            _ => number + " GHz",
        };
    }

    /// <summary>A negative dBm value ("-54" or "-54 dBm").</summary>
    public static int? Dbm(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var match = DbmRegex().Match(value.Trim());
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int dbm) && dbm is >= -120 and < 0 ? dbm : null;
    }

    /// <summary>A label that carries a unit in parentheses, such as "Receive rate (Mbps)" or "Empfangsrate (MBit/s)".</summary>
    public static bool IsRateLabel(string label) => RateLabelRegex().IsMatch(label);

    /// <summary>True when <paramref name="label"/> starts with one of the given words (case-insensitive).</summary>
    public static bool LabelStartsWith(string label, IEnumerable<string> words) =>
        words.Any(w => label.StartsWith(w, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^(?<indent>\s*)(?<label>\S.*?)\s+:(?:\s(?<value>.*)|(?<value>))$")]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{2}([:-][0-9a-fA-F]{2}){5}$")]
    private static partial Regex MacRegex();

    [GeneratedRegex(@"^(\d{1,3})\s?%$")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"^\d{1,6}$")]
    private static partial Regex IntegerRegex();

    [GeneratedRegex(@"^\d{1,6}(?:[.,]\d{1,3})?$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^802\.11[a-z0-9]*", RegexOptions.IgnoreCase)]
    private static partial Regex RadioRegex();

    [GeneratedRegex(@"^(\d(?:[.,]\d)?)\s?GHz$", RegexOptions.IgnoreCase)]
    private static partial Regex BandRegex();

    [GeneratedRegex(@"^(-\d{2,3})(?:\s?dBm)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DbmRegex();

    [GeneratedRegex(@"\(\s*[^)]*(?:bps|bit/s|b/s|бит/с|bit/sn)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RateLabelRegex();
}
