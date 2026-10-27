using System.Text.RegularExpressions;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>
/// Guards against WPF-UI pitfalls that compile fine but break the UI at runtime, and keeps the navigation tidy
/// (see docs/UI-GUIDELINES.md).
/// </summary>
public class XamlConventionsTests
{
    // Styles on these controls replace WPF-UI's implicit style (and its template) unless they are BasedOn it.
    private static readonly string[] TemplatedControls = ["ui:CardControl", "ui:CardExpander", "ui:NumberBox", "ui:DataGrid", "ui:Button"];

    // WPF-UI 3.0.5 truncates symbol code points above 0xFFFF and draws the wrong glyph for these names.
    private static readonly string[] BrokenSymbols = ["HardDrive24", "DesktopTower24", "DocumentArrowDown24"];

    [Fact]
    public void Styles_on_templated_wpfui_controls_are_based_on_the_default_style()
    {
        var offenders = new List<string>();
        foreach (var file in XamlFiles())
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "<Style\\s[^>]*TargetType=\"(ui:[A-Za-z]+)\"[^>]*>"))
            {
                if (TemplatedControls.Contains(m.Groups[1].Value) && !m.Value.Contains("BasedOn"))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Known_broken_symbol_names_are_not_used()
    {
        var offenders = new List<string>();
        foreach (var file in XamlFiles().Concat(SourceFiles()))
        {
            var text = File.ReadAllText(file);
            offenders.AddRange(BrokenSymbols.Where(s => Regex.IsMatch(text, $"\\b{s}\\b")).Select(s => $"{Path.GetFileName(file)}: {s}"));
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_page_is_listed_in_the_navigation_catalog()
    {
        var catalog = File.ReadAllText(Path.Combine(AppDirectory(), "Services", "NavigationCatalog.cs"));
        var pages = Directory.EnumerateFiles(Path.Combine(AppDirectory(), "Views", "Pages"), "*Page.xaml")
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();

        Assert.NotEmpty(pages);
        Assert.Empty(pages.Where(p => !catalog.Contains($"typeof({p})")));
    }

    [Fact]
    public void The_side_menu_stays_short()
    {
        var catalog = File.ReadAllText(Path.Combine(AppDirectory(), "Services", "NavigationCatalog.cs"));
        int sections = Regex.Matches(catalog, @"\b(Section|Footer)\(""").Count;
        Assert.InRange(sections, 1, 14);
    }

    private static string AppDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcChecker.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "PcChecker.App");
    }

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(AppDirectory(), "*.xaml", SearchOption.AllDirectories).Where(NotBuildOutput);

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(AppDirectory(), "*.cs", SearchOption.AllDirectories).Where(NotBuildOutput);

    private static bool NotBuildOutput(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");
}
