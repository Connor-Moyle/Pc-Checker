namespace PcChecker.Core.Shell;

/// <summary>
/// Ranks the quick search index for a query. The title counts most, then the page and section together ("monitor
/// power"), then the extra keywords, then the group alone. Each piece is matched in English and in the interface
/// language. Recently opened items are lifted above similar matches; with an empty query the palette shows the recent
/// items first, then the quick actions, then the pages.
/// </summary>
public static class QuickSearchRanker
{
    public const int DefaultMaxResults = 12;

    /// <summary>Added to a recent item's score; the most recent gets the most.</summary>
    public const int RecentBoost = 250;

    private const int RecentStep = 10;

    public static IReadOnlyList<SearchResult> Search(
        string? query,
        IReadOnlyList<SearchItem> items,
        IReadOnlyList<string>? recentIds = null,
        Func<string, string>? translate = null,
        int maxResults = DefaultMaxResults)
    {
        translate ??= static s => s;
        var recentRank = RecentRanks(recentIds);
        var normalizedQuery = FuzzyMatcher.Normalize(query);
        if (maxResults <= 0)
        {
            return [];
        }

        if (normalizedQuery.Length == 0)
        {
            return Browse(items, recentRank, translate, maxResults);
        }

        var scored = new List<(SearchResult Result, int Index)>();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            int score = ScoreItem(normalizedQuery, item, translate);
            if (score <= FuzzyMatcher.NoMatch)
            {
                continue;
            }

            bool recent = recentRank.TryGetValue(item.Id, out int rank);
            if (recent)
            {
                score += Math.Max(RecentStep, RecentBoost - (rank * RecentStep));
            }

            scored.Add((ToResult(item, score, recent, translate), i));
        }

        return scored
            .OrderByDescending(s => s.Result.Score)
            .ThenBy(s => KindOrder(s.Result.Item.Kind))
            .ThenBy(s => s.Index)
            .Take(maxResults)
            .Select(s => s.Result)
            .ToList();
    }

    /// <summary>The best score of <paramref name="normalizedQuery"/> over the item's text, before any recent boost.</summary>
    public static int ScoreItem(string normalizedQuery, SearchItem item, Func<string, string> translate)
    {
        int best = Best(normalizedQuery, item.Title, translate);

        // "monitor power" or "settings fans": section or page, then the title.
        if (!string.IsNullOrWhiteSpace(item.Group))
        {
            best = Math.Max(best, Scaled(Combined(normalizedQuery, item.Group, item.Title, translate), 80));
        }

        if (!string.IsNullOrWhiteSpace(item.PageTitle) && item.PageTitle != item.Title)
        {
            best = Math.Max(best, Scaled(Combined(normalizedQuery, item.PageTitle!, item.Title, translate), 80));
        }

        foreach (var keywords in item.Keywords)
        {
            best = Math.Max(best, Scaled(Best(normalizedQuery, keywords, translate), 60));
        }

        if (!string.IsNullOrWhiteSpace(item.Group))
        {
            best = Math.Max(best, Scaled(Best(normalizedQuery, item.Group, translate), 40));
        }

        return best;
    }

    private static IReadOnlyList<SearchResult> Browse(IReadOnlyList<SearchItem> items, Dictionary<string, int> recentRank, Func<string, string> translate, int maxResults)
    {
        var recent = items
            .Where(i => recentRank.ContainsKey(i.Id))
            .OrderBy(i => recentRank[i.Id])
            .Select(i => ToResult(i, 0, true, translate));
        var rest = items
            .Where(i => !recentRank.ContainsKey(i.Id) && i.Kind != SearchItemKind.Setting)
            .OrderBy(i => i.Kind == SearchItemKind.Action ? 0 : 1)
            .Select(i => ToResult(i, 0, false, translate));
        return recent.Concat(rest).Take(maxResults).ToList();
    }

    private static int Best(string normalizedQuery, string english, Func<string, string> translate)
    {
        int score = FuzzyMatcher.ScoreNormalized(normalizedQuery, FuzzyMatcher.Normalize(english));
        var local = translate(english);
        if (!string.Equals(local, english, StringComparison.Ordinal))
        {
            score = Math.Max(score, FuzzyMatcher.ScoreNormalized(normalizedQuery, FuzzyMatcher.Normalize(local)));
        }

        return score;
    }

    private static int Combined(string normalizedQuery, string first, string second, Func<string, string> translate)
    {
        int score = FuzzyMatcher.ScoreNormalized(normalizedQuery, FuzzyMatcher.Normalize(first + " " + second));
        var local = translate(first) + " " + translate(second);
        return Math.Max(score, FuzzyMatcher.ScoreNormalized(normalizedQuery, FuzzyMatcher.Normalize(local)));
    }

    private static int Scaled(int score, int percent) => score * percent / 100;

    private static SearchResult ToResult(SearchItem item, int score, bool recent, Func<string, string> translate) =>
        new(item, score, recent, translate(item.Title), string.IsNullOrWhiteSpace(item.Group) ? string.Empty : translate(item.Group));

    private static Dictionary<string, int> RecentRanks(IReadOnlyList<string>? recentIds)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        if (recentIds is null)
        {
            return ranks;
        }

        foreach (var id in recentIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                ranks.TryAdd(id, ranks.Count);
            }
        }

        return ranks;
    }

    private static int KindOrder(SearchItemKind kind) => kind switch
    {
        SearchItemKind.Page => 0,
        SearchItemKind.Action => 1,
        _ => 2,
    };
}
