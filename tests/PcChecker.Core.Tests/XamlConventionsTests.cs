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

    [Fact]
    public void Page_command_bindings_name_a_command_that_exists()
    {
        // A {Binding XCommand} that names no command fails silently: the button looks enabled and does nothing.
        // Commands come from [RelayCommand] methods (Foo/FooAsync -> FooCommand) or hand-written XCommand properties.
        var commandsByClass = new Dictionary<string, HashSet<string>>();
        foreach (var file in SourceFiles().Where(f => f.Contains($"{Path.DirectorySeparatorChar}ViewModels{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            var commands = new HashSet<string>();
            foreach (Match m in Regex.Matches(text, @"\[RelayCommand[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|async)\s+)*[\w<>?,\s]+?\s(\w+)\s*\("))
            {
                var name = m.Groups[1].Value;
                commands.Add((name.EndsWith("Async", StringComparison.Ordinal) ? name[..^5] : name) + "Command");
            }

            foreach (Match m in Regex.Matches(text, @"\b(\w+Command)\s*(?:\{|=>)"))
            {
                commands.Add(m.Groups[1].Value);
            }

            foreach (Match m in Regex.Matches(text, @"\bclass\s+(\w+)"))
            {
                if (!commandsByClass.TryGetValue(m.Groups[1].Value, out var set))
                {
                    commandsByClass[m.Groups[1].Value] = set = [];
                }

                set.UnionWith(commands);
            }
        }

        var pages = Directory.EnumerateFiles(Path.Combine(AppDirectory(), "Views", "Pages"), "*Page.xaml")
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .Select(p => (p.File, p.Text, Vm: Regex.Match(p.Text, @"d:DesignInstance\s+(?:Type=)?vm:(\w+)").Groups[1].Value))
            .Where(p => p.Vm.Length > 0)
            .ToList();
        Assert.NotEmpty(pages);

        // Item templates bind to row and card classes; another page's view model never counts.
        var pageViewModels = pages.Select(p => p.Vm).ToHashSet();
        var itemCommands = commandsByClass.Where(c => !pageViewModels.Contains(c.Key)).SelectMany(c => c.Value).ToHashSet();

        var offenders = new List<string>();
        foreach (var (file, text, vm) in pages)
        {
            var own = commandsByClass.GetValueOrDefault(vm) ?? [];
            foreach (Match b in Regex.Matches(text, @"Command=""\{Binding\s+(?:Path=)?(\w+Command)\s*\}"""))
            {
                var name = b.Groups[1].Value;
                if (!own.Contains(name) && !itemCommands.Contains(name))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {name} is not on {vm}");
                }
            }
        }

        Assert.Empty(offenders.Distinct());
    }

    internal static string AppDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcChecker.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "PcChecker.App");
    }

    internal static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(AppDirectory(), "*.xaml", SearchOption.AllDirectories).Where(NotBuildOutput);

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(AppDirectory(), "*.cs", SearchOption.AllDirectories).Where(NotBuildOutput);

    private static bool NotBuildOutput(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");
}
