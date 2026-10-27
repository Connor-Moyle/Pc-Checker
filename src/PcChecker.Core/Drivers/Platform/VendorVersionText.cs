using System.Text;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>
/// Compares the version numbers makers print on their own packages and firmware, which the four-part Windows
/// comparison gets wrong: AMD chipset software (6.10.17.152 &lt; 7.01.08.129, leading zeros ignored), MSI BIOS
/// (1.90 &lt; 1.A0: MSI counts on from 9 with letters), GIGABYTE BIOS (F23 &lt; F23b &lt; F23c, F9 &lt; F10), ASUS
/// (2801), Lenovo and Dell (1.40, 1.21.0). Two values with a different letter prefix (F23c against 2.A0) cannot be
/// compared and give null. Pure.
/// </summary>
public static class VendorVersionText
{
    /// <summary>
    /// Positive when <paramref name="a"/> is newer, zero when equal, negative when older; null when either value is
    /// missing or the two are written in different ways.
    /// </summary>
    public static int? Compare(string? a, string? b)
    {
        var ta = Tokenize(a);
        var tb = Tokenize(b);
        if (ta.Count == 0 || tb.Count == 0)
        {
            return null;
        }

        // A leading word (the "F" of F23c, the "v" of v1.2) must match, otherwise the numbers mean different things.
        bool alphaA = !ta[0].IsNumber;
        bool alphaB = !tb[0].IsNumber;
        if (alphaA != alphaB || (alphaA && !string.Equals(ta[0].Text, tb[0].Text, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        int n = Math.Max(ta.Count, tb.Count);
        for (int i = 0; i < n; i++)
        {
            if (i >= ta.Count)
            {
                return TrailingIsZero(tb, i) ? 0 : -1;
            }

            if (i >= tb.Count)
            {
                return TrailingIsZero(ta, i) ? 0 : 1;
            }

            var (x, y) = (ta[i], tb[i]);
            int c;
            if (x.IsNumber && y.IsNumber)
            {
                c = CompareNumbers(x.Text, y.Text);
            }
            else if (!x.IsNumber && !y.IsNumber)
            {
                c = string.Compare(x.Text, y.Text, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // A letter where the other has a digit: makers count on from 9 with letters (MSI 1.90 → 1.A0).
                c = x.IsNumber ? -1 : 1;
            }

            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return 0;
    }

    /// <summary>True when <paramref name="candidate"/> is known to be newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    /// <summary>
    /// The parts of a version: runs of digits and runs of letters. Dots, dashes, spaces and brackets separate parts;
    /// in a part such as "A0" the letter and the digit become two parts, so "1.A0" reads as 1, A, 0.
    /// </summary>
    public static IReadOnlyList<(string Text, bool IsNumber)> Parts(string? value) => Tokenize(value).Select(t => (t.Text, t.IsNumber)).ToList();

    private static List<Token> Tokenize(string? value)
    {
        var tokens = new List<Token>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return tokens;
        }

        var current = new StringBuilder();
        bool? numeric = null;
        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(new Token(current.ToString(), numeric == true));
                current.Clear();
            }

            numeric = null;
        }

        foreach (char ch in value.Trim())
        {
            if (char.IsAsciiDigit(ch))
            {
                if (numeric == false)
                {
                    Flush();
                }

                numeric = true;
                current.Append(ch);
            }
            else if (char.IsAsciiLetter(ch))
            {
                if (numeric == true)
                {
                    Flush();
                }

                numeric = false;
                current.Append(ch);
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return tokens;
    }

    private static bool TrailingIsZero(List<Token> tokens, int from) =>
        tokens.Skip(from).All(t => t.IsNumber && t.Text.All(c => c == '0'));

    private static int CompareNumbers(string a, string b)
    {
        var x = a.TrimStart('0');
        var y = b.TrimStart('0');
        if (x.Length != y.Length)
        {
            return x.Length.CompareTo(y.Length);
        }

        return string.CompareOrdinal(x, y);
    }

    private readonly record struct Token(string Text, bool IsNumber);
}
