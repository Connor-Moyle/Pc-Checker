using System.IO;
using System.Reflection;
using System.Text.Json;
using PcChecker.Core.Localization;

namespace PcChecker.App.Localization;

/// <summary>
/// The interface language for this run. Chosen once at startup from Settings (or the Windows display language);
/// changing it takes effect on the next start. Translations ship inside the exe as <c>lang/&lt;code&gt;.json</c>
/// (English text to translated text).
/// </summary>
public static class Loc
{
    public static Translator Current { get; private set; } = Translator.English;

    /// <summary>Translates fixed English text; returns it unchanged in English or when no translation exists.</summary>
    public static string T(string english) => Current.T(english);

    /// <summary>Translates an English format string ("{0} items") and fills it in.</summary>
    public static string F(string englishFormat, params object?[] args) => Current.F(englishFormat, args);

    /// <summary>Loads <paramref name="languageCode"/>; falls back to English when the file is missing or unreadable.</summary>
    public static void Load(string languageCode)
    {
        Current = languageCode == "en" ? Translator.English : new Translator(languageCode, ReadEmbedded(languageCode));
    }

    /// <summary>The English-to-translation map embedded for <paramref name="languageCode"/>, or null.</summary>
    public static IReadOnlyDictionary<string, string>? ReadEmbedded(string languageCode)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"lang/{languageCode}.json");
            if (stream is null)
            {
                return null;
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
