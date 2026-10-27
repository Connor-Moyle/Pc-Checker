namespace PcChecker.Core.ContextMenus;

/// <summary>How an entry gets into File Explorer's right-click menu, which also decides how it is turned off.</summary>
public enum MenuEntryKind
{
    /// <summary>A shell verb (<c>shell\&lt;name&gt;\command</c>); turned off with the <c>LegacyDisable</c> value.</summary>
    Command,

    /// <summary>A shell extension DLL (<c>shellex\ContextMenuHandlers</c>); turned off through the Blocked list.</summary>
    ShellExtension,

    /// <summary>A shortcut or file in the user's Send to folder; turned off by moving it to a backup folder.</summary>
    SendTo,

    /// <summary>A Windows 11 app extension from a packaged app (IExplorerCommand); turned off through the Blocked list.</summary>
    AppExtension,
}

/// <summary>Who added an entry. Windows' own entries are shown read-only; Microsoft apps need Advanced mode.</summary>
public enum MenuEntryOwner
{
    Program,
    MicrosoftApp,
    Windows,
}

/// <summary>Where an entry is registered: for the current user only, or for every user of the PC.</summary>
public enum MenuEntryScope
{
    User,
    Machine,
}

/// <summary>One registry key (or file) that makes up an entry.</summary>
public sealed record MenuEntryKey(MenuEntryScope Scope, string Path)
{
    /// <summary>"HKCU\…" or "HKLM\…" for display and copying.</summary>
    public string DisplayPath => (Scope == MenuEntryScope.User ? "HKCU\\" : "HKLM\\") + Path;
}

/// <summary>One line of File Explorer's right-click menu, merged across the places it is registered.</summary>
public sealed record MenuEntry
{
    /// <summary>Stable identity: kind plus the CLSID, verb name or file name.</summary>
    public required string Id { get; init; }

    public required MenuEntryKind Kind { get; init; }

    /// <summary>The text as File Explorer shows it (indirect strings resolved, access-key ampersands removed).</summary>
    public required string Text { get; init; }

    /// <summary>The verb or handler key name, the Send to file name, or the packaged verb id.</summary>
    public required string KeyName { get; init; }

    public string? Clsid { get; init; }

    /// <summary>The registry keys (or for Send to, the file) behind this entry. A verb is turned off in each of them.</summary>
    public IReadOnlyList<MenuEntryKey> Keys { get; init; } = [];

    public string? Command { get; init; }

    /// <summary>The program or DLL that does the work, when known.</summary>
    public string? FilePath { get; init; }

    public string? Publisher { get; init; }

    /// <summary>Plain words such as "Files", "Folders", "Desktop", ".txt files".</summary>
    public IReadOnlyList<string> Locations { get; init; } = [];

    public bool IsEnabled { get; init; } = true;

    public MenuEntryOwner Owner { get; init; }

    /// <summary>True when the program file it points at no longer exists, so the entry does nothing.</summary>
    public bool IsBroken { get; init; }

    /// <summary>True for verbs that only show while Shift is held.</summary>
    public bool ShiftOnly { get; init; }

    /// <summary>Package family or full name of a packaged app.</summary>
    public string? PackageName { get; init; }

    /// <summary>One extra sentence (why it is read-only, who turned it off).</summary>
    public string? Note { get; init; }

    /// <summary>True when Windows blocks it through the machine-wide Blocked list (turning it on needs that list changed).</summary>
    public bool BlockedForAllUsers { get; init; }

    /// <summary>True when another tool turned it off its own way (a '-' before the class ID); turn it on there.</summary>
    public bool DisabledElsewhere { get; init; }

    /// <summary>For a turned-off Send to item: where its file was moved.</summary>
    public string? BackupPath { get; init; }
}

/// <summary>Result of reading the right-click menu.</summary>
public sealed record MenuScanResult(
    IReadOnlyList<MenuEntry> Entries,
    IReadOnlyList<string> Warnings,
    DateTimeOffset Captured,
    ClassicMenuState ClassicMenu);

/// <summary>The Windows 11 "Show more options" switch.</summary>
public sealed record ClassicMenuState(bool Supported, bool IsClassic);

/// <summary>A finished change, kept so the page can undo it.</summary>
public sealed record MenuChange(string EntryId, string Title, bool TurnedOn, DateTimeOffset When);
