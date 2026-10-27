using System.Globalization;

namespace PcChecker.Core.Localization;

/// <summary>
/// Translates English interface text through a dictionary keyed by the English text itself. Anything without a
/// translation stays in English, so a partly translated language never shows blanks or keys.
/// </summary>
public sealed class Translator
{
    private readonly IReadOnlyDictionary<string, string> _strings;

    public Translator(string languageCode, IReadOnlyDictionary<string, string>? strings)
    {
        LanguageCode = languageCode;
        _strings = strings ?? new Dictionary<string, string>();
    }

    /// <summary>English, with no translations.</summary>
    public static Translator English { get; } = new("en", null);

    public string LanguageCode { get; }

    public bool IsEnglish => _strings.Count == 0;

    public int Count => _strings.Count;

    /// <summary>The translation of <paramref name="english"/>, or the English text when there is none.</summary>
    public string T(string english)
    {
        if (string.IsNullOrEmpty(english) || _strings.Count == 0)
        {
            return english;
        }

        if (_strings.TryGetValue(english, out var translated) && !string.IsNullOrWhiteSpace(translated))
        {
            return translated;
        }

        // Interface text often carries a trailing ellipsis or colon that the dictionary entry leaves out.
        foreach (var suffix in Suffixes)
        {
            if (english.Length > suffix.Length && english.EndsWith(suffix, StringComparison.Ordinal) &&
                _strings.TryGetValue(english[..^suffix.Length], out var stem) && !string.IsNullOrWhiteSpace(stem))
            {
                return stem + suffix;
            }
        }

        return english;
    }

    /// <summary>True when <paramref name="english"/> has a translation (used to skip text that needs no work).</summary>
    public bool Has(string english) => _strings.Count > 0 && T(english) != english;

    /// <summary>Translates a composite format string ("{0} items") and fills it in with the current culture.</summary>
    public string F(string englishFormat, params object?[] args)
    {
        var format = T(englishFormat);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            // A translation with broken placeholders must never crash the page: fall back to the English format.
            return string.Format(CultureInfo.CurrentCulture, englishFormat, args);
        }
    }

    private static readonly string[] Suffixes = ["…", "...", ":"];
}
