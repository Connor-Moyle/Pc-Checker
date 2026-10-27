using System.Globalization;

namespace PcChecker.Core.Debloat;

/// <summary>
/// Pure helpers for registry values in canonical text: DWORD as unsigned decimal, strings as-is, binary as lowercase
/// hex without separators. Captured values are stored as <c>kind:value</c> so the exact type comes back on revert.
/// </summary>
public static class RegistryValueCodec
{
    public static string HiveName(DebloatHive hive) => hive == DebloatHive.LocalMachine ? "HKLM" : "HKCU";

    /// <summary>"kind:value" for the capture file; null (value was absent) stays null.</summary>
    public static string? Encode(RegistryData? data) => data is null ? null : $"{KindName(data.Kind)}:{data.Value}";

    /// <summary>Parses <see cref="Encode"/> output; returns null for null or malformed text.</summary>
    public static RegistryData? Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return null;
        }

        int colon = encoded.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        var kind = encoded[..colon].ToLowerInvariant() switch
        {
            "dword" => DebloatValueKind.DWord,
            "sz" => DebloatValueKind.String,
            "binary" => (DebloatValueKind?)DebloatValueKind.Binary,
            "expand_sz" => DebloatValueKind.ExpandString,
            "multi_sz" => DebloatValueKind.MultiString,
            "qword" => DebloatValueKind.QWord,
            _ => null,
        };
        return kind is null ? null : new RegistryData(kind.Value, encoded[(colon + 1)..]);
    }

    public static string KindName(DebloatValueKind kind) => kind switch
    {
        DebloatValueKind.DWord => "dword",
        DebloatValueKind.Binary => "binary",
        DebloatValueKind.ExpandString => "expand_sz",
        DebloatValueKind.MultiString => "multi_sz",
        DebloatValueKind.QWord => "qword",
        _ => "sz",
    };

    /// <summary>
    /// True when the current value equals the expected catalog value. DWORDs compare numerically (so "0" matches a
    /// value stored as a string "0"), strings case-insensitively, and binary values by their first byte because Windows
    /// stores a timestamp after it in the StartupApproved flags.
    /// </summary>
    public static bool Matches(RegistryData? current, DebloatValueKind kind, string? expected)
    {
        if (expected is null)
        {
            return current is null;
        }

        if (current is null)
        {
            return false;
        }

        switch (kind)
        {
            case DebloatValueKind.DWord:
                return TryParseDword(current.Value, out uint a) && TryParseDword(expected, out uint b) && a == b;
            case DebloatValueKind.Binary:
            {
                var x = ParseHex(current.Value);
                var y = ParseHex(expected);
                return x is { Length: > 0 } && y is { Length: > 0 } && x[0] == y[0];
            }

            default:
                return string.Equals(current.Value, expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Parses QWORD text: decimal (signed or unsigned) or 0x-prefixed hex.</summary>
    public static bool TryParseQword(string? text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed))
        {
            value = unchecked((ulong)signed);
            return true;
        }

        return false;
    }

    /// <summary>Parses DWORD text: decimal (signed or unsigned) or 0x-prefixed hex.</summary>
    public static bool TryParseDword(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int signed))
        {
            value = unchecked((uint)signed);
            return true;
        }

        return false;
    }

    /// <summary>Lowercase hex without separators.</summary>
    public static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>Parses hex with optional spaces, commas or dashes; null when malformed.</summary>
    public static byte[]? ParseHex(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var clean = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (clean.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(clean);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Short display form for dialogs: 0, "text", or hex bytes.</summary>
    public static string Display(DebloatValueKind kind, string value) => kind switch
    {
        DebloatValueKind.String or DebloatValueKind.ExpandString => value.Length == 0 ? "(empty)" : $"\"{value}\"",
        DebloatValueKind.MultiString => value.Length == 0 ? "(empty)" : "\"" + value.Replace("\n", "\", \"", StringComparison.Ordinal) + "\"",
        DebloatValueKind.Binary => "hex:" + value,
        _ => value,
    };

    /// <summary>Display form of a captured or current value for the action journal.</summary>
    public static string Describe(RegistryData? data) => data is null ? "(not set)" : Display(data.Kind, data.Value);
}
