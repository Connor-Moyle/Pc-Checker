namespace PcChecker.Core.Shell;

/// <summary>What a quick search result opens.</summary>
public enum SearchItemKind
{
    /// <summary>A page or tab from the navigation.</summary>
    Page,

    /// <summary>One option on a page (most of them on Settings); opening it scrolls the option into view.</summary>
    Setting,

    /// <summary>Something to do straight away, such as a full scan.</summary>
    Action,
}

/// <summary>A page as the quick search sees it: section, tab title and the stable key from the navigation catalog.</summary>
public sealed record SearchPageInfo(string Section, string Title, string PageKey);

/// <summary>
/// One option for the search index. <see cref="Title"/> is the English label exactly as the page shows it, so the
/// app can find it on screen and scroll to it; <see cref="Keywords"/> holds extra English words people may type.
/// </summary>
public sealed record SettingSearchEntry(string Title, string Group, string PageKey, string Keywords = "");

/// <summary>
/// A quick action. <see cref="PageKey"/> is the page the action runs on, or null for an action that needs no page
/// (it is then always offered).
/// </summary>
public sealed record QuickActionDefinition(string Id, string Title, string? PageKey, string Keywords = "");

/// <summary>
/// One searchable entry. All text is English; the ranker translates it for display and matches both the English
/// and the translated text, so typing either finds it.
/// </summary>
public sealed record SearchItem(
    string Id,
    SearchItemKind Kind,
    string Title,
    string Group,
    string? PageKey,
    string? PageTitle,
    IReadOnlyList<string> Keywords)
{
    /// <summary>For settings: the label to scroll into view on the page.</summary>
    public string? Anchor { get; init; }
}

/// <summary>A ranked match, with its title and group already translated for display.</summary>
public sealed record SearchResult(SearchItem Item, int Score, bool IsRecent, string Title, string Group);
