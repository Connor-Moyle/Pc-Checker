using System.Text.Json;
using System.Text.RegularExpressions;
using PcChecker.Core.Localization;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Checks the shipped translation files (src/PcChecker.App/Localization/Languages/&lt;code&gt;.json).</summary>
public class TranslationFilesTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    public static IEnumerable<object[]> Languages() => LanguageCatalog.TranslatedCodes.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_shipped_language_has_a_readable_file(string code)
    {
        var map = Load(code);
        Assert.NotEmpty(map);
        Assert.DoesNotContain(map, kv => string.IsNullOrWhiteSpace(kv.Value));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Translations_keep_every_placeholder(string code)
    {
        var broken = Load(code)
            .Where(kv => !Placeholders(kv.Key).SequenceEqual(Placeholders(kv.Value)))
            .Select(kv => $"{kv.Key} => {kv.Value}")
            .ToList();
        Assert.Empty(broken);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Most_interface_strings_are_translated(string code)
    {
        var strings = File.ReadAllLines(Path.Combine(LocalizationDirectory(), "strings.txt")).Where(l => l.Length > 0).Select(l => l.Replace("\\n", "\n")).ToHashSet();
        var map = Load(code);
        int covered = strings.Count(map.ContainsKey);
        Assert.True(covered >= strings.Count * 0.9, $"{code} covers {covered} of {strings.Count} strings; run the translation step again.");
    }

    private static IEnumerable<string> Placeholders(string text) =>
        Placeholder.Matches(text).Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal);

    private static Dictionary<string, string> Load(string code)
    {
        var path = Path.Combine(LocalizationDirectory(), "Languages", code + ".json");
        Assert.True(File.Exists(path), $"Missing translation file {path}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
    }

    private static string LocalizationDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcChecker.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "PcChecker.App", "Localization");
    }
}
