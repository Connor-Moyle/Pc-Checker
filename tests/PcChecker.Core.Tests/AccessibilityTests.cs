using System.Text.RegularExpressions;
using PcChecker.Core.Accessibility;
using PcChecker.Core.Fans;
using PcChecker.Core.Localization;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Contrast maths, the theme palette, screen-reader names and keyboard helpers (docs/ACCESSIBILITY.md).</summary>
public class AccessibilityTests
{
    public static IEnumerable<object[]> RolesAndVariants() =>
        from role in AccentPalette.Roles
        from variant in Enum.GetValues<PaletteVariant>()
        select new object[] { role, variant };

    [Fact]
    public void Contrast_runs_from_one_to_twenty_one()
    {
        var black = ThemeColor.Parse("#000000");
        var white = ThemeColor.Parse("#FFFFFF");

        Assert.Equal(21, ContrastMath.Ratio(black, white), 2);
        Assert.Equal(21, ContrastMath.Ratio(white, black), 2);
        Assert.Equal(1, ContrastMath.Ratio(white, white), 5);
        Assert.Equal(0, ContrastMath.RelativeLuminance(black), 5);
        Assert.Equal(1, ContrastMath.RelativeLuminance(white), 5);
    }

    [Theory]
    [InlineData("#767676", "#FFFFFF", 4.54, true)]
    [InlineData("#777777", "#FFFFFF", 4.48, false)]
    [InlineData("#0078D4", "#FFFFFF", 4.53, true)]
    [InlineData("#4CC2FF", "#F3F3F3", 1.81, false)]
    public void Matches_known_WCAG_ratios(string foreground, string background, double expected, bool meetsText)
    {
        var fg = ThemeColor.Parse(foreground);
        var bg = ThemeColor.Parse(background);

        Assert.Equal(expected, ContrastMath.Ratio(fg, bg), 2);
        Assert.Equal(meetsText, ContrastMath.MeetsText(fg, bg));
    }

    [Fact]
    public void See_through_text_is_blended_over_the_background_first()
    {
        // 62 % black over white is a mid grey; the theme's tertiary text (45 %) is too faint for small print.
        var page = ThemeColor.Parse("#F3F3F3");
        Assert.True(ContrastMath.MeetsText(ThemeColor.Parse("#9E000000"), page));
        Assert.False(ContrastMath.MeetsText(ThemeColor.Parse("#72000000"), page));
        Assert.Equal(ThemeColor.Rgb(0x7F, 0x7F, 0x7F), ThemeColor.Parse("#80000000").Over(ThemeColor.Parse("#FFFFFF")));
        Assert.Throws<ArgumentException>(() => ContrastMath.Ratio(ThemeColor.Parse("#000000"), ThemeColor.Parse("#80FFFFFF")));
    }

    [Theory]
    [InlineData("#4CC2FF", 255, 0x4C, 0xC2, 0xFF, "#4CC2FF")]
    [InlineData("87FFFFFF", 0x87, 0xFF, 0xFF, 0xFF, "#87FFFFFF")]
    [InlineData("#fff", 255, 0xFF, 0xFF, 0xFF, "#FFFFFF")]
    public void Parses_xaml_colours(string text, int a, int r, int g, int b, string hex)
    {
        Assert.True(ThemeColor.TryParse(text, out var color));
        Assert.Equal(new ThemeColor((byte)a, (byte)r, (byte)g, (byte)b), color);
        Assert.Equal(hex, color.ToHex());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GG0000")]
    [InlineData("blue")]
    public void Rejects_what_is_not_a_colour(string? text)
    {
        Assert.False(ThemeColor.TryParse(text, out _));
        if (text is not null)
        {
            Assert.Throws<FormatException>(() => ThemeColor.Parse(text));
        }
    }

    [Theory]
    [MemberData(nameof(RolesAndVariants))]
    public void Every_accent_is_readable_as_text_on_every_surface_of_its_theme(AccentRole role, PaletteVariant variant)
    {
        var accent = AccentPalette.Accent(role, variant);
        foreach (var surface in AccentPalette.Surfaces(variant))
        {
            double ratio = ContrastMath.Ratio(accent, surface);
            Assert.True(ratio >= ContrastMath.TextMinimum, $"{role} {accent} on {surface} ({variant}) is {ratio:0.00}:1");
        }
    }

    [Theory]
    [MemberData(nameof(RolesAndVariants))]
    public void Badge_text_is_readable_on_every_accent(AccentRole role, PaletteVariant variant)
    {
        double ratio = ContrastMath.Ratio(AccentPalette.OnAccentText(variant), AccentPalette.Accent(role, variant));
        Assert.True(ratio >= ContrastMath.TextMinimum, $"badge text on {role} ({variant}) is {ratio:0.00}:1");
    }

    [Theory]
    [InlineData(PaletteVariant.Dark)]
    [InlineData(PaletteVariant.Light)]
    public void Caption_text_is_readable_on_every_surface(PaletteVariant variant)
    {
        foreach (var surface in AccentPalette.Surfaces(variant))
        {
            Assert.True(ContrastMath.MeetsText(AccentPalette.CaptionText(variant), surface), $"Caption on {surface} ({variant})");
        }
    }

    [Fact]
    public void The_light_theme_needs_its_own_shades()
    {
        // The bright dark-theme colours fade into a light page, which is why the light palette exists.
        var lightPage = ThemeColor.Parse("#F3F3F3");
        foreach (var role in AccentPalette.Roles)
        {
            Assert.NotEqual(AccentPalette.Accent(role, PaletteVariant.Dark), AccentPalette.Accent(role, PaletteVariant.Light));
            Assert.False(ContrastMath.MeetsText(AccentPalette.Accent(role, PaletteVariant.Dark), lightPage), role.ToString());
        }
    }

    [Fact]
    public void Styles_xaml_declares_every_palette_brush_with_the_dark_values()
    {
        var styles = File.ReadAllText(Path.Combine(XamlConventionsTests.AppDirectory(), "Resources", "Styles.xaml"));
        foreach (var role in AccentPalette.Roles)
        {
            var match = Regex.Match(styles, $"<SolidColorBrush x:Key=\"{AccentPalette.ResourceKey(role)}\" Color=\"(#[0-9A-Fa-f]+)\"");
            Assert.True(match.Success, AccentPalette.ResourceKey(role));
            Assert.Equal(AccentPalette.Accent(role, PaletteVariant.Dark), ThemeColor.Parse(match.Groups[1].Value));
        }

        Assert.Contains($"x:Key=\"{AccentPalette.OnAccentTextKey}\"", styles);
        Assert.Contains($"x:Key=\"{AccentPalette.CaptionTextKey}\"", styles);
    }

    [Theory]
    [InlineData("_Scan", "Scan")]
    [InlineData("  Export   report: ", "Export report")]
    [InlineData("Search", "Search")]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    [InlineData("—", null)]
    [InlineData("VEN__10DE", "VEN_10DE")]
    public void Turns_interface_text_into_screen_reader_names(string? text, string? expected)
    {
        Assert.Equal(expected, AccessibleText.ToName(text));
    }

    [Fact]
    public void Long_text_is_cut_at_a_word()
    {
        var name = AccessibleText.ToName(string.Join(' ', Enumerable.Repeat("temperature", 20)));
        Assert.NotNull(name);
        Assert.True(name!.Length <= AccessibleText.MaxNameLength + 1);
        Assert.EndsWith("temperature…", name);
    }

    [Theory]
    [InlineData("_Scan", true, "Scan", 'S')]
    [InlineData("Check for _updates", true, "Check for updates", 'u')]
    [InlineData("Save __as", false, "Save __as", '\0')]
    [InlineData("A__B _Copy", true, "A_B Copy", 'C')]
    [InlineData("Scan", false, "Scan", '\0')]
    [InlineData("trailing_", false, "trailing_", '\0')]
    public void Splits_access_keys_like_WPF(string text, bool found, string plain, char key)
    {
        Assert.Equal(found, AccessibleText.TrySplitAccessKey(text, out var actualPlain, out var actualKey));
        Assert.Equal(plain, actualPlain);
        Assert.Equal(key, actualKey);
    }

    [Theory]
    [InlineData("Scannen", 'S', "_Scannen")]
    [InlineData("Analysieren", 'y', "Anal_ysieren")]
    [InlineData("Analyser", 'A', "_Analyser")]
    [InlineData("Actualizar", 'R', "Actualiza_r")]
    [InlineData("Exporter", 'q', "_Exporter")]
    [InlineData("…", 'S', "…")]
    [InlineData("Datei_A", 'x', "_Datei__A")]
    public void Places_the_access_key_in_translated_text(string translated, char key, string expected)
    {
        Assert.Equal(expected, AccessibleText.ApplyAccessKey(translated, key));
    }

    [Fact]
    public void Translates_labels_that_carry_an_access_key()
    {
        var german = new Translator("de", new Dictionary<string, string>
        {
            ["Scan"] = "Scannen",
            ["Check for updates"] = "Nach Updates suchen",
        });

        Assert.Equal("_Scannen", german.T("_Scan"));
        Assert.Equal("Nach _Updates suchen", german.T("Check for _updates"));
        Assert.Equal("_Refresh", german.T("_Refresh"));
        Assert.True(german.Has("_Scan"));
        Assert.Equal("_Scan", Translator.English.T("_Scan"));
    }

    [Theory]
    [InlineData(0, 1, 3, 1)]
    [InlineData(2, 1, 3, 0)]
    [InlineData(0, -1, 3, 2)]
    [InlineData(-1, 1, 3, 0)]
    [InlineData(-1, -1, 3, 2)]
    [InlineData(1, 5, 3, 0)]
    [InlineData(0, 1, 0, -1)]
    public void Cycles_through_tabs_and_regions(int current, int offset, int count, int expected)
    {
        Assert.Equal(expected, AccessibleText.Cycle(current, offset, count));
    }

    [Fact]
    public void Describes_access_keys_the_way_screen_readers_say_them()
    {
        Assert.Equal("Alt+S", AccessibleText.DescribeAccessKey('s'));
    }

    [Fact]
    public void Keyboard_moves_keep_a_fan_curve_point_inside_its_neighbours()
    {
        List<FanCurvePoint> points = [new(30, 30), new(50, 50), new(70, 100)];

        Assert.Equal(new FanCurvePoint(51, 55), FanCurveMath.MoveWithinNeighbours(points, 1, 51, 55));
        Assert.Equal(new FanCurvePoint(69, 50), FanCurveMath.MoveWithinNeighbours(points, 1, 90, 50));
        Assert.Equal(new FanCurvePoint(31, 0), FanCurveMath.MoveWithinNeighbours(points, 1, 10, -20));
        Assert.Equal(new FanCurvePoint(30, 100), FanCurveMath.MoveWithinNeighbours(points, 0, 30, 140));
        Assert.Equal(new FanCurvePoint(100, 100), FanCurveMath.MoveWithinNeighbours(points, 2, 130, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => FanCurveMath.MoveWithinNeighbours(points, 3, 50, 50));
    }

    [Fact]
    public void A_point_with_no_room_beside_it_stays_where_it_is()
    {
        List<FanCurvePoint> points = [new(40, 30), new(41, 40), new(42, 50)];
        Assert.Equal(new FanCurvePoint(41, 45), FanCurveMath.MoveWithinNeighbours(points, 1, 45, 45));
    }

    [Fact]
    public void Insert_adds_a_point_halfway_along_the_curve()
    {
        List<FanCurvePoint> points = [new(70, 100), new(30, 30), new(50, 50)];

        Assert.Equal(new FanCurvePoint(40, 40), FanCurveMath.PointBetween(points, 1));
        Assert.Equal(new FanCurvePoint(60, 75), FanCurveMath.PointBetween(points, 2));
        // The hottest point has nothing after it: the new one goes before it.
        Assert.Equal(new FanCurvePoint(60, 75), FanCurveMath.PointBetween(points, 0));
        Assert.Null(FanCurveMath.PointBetween([new(40, 30), new(41, 40)], 0));
        Assert.Null(FanCurveMath.PointBetween(points, 5));
    }
}
