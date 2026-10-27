namespace PcChecker.Core.ContextMenus;

/// <summary>The quick filters of the right-click menu list, in the order the drop-down shows them.</summary>
public enum MenuFilter
{
    All,
    TurnedOn,
    TurnedOff,
    Files,
    Folders,
    DesktopAndDrives,
    SendTo,
    WindowsApps,
    NotWorking,
}

/// <summary>Search and filter for the right-click menu list. Windows' own entries only show when asked for.</summary>
public static class ContextMenuFilter
{
    public static IReadOnlyList<MenuEntry> Apply(IEnumerable<MenuEntry> entries, string? search, MenuFilter filter, bool showWindowsEntries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string term = search?.Trim() ?? string.Empty;
        return entries
            .Where(e => showWindowsEntries || e.Owner != MenuEntryOwner.Windows)
            .Where(e => Matches(e, filter))
            .Where(e => term.Length == 0 || MatchesSearch(e, term))
            .ToList();
    }

    public static bool Matches(MenuEntry entry, MenuFilter filter)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return filter switch
        {
            MenuFilter.TurnedOn => entry.IsEnabled,
            MenuFilter.TurnedOff => !entry.IsEnabled,
            MenuFilter.Files => entry.Locations.Any(l => l is "Files" or "Files and folders" || l.EndsWith(" files", StringComparison.Ordinal)),
            MenuFilter.Folders => entry.Locations.Any(l => l is "Folders" or "Inside a folder" or "Files and folders"),
            MenuFilter.DesktopAndDrives => entry.Locations.Any(l => l is "Desktop" or "Drives"),
            MenuFilter.SendTo => entry.Kind == MenuEntryKind.SendTo,
            MenuFilter.WindowsApps => entry.Kind == MenuEntryKind.AppExtension,
            MenuFilter.NotWorking => entry.IsBroken,
            _ => true,
        };
    }

    private static bool MatchesSearch(MenuEntry entry, string term) =>
        Contains(entry.Text, term) || Contains(entry.Publisher, term) || Contains(entry.FilePath, term)
        || Contains(entry.KeyName, term) || entry.Locations.Any(l => Contains(l, term));

    private static bool Contains(string? text, string term) =>
        text is not null && text.Contains(term, StringComparison.CurrentCultureIgnoreCase);
}
