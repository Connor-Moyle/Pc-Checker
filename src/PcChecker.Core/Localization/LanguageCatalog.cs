using System.Globalization;

namespace PcChecker.Core.Localization;

/// <summary>One interface language: its code, the name in that language and the name in English.</summary>
public sealed record AppLanguage(string Code, string NativeName, string EnglishName)
{
    public string Display => Code == LanguageCatalog.System ? NativeName : $"{NativeName} ({EnglishName})";
}

/// <summary>The interface languages PC Checker ships, and how the "System" choice resolves.</summary>
public static class LanguageCatalog
{
    /// <summary>The setting value meaning "follow the Windows display language".</summary>
    public const string System = "system";

    public static IReadOnlyList<AppLanguage> All { get; } =
    [
        new(System, "Windows language", "Windows language"),
        new("en", "English", "English"),
        new("de", "Deutsch", "German"),
        new("es", "Español", "Spanish"),
        new("fr", "Français", "French"),
        new("it", "Italiano", "Italian"),
        new("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
    ];

    /// <summary>The codes that have a translation file (everything except System and English).</summary>
    public static IEnumerable<string> TranslatedCodes => All.Select(l => l.Code).Where(c => c is not System and not "en");

    /// <summary>
    /// Turns the saved setting into a shipped language code. "System" (or an unknown value) follows
    /// <paramref name="uiCulture"/>: an exact match first (pt-BR), then the neutral language (de-AT becomes de,
    /// pt-PT becomes pt-BR), otherwise English.
    /// </summary>
    public static string Resolve(string? setting, CultureInfo uiCulture)
    {
        if (!string.IsNullOrWhiteSpace(setting) && setting != System)
        {
            var exact = All.FirstOrDefault(l => string.Equals(l.Code, setting, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact.Code;
            }
        }

        var name = uiCulture.Name;
        var match = All.FirstOrDefault(l => l.Code != System && string.Equals(l.Code, name, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(l => l.Code != System &&
                string.Equals(l.Code.Split('-')[0], uiCulture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? "en";
    }
}
