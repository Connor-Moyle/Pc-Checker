using System.Text;

namespace PcChecker.Core.Storage;

/// <summary>
/// Cleans drive serial numbers. ATA drives store text with the two bytes of every 16-bit word swapped; some Windows
/// versions and USB bridges hand that raw form back ("W -DCW4C1E325476" for "WD-WCC4E1234567"), and older ones
/// return it hex-encoded. Pure, so it is unit-tested.
/// </summary>
public static class DriveSerial
{
    /// <summary>
    /// Returns a trimmed, readable serial. When <paramref name="reference"/> (the same drive's serial from another
    /// source) equals the byte-swapped form, the reference is returned; without one, a serial whose swapped form loses an
    /// embedded space is swapped back.
    /// </summary>
    public static string? Clean(string? raw, string? reference = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return CleanText(reference);
        }

        string text = StripControl(raw);
        if (TryDecodeHex(text.Trim(), out var decoded))
        {
            text = decoded;
        }

        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return CleanText(reference);
        }

        string? referenceText = CleanText(reference);
        if (referenceText is not null)
        {
            string key = Normalize(referenceText);
            if (Normalize(trimmed) == key)
            {
                return trimmed;
            }

            foreach (var candidate in SwapCandidates(text))
            {
                if (Normalize(candidate) == key)
                {
                    return referenceText;
                }
            }
        }

        if (trimmed.Contains(' '))
        {
            foreach (var candidate in SwapCandidates(text))
            {
                var c = candidate.Trim();
                if (c.Length > 0 && !c.Contains(' ') && c.All(IsSerialChar))
                {
                    return c;
                }
            }
        }

        return trimmed;
    }

    /// <summary>Swaps the two characters of every pair ("BADC" → "ABCD"). An odd last character stays put.</summary>
    public static string SwapPairs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chars = text.ToCharArray();
        for (int i = 0; i + 1 < chars.Length; i += 2)
        {
            (chars[i], chars[i + 1]) = (chars[i + 1], chars[i]);
        }

        return new string(chars);
    }

    /// <summary>Decodes an ATA IDENTIFY string field (words, each stored high byte first) into trimmed text.</summary>
    public static string AtaString(ReadOnlySpan<byte> identify, int firstWord, int wordCount)
    {
        int start = firstWord * 2;
        int length = wordCount * 2;
        if (identify.Length < start + length)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i += 2)
        {
            sb.Append(ToPrintable(identify[start + i + 1]));
            sb.Append(ToPrintable(identify[start + i]));
        }

        return sb.ToString().Trim();
    }

    /// <summary>Upper-case letters and digits only, for comparing serials from different sources.</summary>
    public static string Normalize(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(serial.Length);
        foreach (char c in serial)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// True for serials that do not identify a drive: empty, all one character ("000000000000") or the generic
    /// strings some USB bridges report for every drive.
    /// </summary>
    public static bool IsPlaceholder(string? serial)
    {
        var n = Normalize(serial);
        if (n.Length < 4)
        {
            return true;
        }

        if (n.All(c => c == n[0]))
        {
            return true;
        }

        return n is "123456789ABC" or "0123456789ABCDEF" or "1234567890" or "NONE" or "DEFAULTSTRING" or "TOBEFILLEDBYOEM";
    }

    /// <summary>True when two serials name the same drive, also when one source reported the byte-swapped form.</summary>
    public static bool SameDrive(string? a, string? b)
    {
        if (IsPlaceholder(a) || IsPlaceholder(b))
        {
            return false;
        }

        string na = Normalize(a), nb = Normalize(b);
        if (na == nb)
        {
            return true;
        }

        return Normalize(Clean(a, b)) == nb || Normalize(Clean(b, a)) == na;
    }

    private static IEnumerable<string> SwapCandidates(string text)
    {
        // WMI may already have trimmed the padding, which shifts the pairs by one; try both alignments.
        yield return SwapPairs(text.Length % 2 == 0 ? text : text + " ");
        yield return SwapPairs(" " + (text.Length % 2 == 0 ? text + " " : text));
        string t = text.Trim();
        yield return SwapPairs(t.Length % 2 == 0 ? t : t + " ");
        yield return SwapPairs(t.Length % 2 == 0 ? " " + t + " " : " " + t);
    }

    private static bool TryDecodeHex(string text, out string decoded)
    {
        decoded = string.Empty;
        if (text.Length < 16 || text.Length % 2 != 0 || !text.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        // A long hex string could also be a genuine serial (some NVMe drives report EUI-64 style ids). Only decode when
        // every byte is printable text.
        var sb = new StringBuilder(text.Length / 2);
        for (int i = 0; i < text.Length; i += 2)
        {
            int value = Convert.ToInt32(text.Substring(i, 2), 16);
            if (value is < 0x20 or > 0x7E)
            {
                return false;
            }

            sb.Append((char)value);
        }

        // Hex-encoded serials come from the raw ATA field, which is byte-swapped.
        decoded = SwapPairs(sb.ToString());
        return decoded.Trim().Length > 0;
    }

    private static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = StripControl(text).Trim();
        return t.Length == 0 ? null : t;
    }

    private static string StripControl(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(char.IsControl(c) || c == '\0' ? ' ' : c);
        }

        return sb.ToString();
    }

    private static bool IsSerialChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/';

    private static char ToPrintable(byte b) => b is >= 0x20 and <= 0x7E ? (char)b : ' ';
}
