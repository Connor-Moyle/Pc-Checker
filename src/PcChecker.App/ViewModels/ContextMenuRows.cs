using PcChecker.App.Localization;
using PcChecker.Core.ContextMenus;

namespace PcChecker.App.ViewModels;

/// <summary>One line of the right-click menu list.</summary>
public sealed class MenuEntryRow
{
    public MenuEntryRow(MenuEntry entry, bool advancedMode)
    {
        Entry = entry;
        LockReason = ContextMenuRules.WhyLocked(entry, advancedMode);
    }

    public MenuEntry Entry { get; }

    public string Text => Entry.Text;

    /// <summary>Publisher and file, for the line under the name.</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Entry.Publisher))
            {
                parts.Add(Entry.Publisher!);
            }

            if (!string.IsNullOrWhiteSpace(Entry.FilePath))
            {
                parts.Add(Entry.FilePath!);
            }

            return parts.Count == 0 ? Loc.T("Publisher unknown") : string.Join(" · ", parts);
        }
    }

    public string LocationsText => string.Join(", ", Entry.Locations.Select(Place));

    public string KindText => Entry.Kind switch
    {
        MenuEntryKind.Command => Loc.T("Command"),
        MenuEntryKind.ShellExtension => Loc.T("Shell extension"),
        MenuEntryKind.SendTo => Loc.T("Send to"),
        _ => Loc.T("Windows 11 app"),
    };

    public string StateText => Entry.IsEnabled ? Loc.T("On") : Loc.T("Off");

    public string OwnerText => Entry.Owner switch
    {
        MenuEntryOwner.Windows => Loc.T("Windows"),
        MenuEntryOwner.MicrosoftApp => Loc.T("Microsoft app"),
        _ => Loc.T("Program"),
    };

    public bool IsEnabled => Entry.IsEnabled;

    public bool IsBroken => Entry.IsBroken;

    public bool IsLocked => LockReason is not null;

    /// <summary>Why it cannot be changed (Windows' own, Advanced mode needed), or null.</summary>
    public string? LockReason { get; }

    public bool CanTurnOff => LockReason is null && Entry.IsEnabled;

    public bool CanTurnOn => LockReason is null && !Entry.IsEnabled;

    /// <summary>Extra notes for the details card: shift-only, broken, who blocked it.</summary>
    public string? Note
    {
        get
        {
            var notes = new List<string>();
            if (Entry.Note is { } note)
            {
                notes.Add(note);
            }

            if (Entry.ShiftOnly)
            {
                notes.Add(Loc.T("Only shows when you hold Shift while right-clicking."));
            }

            return notes.Count == 0 ? null : string.Join(" ", notes);
        }
    }

    public bool HasNote => Note is not null;

    /// <summary>Where it is registered, for the details card and "Copy where it is registered".</summary>
    public string WhereText => Entry.Kind switch
    {
        MenuEntryKind.SendTo => Entry.BackupPath ?? Entry.KeyName,
        MenuEntryKind.AppExtension => (Entry.PackageName ?? string.Empty) + (Entry.Clsid is null ? string.Empty : " " + Entry.Clsid),
        MenuEntryKind.ShellExtension => string.Join("\n", Entry.Keys.Select(k => k.DisplayPath).Prepend(Entry.Clsid ?? string.Empty).Where(s => s.Length > 0)),
        _ => string.Join("\n", Entry.Keys.Select(k => k.DisplayPath)),
    };

    /// <summary>Translates the fixed place names; file types (".txt files") stay as they are.</summary>
    private static string Place(string location) => location switch
    {
        "Files" => Loc.T("Files"),
        "Files and folders" => Loc.T("Files and folders"),
        "Folders" => Loc.T("Folders"),
        "Inside a folder" => Loc.T("Inside a folder"),
        "Desktop" => Loc.T("Desktop"),
        "Drives" => Loc.T("Drives"),
        "Send to" => Loc.T("Send to"),
        _ => location,
    };
}
