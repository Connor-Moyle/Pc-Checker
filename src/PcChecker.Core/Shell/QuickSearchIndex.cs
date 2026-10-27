namespace PcChecker.Core.Shell;

/// <summary>
/// Builds the quick search index from the navigation pages, the settings catalog and the quick actions. Settings and
/// actions whose page is not in this build are left out, so the palette never offers something that cannot open.
/// </summary>
public static class QuickSearchIndex
{
    public const string PagePrefix = "page:";
    public const string SettingPrefix = "setting:";
    public const string ActionPrefix = "action:";

    /// <summary>Pages in menu order, then quick actions, then settings. Ids are unique (the first one wins).</summary>
    public static IReadOnlyList<SearchItem> Build(
        IEnumerable<SearchPageInfo> pages,
        IEnumerable<SettingSearchEntry> settings,
        IEnumerable<QuickActionDefinition> actions)
    {
        var pageList = pages.Where(p => !string.IsNullOrWhiteSpace(p.PageKey)).ToList();
        var pageByKey = new Dictionary<string, SearchPageInfo>(StringComparer.Ordinal);
        foreach (var page in pageList)
        {
            pageByKey.TryAdd(page.PageKey, page);
        }

        var items = new List<SearchItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Add(SearchItem item)
        {
            if (ids.Add(item.Id))
            {
                items.Add(item);
            }
        }

        foreach (var page in pageList)
        {
            var keywords = new List<string>();
            var extra = PageSearchKeywords.For(page.PageKey);
            if (extra.Length > 0)
            {
                keywords.Add(extra);
            }

            Add(new SearchItem(PageId(page.PageKey), SearchItemKind.Page, page.Title, page.Section, page.PageKey, page.Title, keywords));
        }

        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.Id) || string.IsNullOrWhiteSpace(action.Title))
            {
                continue;
            }

            string? pageTitle = null;
            if (action.PageKey is { } key)
            {
                if (!pageByKey.TryGetValue(key, out var page))
                {
                    continue;
                }

                pageTitle = page.Title;
            }

            Add(new SearchItem(ActionId(action.Id), SearchItemKind.Action, action.Title, pageTitle ?? string.Empty, action.PageKey, pageTitle,
                KeywordList(action.Keywords)));
        }

        foreach (var setting in settings)
        {
            if (string.IsNullOrWhiteSpace(setting.Title) || !pageByKey.TryGetValue(setting.PageKey, out var page))
            {
                continue;
            }

            Add(new SearchItem(SettingId(setting.PageKey, setting.Title), SearchItemKind.Setting, setting.Title, setting.Group, setting.PageKey, page.Title,
                KeywordList(setting.Keywords))
            {
                Anchor = setting.Title,
            });
        }

        return items;
    }

    public static string PageId(string pageKey) => PagePrefix + pageKey;

    public static string ActionId(string actionId) => ActionPrefix + actionId;

    public static string SettingId(string pageKey, string title) => SettingPrefix + pageKey + "/" + title;

    /// <summary>The quick action id inside an index id ("action:scan.full" → "scan.full"), or null for other items.</summary>
    public static string? ActionIdOf(string itemId) =>
        itemId.StartsWith(ActionPrefix, StringComparison.Ordinal) ? itemId[ActionPrefix.Length..] : null;

    private static IReadOnlyList<string> KeywordList(string? keywords) =>
        string.IsNullOrWhiteSpace(keywords) ? [] : [keywords];
}
