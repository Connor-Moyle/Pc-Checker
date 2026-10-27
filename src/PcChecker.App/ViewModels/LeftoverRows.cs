using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Leftovers;
using PcChecker.Core.RegistryHealth;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>One finding on the Leftovers page with its tick box. Only High findings start ticked.</summary>
public sealed partial class LeftoverRow : ObservableObject
{
    private readonly Action _selectionChanged;

    [ObservableProperty]
    private bool _isSelected;

    public LeftoverRow(LeftoverItem item, Action selectionChanged)
    {
        Item = item;
        _isSelected = item.IsPreselected;
        _selectionChanged = selectionChanged;
    }

    public LeftoverItem Item { get; }

    public bool CanSelect => Item.CanRemove;

    public string Title => Item.Name;

    public string Reason => Item.Reason;

    public LeftoverConfidence Confidence => Item.Confidence;

    public string ConfidenceText => Item.Confidence switch
    {
        LeftoverConfidence.High => Loc.T("High"),
        LeftoverConfidence.Medium => Loc.T("Medium"),
        _ => Loc.T("Low"),
    };

    public string ConfidenceTooltip => Item.Confidence switch
    {
        LeftoverConfidence.High => Loc.T("Almost certainly left behind by a removed program. Ticked for you."),
        LeftoverConfidence.Medium => Loc.T("Probably left behind. Check the name before you tick it."),
        _ => Loc.T("Might still be used (recently changed, or shared by a maker's other programs). Only remove it if you recognise it."),
    };

    public bool MatchesFocus => Item.MatchesFocus;

    public string SizeText => Item.SizeBytes is { } bytes && bytes > 0 ? ByteSize.Format(bytes) : Item.Kind == LeftoverKind.EmptyFolder ? Loc.T("Empty") : string.Empty;

    /// <summary>Where it is, in the form people know: the folder, the regedit key, the service or task name.</summary>
    public string Location => Item.Kind switch
    {
        LeftoverKind.RegistryKey when LeftoverRules.SplitKey(Item.Location) is { } key => RegistryPathTools.FullKeyName(key.Hive, key.Path),
        LeftoverKind.Service => Loc.F("Service name: {0}", Item.Location),
        LeftoverKind.ScheduledTask => Loc.F("Task Scheduler: {0}", Item.Location),
        _ => Item.Location,
    };

    /// <summary>Extra facts on one line: the note and when it last changed.</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Item.Note))
            {
                parts.Add(Item.Note.Trim());
            }

            if (Item.LastChanged is { } changed)
            {
                parts.Add(Loc.F("Last changed {0}", changed.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)));
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    /// <summary>Folders and shortcuts can be shown in File Explorer.</summary>
    public bool CanOpen => Item.Kind is LeftoverKind.Folder or LeftoverKind.EmptyFolder or LeftoverKind.Shortcut;

    public string CheckTooltip => CanSelect
        ? Loc.T("Include this in the removal")
        : Loc.T("Only reported: it cannot be removed safely right now.");

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}

/// <summary>A kind of finding (folders, registry keys…) shown as one expander.</summary>
public sealed class LeftoverGroup
{
    public LeftoverGroup(string key, IEnumerable<LeftoverRow> rows)
    {
        Key = key;
        Rows = new ObservableCollection<LeftoverRow>(rows);
        long bytes = Rows.Sum(r => r.Item.SizeBytes ?? 0);
        var count = Rows.Count == 1 ? Loc.T("1 item") : Loc.F("{0} items", Rows.Count);
        int high = Rows.Count(r => r.Confidence == LeftoverConfidence.High);
        CountText = Key == FoldersKey && bytes > 0 ? $"{count} · {ByteSize.Format(bytes)}" : count;
        HighText = high == 0 ? string.Empty : Loc.F("{0} high", high);
    }

    public const string FoldersKey = "folders";

    public string Key { get; }

    public ObservableCollection<LeftoverRow> Rows { get; }

    public string CountText { get; }

    public string HighText { get; }

    public bool IsExpanded => Rows.Any(r => r.Confidence != LeftoverConfidence.Low) || Rows.Count <= 3;

    public SymbolRegular Icon => Key switch
    {
        FoldersKey => SymbolRegular.Folder24,
        "keys" => SymbolRegular.Database24,
        "services" => SymbolRegular.Settings24,
        "tasks" => SymbolRegular.CalendarClock24,
        _ => SymbolRegular.Link24,
    };

    public string Title => Key switch
    {
        FoldersKey => Loc.T("Folders"),
        "keys" => Loc.T("Registry keys"),
        "services" => Loc.T("Services"),
        "tasks" => Loc.T("Scheduled tasks"),
        _ => Loc.T("Start menu shortcuts"),
    };

    public string Description => Key switch
    {
        FoldersKey => Loc.T("Folders no installed program uses, including empty ones. They go to the Recycle Bin."),
        "keys" => Loc.T("Settings of makers and products that are no longer installed. Backed up to a .reg file first."),
        "services" => Loc.T("Services whose program file is gone. Only stopped services are deleted."),
        "tasks" => Loc.T("Tasks that start a program that is gone. Exported to XML first."),
        _ => Loc.T("Shortcuts to programs that are gone. They go to the Recycle Bin."),
    };

    /// <summary>The group a kind of finding belongs to.</summary>
    public static string KeyOf(LeftoverKind kind) => kind switch
    {
        LeftoverKind.Folder or LeftoverKind.EmptyFolder => FoldersKey,
        LeftoverKind.RegistryKey => "keys",
        LeftoverKind.Service => "services",
        LeftoverKind.ScheduledTask => "tasks",
        _ => "shortcuts",
    };

    public static int OrderOf(string key) => key switch
    {
        FoldersKey => 0,
        "keys" => 1,
        "services" => 2,
        "tasks" => 3,
        _ => 4,
    };
}

/// <summary>One entry of the restore list.</summary>
public sealed class LeftoverRestoreRow
{
    public LeftoverRestoreRow(LeftoverRestoreEntry entry)
    {
        Entry = entry;
    }

    public LeftoverRestoreEntry Entry { get; }

    public string Title => Entry.Title;

    public string Location => Entry.Location;

    public string KindText => Entry.Kind == LeftoverRestoreKind.RegistryKeys ? Loc.T("Registry keys") : Loc.T("Scheduled task");

    public string CreatedText => Entry.Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public bool IsRestored => Entry.Restored is not null;

    public bool CanRestore => Entry.Restored is null;

    public string StatusText => Entry.Restored is { } when
        ? Loc.F("Restored {0}", when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
        : Entry.BackupId;
}
