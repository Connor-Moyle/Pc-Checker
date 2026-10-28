using static PcChecker.Core.Shell.SearchText;

namespace PcChecker.Core.Shell;

/// <summary>One line of the keyboard shortcuts overlay: the keys as printed and what they do.</summary>
public sealed record KeyboardShortcut(string Keys, string Description);

/// <summary>
/// The keys listed by the shortcuts overlay ("?" or Shift+/): the ones that work everywhere, and the extra keys of
/// some pages. The window's key handler and the pages implement them; this list only describes them, so keep the two in
/// step when you add a key.
/// </summary>
public static class KeyboardShortcutCatalog
{
    /// <summary>Keys that work on every page.</summary>
    public static IReadOnlyList<KeyboardShortcut> Global { get; } =
    [
        new("Ctrl+K", T("Search for any page, setting or action")),
        new("F1", T("Help for this page")),
        new("?", T("Show this list of keyboard shortcuts")),
        new("F5", T("Refresh or scan again (pages that have a refresh)")),
        new("Ctrl+Tab", T("Next tab in this section (Ctrl+Shift+Tab goes back)")),
        new("F6", T("Move between the menu, the tabs and the page (Shift+F6 goes back)")),
        new("Alt+letter", T("Press the button with that underlined letter")),
        new("Shift+F10", T("Open the right-click menu of the selected item")),
        new("Esc", T("Close a dialog, the search box or this list")),
    ];

    private static readonly Dictionary<string, IReadOnlyList<KeyboardShortcut>> PageKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["processes"] =
        [
            new("Enter", T("Switch to the selected program")),
            new("Delete", T("End the selected task (asks first)")),
            new("Ctrl+C", T("Copy the details of the selected process")),
            new("Ctrl+F", T("Search the list")),
            new("Left / Right", T("Collapse or expand a group")),
        ],
        ["live-usage"] =
        [
            new("Up / Down", T("Pick the part to show")),
        ],
        ["app-history"] =
        [
            new("Enter", T("Open the selected app's file location")),
        ],
        ["overlay-settings"] =
        [
            new("Ctrl+Shift+O", T("Show or hide the in-game overlay (the default key; you can change it here)")),
        ],
    };

    /// <summary>The extra keys of the page with <paramref name="pageKey"/> (empty when it has none).</summary>
    public static IReadOnlyList<KeyboardShortcut> ForPage(string? pageKey) =>
        pageKey is not null && PageKeys.TryGetValue(pageKey, out var keys) ? keys : [];

    /// <summary>The page keys that have extra shortcuts.</summary>
    public static IEnumerable<string> PagesWithShortcuts => PageKeys.Keys;
}
