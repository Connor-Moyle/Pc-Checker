using System.Text.RegularExpressions;
using System.Xml.Linq;
using PcChecker.Core.Accessibility;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>
/// Scans every XAML file for the accessibility rules in docs/ACCESSIBILITY.md: things a screen reader cannot name,
/// icons it would read as junk, colours that ignore the theme and hidden keyboard focus.
/// </summary>
public class XamlAccessibilityTests
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string WpfUi = "http://schemas.lepo.co/wpfui/2022/xaml";

    private static readonly XName[] ButtonTypes =
    [
        XName.Get("Button", Presentation), XName.Get("ToggleButton", Presentation), XName.Get("Button", WpfUi),
        XName.Get("DropDownButton", WpfUi), XName.Get("SplitButton", WpfUi), XName.Get("ToggleButton", WpfUi),
    ];

    private static readonly XName[] ImageTypes = [XName.Get("Image", Presentation), XName.Get("Image", WpfUi)];

    private static readonly XName[] ListTypes =
    [
        XName.Get("ListBox", Presentation), XName.Get("ListView", Presentation), XName.Get("DataGrid", Presentation),
        XName.Get("ListView", WpfUi), XName.Get("DataGrid", WpfUi),
    ];

    private static readonly string[] IconElementNames = ["SymbolIcon", "IconGlyph", "ImageIcon", "FontIcon", "Image"];

    // Text drawn on the window's own dark panel, whatever the theme.
    private static readonly string[] FixedColourFiles = ["IdentifyWindow.xaml"];

    [Fact]
    public void Icon_only_buttons_have_a_screen_reader_name()
    {
        // Guards the scan itself: the app has plenty of icon-only buttons, all named.
        Assert.True(Elements(ButtonTypes).Count(x => IsIconOnly(x.Element)) > 10);

        var offenders = Elements(ButtonTypes)
            .Where(x => IsIconOnly(x.Element) && !HasName(x.Element))
            .Select(Describe)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Images_have_a_screen_reader_name()
    {
        Assert.Empty(Elements(ImageTypes).Where(x => !HasName(x.Element)).Select(Describe));
    }

    [Fact]
    public void Lists_and_grids_have_a_screen_reader_name()
    {
        Assert.True(Elements(ListTypes).Count() > 20);
        Assert.Empty(Elements(ListTypes).Where(x => !HasName(x.Element)).Select(Describe));
    }

    [Fact]
    public void Standalone_icons_use_IconGlyph_so_screen_readers_skip_or_name_them()
    {
        // ui:SymbolIcon exposes its font glyph as text; the {ui:SymbolIcon} markup extension on a button is fine.
        Assert.Empty(Elements([XName.Get("SymbolIcon", WpfUi)]).Select(Describe));
    }

    [Fact]
    public void Theme_colour_brushes_are_dynamic_resources()
    {
        var keys = AccentPalette.Roles.Select(AccentPalette.ResourceKey)
            .Concat([AccentPalette.OnAccentTextKey, AccentPalette.CaptionTextKey, "OnAccentIconBrush"])
            .ToList();
        var offenders = new List<string>();
        foreach (var file in XamlConventionsTests.XamlFiles())
        {
            var text = File.ReadAllText(file);
            offenders.AddRange(keys.Where(k => text.Contains($"{{StaticResource {k}}}", StringComparison.Ordinal))
                .Select(k => $"{Path.GetFileName(file)}: StaticResource {k}"));
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Keyboard_focus_is_never_hidden()
    {
        var offenders = new List<string>();
        foreach (var file in XamlConventionsTests.XamlFiles())
        {
            var text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"FocusVisualStyle=""\{x:Null\}""|Property=""FocusVisualStyle""\s+Value=""\{x:Null\}"""))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Text_colours_come_from_the_theme()
    {
        // Hard-coded white or near-black text disappears on one theme or the other; badges use OnAccentTextBrush.
        var fixedColour = new Regex(@"^(White|Black|#[0-9A-Fa-f]{6,8})$");
        var textTypes = new[] { XName.Get("TextBlock", Presentation), XName.Get("TextBlock", WpfUi), XName.Get("AccessText", Presentation) };
        var offenders = Elements(textTypes)
            .Where(x => !FixedColourFiles.Contains(Path.GetFileName(x.File)))
            .Where(x => x.Element.Attribute("Foreground") is { } fg && fixedColour.IsMatch(fg.Value))
            .Select(Describe)
            .ToList();

        foreach (var file in XamlConventionsTests.XamlFiles().Where(f => !FixedColourFiles.Contains(Path.GetFileName(f))))
        {
            foreach (var style in Load(file).Descendants(XName.Get("Style", Presentation)))
            {
                bool forText = style.Attribute("TargetType")?.Value is "TextBlock" or "ui:TextBlock";
                var setter = style.Elements(XName.Get("Setter", Presentation))
                    .FirstOrDefault(s => s.Attribute("Property")?.Value == "Foreground" && fixedColour.IsMatch(s.Attribute("Value")?.Value ?? string.Empty));
                if (forText && setter is not null)
                {
                    offenders.Add($"{Path.GetFileName(file)}: style {style.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Access_keys_are_unique_on_each_page()
    {
        var offenders = new List<string>();
        foreach (var file in XamlConventionsTests.XamlFiles())
        {
            var keys = Load(file).Descendants(XName.Get("AccessText", Presentation))
                .Select(a => AccessibleText.TrySplitAccessKey(a.Attribute("Text")?.Value ?? string.Empty, out _, out var key) ? char.ToUpperInvariant(key) : '\0')
                .ToList();
            Assert.DoesNotContain('\0', keys);
            offenders.AddRange(keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => $"{Path.GetFileName(file)}: Alt+{g.Key} twice"));
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_scan_finds_what_it_is_meant_to_find()
    {
        var sample = XElement.Parse($$"""
            <Grid xmlns="{{Presentation}}" xmlns:ui="{{WpfUi}}">
              <ui:Button Icon="{ui:SymbolIcon Delete24}" ToolTip="Delete" />
              <ui:Button Icon="{ui:SymbolIcon Delete24}" AutomationProperties.Name="Delete" />
              <ui:Button Icon="{ui:SymbolIcon Save24}" Content="Save" />
              <ui:Button><ui:Button.Icon><ui:SymbolIcon Symbol="Add24" /></ui:Button.Icon></ui:Button>
              <ui:Button><ui:SymbolIcon Symbol="Add24" /></ui:Button>
              <ui:Button><AccessText Text="_Scan" /></ui:Button>
              <Button>Text</Button>
            </Grid>
            """);
        var buttons = sample.Elements().ToList();

        Assert.Equal([true, true, false, true, true, false, false], buttons.Select(IsIconOnly).ToArray());
        Assert.Equal([false, true, false, false, false, false, false], buttons.Select(HasName).ToArray());
    }

    private static bool IsIconOnly(XElement button)
    {
        if (button.Attribute("Content") is not null || button.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
        {
            return false;
        }

        var content = button.Elements().Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal)).ToList();
        bool hasIcon = button.Attribute("Icon") is not null || button.Elements().Any(e => e.Name.LocalName.EndsWith(".Icon", StringComparison.Ordinal));
        return content.Count == 0 ? hasIcon : content.All(e => IconElementNames.Contains(e.Name.LocalName));
    }

    private static bool HasName(XElement element) =>
        element.Attribute("AutomationProperties.Name") is { Value.Length: > 0 } || element.Attribute("AutomationProperties.LabeledBy") is not null;

    private static IEnumerable<(string File, XElement Element)> Elements(IReadOnlyCollection<XName> names) =>
        XamlConventionsTests.XamlFiles().SelectMany(file => Load(file).Descendants().Where(e => names.Contains(e.Name)).Select(e => (file, e)));

    private static XDocument Load(string file) => XDocument.Load(file, LoadOptions.SetLineInfo);

    private static string Describe((string File, XElement Element) item) =>
        $"{Path.GetFileName(item.File)}:{((System.Xml.IXmlLineInfo)item.Element).LineNumber} <{item.Element.Name.LocalName}>";
}
