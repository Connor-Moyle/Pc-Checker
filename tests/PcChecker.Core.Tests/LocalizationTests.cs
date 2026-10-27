using System.Globalization;
using PcChecker.Core.Localization;
using Xunit;

namespace PcChecker.Core.Tests;

public class LocalizationTests
{
    private static readonly Translator German = new("de", new Dictionary<string, string>
    {
        ["Scan"] = "Scannen",
        ["Export report"] = "Bericht exportieren",
        ["{0} items"] = "{0} Einträge",
        ["{0} broken"] = "{1} kaputt",
        ["Empty"] = "  ",
    });

    [Fact]
    public void Translates_known_text_and_keeps_unknown_text_in_English()
    {
        Assert.Equal("Scannen", German.T("Scan"));
        Assert.Equal("Fans", German.T("Fans"));
        Assert.Equal("", German.T(""));
    }

    [Fact]
    public void Blank_translations_fall_back_to_English()
    {
        Assert.Equal("Empty", German.T("Empty"));
    }

    [Theory]
    [InlineData("Export report…", "Bericht exportieren…")]
    [InlineData("Export report...", "Bericht exportieren...")]
    [InlineData("Scan:", "Scannen:")]
    public void Keeps_trailing_ellipsis_and_colon(string english, string expected)
    {
        Assert.Equal(expected, German.T(english));
    }

    [Fact]
    public void Formats_translated_patterns()
    {
        Assert.Equal("3 Einträge", German.F("{0} items", 3));
    }

    [Fact]
    public void A_broken_translated_pattern_falls_back_to_the_English_one()
    {
        Assert.Equal("2 broken", German.F("{0} broken", 2));
    }

    [Fact]
    public void English_translator_changes_nothing()
    {
        Assert.True(Translator.English.IsEnglish);
        Assert.Equal("Scan", Translator.English.T("Scan"));
        Assert.False(Translator.English.Has("Scan"));
        Assert.True(German.Has("Scan"));
    }

    [Theory]
    [InlineData("de", "en-US", "de")]
    [InlineData("pt-br", "en-US", "pt-BR")]
    [InlineData("system", "de-AT", "de")]
    [InlineData("system", "pt-PT", "pt-BR")]
    [InlineData("system", "fr-FR", "fr")]
    [InlineData("system", "ja-JP", "en")]
    [InlineData("klingon", "es-MX", "es")]
    [InlineData(null, "it-IT", "it")]
    public void Resolves_the_setting_to_a_shipped_language(string? setting, string culture, string expected)
    {
        Assert.Equal(expected, LanguageCatalog.Resolve(setting, new CultureInfo(culture)));
    }

    [Fact]
    public void Every_translated_language_has_a_catalog_entry()
    {
        Assert.Equal(["de", "es", "fr", "it", "pt-BR"], LanguageCatalog.TranslatedCodes.ToArray());
    }
}
