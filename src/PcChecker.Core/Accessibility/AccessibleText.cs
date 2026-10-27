using System.Text;

namespace PcChecker.Core.Accessibility;

/// <summary>
/// Turns interface text into names that screen readers announce, and handles WPF access keys ("_Scan" shows "Scan"
/// with S as the key; "__" is a literal underscore).
/// </summary>
public static class AccessibleText
{
    /// <summary>Longest name handed to a screen reader; longer text belongs in the help text.</summary>
    public const int MaxNameLength = 80;

    /// <summary>
    /// A screen-reader name for <paramref name="text"/>: the access key marker and a trailing colon removed,
    /// whitespace collapsed, icon-font glyphs (private use characters) dropped, and long text cut at a word
    /// boundary. Null when nothing readable is left.
    /// </summary>
    public static string? ToName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in StripAccessKey(text))
        {
            if (IsPrivateUse(c) || (char.IsControl(c) && !char.IsWhiteSpace(c)))
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        var name = builder.ToString().TrimEnd(':', ' ');
        if (!name.Any(char.IsLetterOrDigit))
        {
            return null;
        }

        if (name.Length <= MaxNameLength)
        {
            return name;
        }

        int cut = name.LastIndexOf(' ', MaxNameLength);
        return (cut > MaxNameLength / 2 ? name[..cut] : name[..MaxNameLength]).TrimEnd(',', ';', '.', ' ') + "…";
    }

    /// <summary>The text as shown on screen: the access key marker removed and "__" turned into "_".</summary>
    public static string StripAccessKey(string text) =>
        TrySplitAccessKey(text, out var plain, out _) ? plain : text.Replace("__", "_", StringComparison.Ordinal);

    /// <summary>
    /// Splits "_Scan" into "Scan" and 'S'. Only the first single underscore followed by a letter or digit marks the
    /// key, as in WPF; false when there is none.
    /// </summary>
    public static bool TrySplitAccessKey(string text, out string plain, out char key)
    {
        plain = text;
        key = '\0';
        if (string.IsNullOrEmpty(text) || !text.Contains('_', StringComparison.Ordinal))
        {
            return false;
        }

        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '_')
            {
                builder.Append(c);
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '_')
            {
                builder.Append('_');
                i++;
                continue;
            }

            if (key == '\0' && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]))
            {
                key = text[i + 1];
                continue;
            }

            builder.Append(c);
        }

        if (key == '\0')
        {
            return false;
        }

        plain = builder.ToString();
        return true;
    }

    /// <summary>
    /// Marks an access key in translated text: before the same letter when the translation has it, otherwise before
    /// the first letter, so a translated button still has a key.
    /// </summary>
    public static string ApplyAccessKey(string text, char key)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var escaped = text.Replace("_", "__", StringComparison.Ordinal);
        int at = escaped.IndexOf(key.ToString(), StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            at = 0;
            while (at < escaped.Length && !char.IsLetterOrDigit(escaped[at]))
            {
                at++;
            }

            if (at == escaped.Length)
            {
                return escaped;
            }
        }

        return escaped.Insert(at, "_");
    }

    /// <summary>"Alt+S" for the key S, as screen readers announce access keys.</summary>
    public static string DescribeAccessKey(char key) => "Alt+" + char.ToUpperInvariant(key);

    /// <summary>Moves <paramref name="offset"/> steps through <paramref name="count"/> items, wrapping at both ends.</summary>
    public static int Cycle(int current, int offset, int count)
    {
        if (count <= 0)
        {
            return -1;
        }

        int start = current < 0 ? (offset >= 0 ? -1 : 0) : current;
        return ((start + offset) % count + count) % count;
    }

    private static bool IsPrivateUse(char c) => c is >= '' and <= '' || char.IsSurrogate(c);
}
