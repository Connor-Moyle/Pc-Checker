using System.Net;
using System.Text.RegularExpressions;
using PcChecker.Core.Common;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Settings;
using PcChecker.Core.Shell;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Quick search (fuzzy matching, index, ranking, recent items) and the first-start welcome rules.</summary>
public class QuickSearchTests
{
    // ----- Fuzzy matcher -----

    [Theory]
    [InlineData("  Keyboard & Mouse ", "keyboard mouse")]
    [InlineData("Température (°C)", "temperature c")]
    [InlineData("GPU core offset limit (± MHz)", "gpu core offset limit mhz")]
    [InlineData("Größe", "grosse")]
    [InlineData("", "")]
    public void Normalize_lowercases_and_drops_accents_and_punctuation(string input, string expected) =>
        Assert.Equal(expected, FuzzyMatcher.Normalize(input));

    [Fact]
    public void Exact_beats_prefix_beats_word_match_beats_initials_beats_letters_in_order()
    {
        int exact = FuzzyMatcher.Score("drivers", "Drivers");
        int prefix = FuzzyMatcher.Score("driv", "Drivers");
        int words = FuzzyMatcher.Score("drivers old", "Old drivers");
        int initials = FuzzyMatcher.Score("wf", "Windows Features");
        int letters = FuzzyMatcher.Score("tmpfl", "Temporary files");

        Assert.Equal(FuzzyMatcher.ExactScore, exact);
        Assert.True(exact > prefix, $"{exact} > {prefix}");
        Assert.True(prefix > initials, $"{prefix} > {initials}");
        Assert.True(initials > words, $"{initials} > {words}");
        Assert.True(words > letters, $"{words} > {letters}");
        Assert.True(letters > FuzzyMatcher.NoMatch);
    }

    [Theory]
    [InlineData("km", "Keyboard & Mouse")]
    [InlineData("ws", "Windows Features")] // not the initials, but w…s in order from a word start
    [InlineData("temp", "Temperatures")]
    [InlineData("mouse", "Keyboard & Mouse")]
    [InlineData("restore point", "Create a restore point before making changes")]
    [InlineData("TEMPÉRATURES", "Températures")]
    [InlineData("temperatures", "Températures")]
    public void Matches_words_initials_and_accents(string query, string text) =>
        Assert.True(FuzzyMatcher.Score(query, text) > FuzzyMatcher.NoMatch, $"'{query}' should match '{text}'");

    [Theory]
    [InlineData("xyz", "Drivers")]
    [InlineData("fans lighting", "Fans")] // every typed word must match
    [InlineData("iv", "Old drivers update")] // two letters only match at the start of a word
    public void Does_not_match_unrelated_text(string query, string text) =>
        Assert.Equal(FuzzyMatcher.NoMatch, FuzzyMatcher.Score(query, text));

    [Fact]
    public void Inside_a_word_matches_from_three_letters_but_below_a_word_start()
    {
        Assert.True(FuzzyMatcher.Score("rivers", "Drivers") > FuzzyMatcher.NoMatch);
        Assert.True(FuzzyMatcher.Score("rivers", "Drivers") < FuzzyMatcher.Score("driv", "Drivers"));
    }

    [Fact]
    public void Shorter_text_wins_a_prefix_tie()
    {
        Assert.True(FuzzyMatcher.Score("fan", "Fans") > FuzzyMatcher.Score("fan", "Fan profile and curves"));
    }

    [Fact]
    public void Letters_in_order_must_start_at_a_word()
    {
        Assert.Equal(FuzzyMatcher.NoMatch, FuzzyMatcher.Score("rs", "Drivers")); // 'r' is not at a word start
        Assert.True(FuzzyMatcher.Score("dvs", "Drivers") > FuzzyMatcher.NoMatch);
    }

    // ----- Index builder -----

    [Fact]
    public void Index_has_every_page_of_the_navigation_catalog()
    {
        var pages = CatalogPages();
        Assert.True(pages.Count >= 25, $"Parsed only {pages.Count} pages from NavigationCatalog.cs");

        var index = QuickSearchIndex.Build(pages, SettingsSearchCatalog.All, QuickActionCatalog.All);

        foreach (var page in pages)
        {
            var item = Assert.Single(index, i => i.Id == QuickSearchIndex.PageId(page.PageKey));
            Assert.Equal(SearchItemKind.Page, item.Kind);
            Assert.Equal(page.Title, item.Title);
            Assert.Equal(page.Section, item.Group);
        }

        Assert.Equal(index.Count, index.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void Index_holds_every_setting_and_action_whose_page_exists()
    {
        var pages = CatalogPages();
        var index = QuickSearchIndex.Build(pages, SettingsSearchCatalog.All, QuickActionCatalog.All);

        Assert.All(SettingsSearchCatalog.All, s => Assert.Contains(index, i => i.Id == QuickSearchIndex.SettingId(s.PageKey, s.Title)));
        Assert.All(QuickActionCatalog.All, a => Assert.Contains(index, i => i.Id == QuickSearchIndex.ActionId(a.Id)));
        Assert.All(index.Where(i => i.Kind == SearchItemKind.Setting), i => Assert.Equal(i.Title, i.Anchor));
    }

    [Fact]
    public void Index_leaves_out_settings_and_actions_for_pages_this_build_does_not_have()
    {
        var pages = new[] { new SearchPageInfo("Home", "Dashboard", "dashboard") };
        var settings = new[]
        {
            new SettingSearchEntry("Theme", "Appearance", "settings"),
            new SettingSearchEntry("Benchmark length", "Benchmark", "benchmark"),
        };
        var actions = new[]
        {
            new QuickActionDefinition("scan.full", "Run a full scan", "dashboard"),
            new QuickActionDefinition("bench.run", "Run benchmark", "benchmark"),
            new QuickActionDefinition("overlay.toggle", "Show or hide the overlay", null),
        };

        var index = QuickSearchIndex.Build(pages, settings, actions);

        Assert.Equal(["page:dashboard", "action:scan.full", "action:overlay.toggle"], index.Select(i => i.Id));
    }

    [Fact]
    public void Catalog_keys_point_at_real_pages_and_real_labels()
    {
        var keys = CatalogPages().Select(p => p.PageKey).ToHashSet();
        Assert.All(PageSearchKeywords.Keys, k => Assert.Contains(k, keys));
        Assert.All(QuickActionCatalog.All.Where(a => a.PageKey is not null), a => Assert.Contains(a.PageKey!, keys));
        Assert.Equal(QuickActionCatalog.All.Count, QuickActionCatalog.All.Select(a => a.Id).Distinct().Count());

        // Each setting title is a label on its page, so the app can scroll it into view.
        var missing = new List<string>();
        foreach (var group in SettingsSearchCatalog.All.GroupBy(s => s.PageKey))
        {
            Assert.Contains(group.Key, keys);
            var labels = PageLabels(group.Key);
            missing.AddRange(group.Where(s => !labels.Contains(s.Title)).Select(s => $"{group.Key}: {s.Title}"));
        }

        Assert.Empty(missing);
    }

    // ----- Ranking -----

    [Fact]
    public void Ranking_puts_the_closest_title_first()
    {
        var index = FullIndex();

        Assert.Equal("page:drivers", QuickSearchRanker.Search("drivers", index)[0].Item.Id);
        Assert.Equal("page:windows-features", QuickSearchRanker.Search("wf", index)[0].Item.Id);
        Assert.Equal("page:input", QuickSearchRanker.Search("mouse", index)[0].Item.Id);
        Assert.Equal("page:power", QuickSearchRanker.Search("monitor power", index)[0].Item.Id);
        Assert.Equal("setting:settings/Theme", QuickSearchRanker.Search("theme", index)[0].Item.Id);
        Assert.Contains(QuickSearchRanker.Search("uninstall", index), r => r.Item.Id == "page:software");
        Assert.Contains(QuickSearchRanker.Search("junk", index).Take(3), r => r.Item.Id == "action:cleanup.scan");
    }

    [Fact]
    public void Ranking_matches_translated_and_english_text()
    {
        var index = FullIndex();
        var german = new Dictionary<string, string> { ["Drivers"] = "Treiber", ["Apps & Drivers"] = "Apps und Treiber" };
        string Translate(string s) => german.TryGetValue(s, out var t) ? t : s;

        var byGerman = QuickSearchRanker.Search("treiber", index, translate: Translate);
        Assert.Equal("page:drivers", byGerman[0].Item.Id);
        Assert.Equal("Treiber", byGerman[0].Title);
        Assert.Equal("Apps und Treiber", byGerman[0].Group);

        Assert.Equal("page:drivers", QuickSearchRanker.Search("drivers", index, translate: Translate)[0].Item.Id);
    }

    [Fact]
    public void Recent_items_come_first_in_an_empty_query_then_actions()
    {
        var index = FullIndex();
        var recent = new[] { "page:fans", "setting:settings/Theme", "gone:item" };

        var results = QuickSearchRanker.Search("", index, recent, maxResults: 5);

        Assert.Equal(["page:fans", "setting:settings/Theme"], results.Take(2).Select(r => r.Item.Id));
        Assert.True(results[0].IsRecent && results[1].IsRecent);
        Assert.All(results.Skip(2), r => Assert.Equal(SearchItemKind.Action, r.Item.Kind));
        Assert.Equal(5, results.Count);
    }

    [Fact]
    public void A_recent_item_is_lifted_above_a_similar_match()
    {
        var index = FullIndex();
        var plain = QuickSearchRanker.Search("temp", index);
        var other = plain.First(r => r.Item.Id != plain[0].Item.Id && r.Score < plain[0].Score);

        var withRecent = QuickSearchRanker.Search("temp", index, [other.Item.Id]);

        Assert.Equal(other.Item.Id, withRecent[0].Item.Id);
        Assert.True(withRecent[0].IsRecent);
    }

    [Fact]
    public void Ranking_respects_the_result_limit_and_ignores_unmatched_queries()
    {
        var index = FullIndex();
        Assert.Equal(3, QuickSearchRanker.Search("s", index, maxResults: 3).Count);
        Assert.Empty(QuickSearchRanker.Search("qqqzzz", index));
        Assert.Empty(QuickSearchRanker.Search("fans", index, maxResults: 0));
    }

    // ----- Recent items -----

    [Fact]
    public void Recent_push_moves_to_front_removes_duplicates_and_trims()
    {
        var list = RecentItems.Push(["a", "b", "c"], "b");
        Assert.Equal(["b", "a", "c"], list);

        list = RecentItems.Push(["a", "b", "c"], "d", capacity: 3);
        Assert.Equal(["d", "a", "b"], list);

        list = RecentItems.Push(null, "x");
        Assert.Equal(["x"], list);

        list = RecentItems.Push(["a", "", "a", "b"], "c");
        Assert.Equal(["c", "a", "b"], list);
    }

    [Fact]
    public void Recent_prune_drops_unknown_blank_and_duplicate_ids_in_order()
    {
        var pruned = RecentItems.Prune(["page:fans", "page:gone", "", "page:fans", "action:scan.full"], ["page:fans", "action:scan.full"]);
        Assert.Equal(["page:fans", "action:scan.full"], pruned);
    }

    // ----- Welcome -----

    [Theory]
    [InlineData(null, false, false)] // first start: no settings file yet
    [InlineData(null, true, true)] // settings from an earlier version: already a user
    [InlineData(false, true, false)] // new user who has not seen it yet
    [InlineData(true, false, true)]
    public void Welcome_flag_is_resolved_from_the_settings_file(bool? stored, bool fileExisted, bool expectedSeen) =>
        Assert.Equal(expectedSeen, WelcomeRules.ResolveSeen(stored, fileExisted));

    [Fact]
    public void Welcome_shows_only_on_a_normal_first_start()
    {
        var normal = CommandLineOptions.Parse([]);
        Assert.True(WelcomeRules.ShouldShow(false, normal));
        Assert.True(WelcomeRules.ShouldShow(false, CommandLineOptions.Parse(["--demo"])));
        Assert.False(WelcomeRules.ShouldShow(true, normal));
        Assert.False(WelcomeRules.ShouldShow(null, normal));
        Assert.False(WelcomeRules.ShouldShow(false, CommandLineOptions.Parse(["--screenshots", "shots"])));
        Assert.False(WelcomeRules.ShouldShow(false, CommandLineOptions.Parse(["--scheduled", "health"])));
        Assert.Equal(ScheduledJobKind.Health, CommandLineOptions.Parse(["--scheduled", "health"]).ScheduledJob);
    }

    [Fact]
    public async Task Settings_from_an_older_version_load_with_no_welcome_flag_and_an_empty_recent_list()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcchecker-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, JsonSettingsService.FileName),
                """{ "General": { "Theme": "Dark", "RecentSearchItems": null } }""");
            var svc = new JsonSettingsService(dir);
            await svc.LoadAsync();

            Assert.Null(svc.Current.General.WelcomeSeen);
            Assert.NotNull(svc.Current.General.RecentSearchItems);
            Assert.Empty(svc.Current.General.RecentSearchItems);

            svc.Current.General.WelcomeSeen = true;
            svc.Current.General.RecentSearchItems = ["page:fans"];
            await svc.SaveAsync();
            var reloaded = new JsonSettingsService(dir);
            await reloaded.LoadAsync();
            Assert.True(reloaded.Current.General.WelcomeSeen);
            Assert.Equal(["page:fans"], reloaded.Current.General.RecentSearchItems);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ----- Helpers -----

    private static IReadOnlyList<SearchItem> FullIndex() =>
        QuickSearchIndex.Build(CatalogPages(), SettingsSearchCatalog.All, QuickActionCatalog.All);

    /// <summary>The pages of src/PcChecker.App/Services/NavigationCatalog.cs, parsed from the source (the app needs Windows).</summary>
    private static List<SearchPageInfo> CatalogPages()
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory(), "Services", "NavigationCatalog.cs"));
        var pages = new List<SearchPageInfo>();
        string? section = null;
        foreach (Match m in Regex.Matches(source, @"\b(Section|Footer|Page)\(""([^""]+)""(?:,\s*""([^""]+)"")?"))
        {
            if (m.Groups[1].Value is "Section" or "Footer")
            {
                section = m.Groups[2].Value;
            }
            else if (section is not null)
            {
                pages.Add(new SearchPageInfo(section, m.Groups[2].Value, m.Groups[3].Value));
            }
        }

        return pages;
    }

    /// <summary>Every literal Text/Content/Header label in the XAML of the page with <paramref name="pageKey"/>.</summary>
    private static HashSet<string> PageLabels(string pageKey)
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory(), "Services", "NavigationCatalog.cs"));
        var m = Regex.Match(source, $@"Page\(""[^""]+"",\s*""{Regex.Escape(pageKey)}"",\s*typeof\((\w+)\)\)");
        Assert.True(m.Success, $"No page with key {pageKey}");
        var xaml = File.ReadAllText(Path.Combine(AppDirectory(), "Views", "Pages", m.Groups[1].Value + ".xaml"));
        return Regex.Matches(xaml, @"\b(?:Text|Content|Header)=""([^""{][^""]*)""")
            .Select(x => WebUtility.HtmlDecode(x.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
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
}
