using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;

namespace PcChecker.App.ViewModels;

/// <summary>The drill-down: folders sorted by size under the current folder, a breadcrumb, and the file lists.</summary>
public sealed partial class DiskSpaceViewModel
{
    /// <summary>Folders shown at once; the rest are summed up in one line.</summary>
    private const int MaxFolderRows = 200;

    private readonly Dictionary<string, DiskItemRow> _rows = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
    private FolderNode? _currentFolder;

    [ObservableProperty]
    private string _currentFolderText = string.Empty;

    [ObservableProperty]
    private DiskLooseFilesRow? _looseFiles;

    [ObservableProperty]
    private string? _moreFoldersText;

    public ObservableCollection<DiskBreadcrumb> Breadcrumbs { get; } = [];

    public ObservableCollection<DiskFolderRow> FolderRows { get; } = [];

    public ObservableCollection<DiskFileRow> LargestFiles { get; } = [];

    public ObservableCollection<DiskFileRow> OldFiles { get; } = [];

    public bool HasLooseFiles => LooseFiles is not null;

    public bool HasMoreFolders => !string.IsNullOrEmpty(MoreFoldersText);

    public bool IsFolderEmpty => CurrentFolder is not null && FolderRows.Count == 0 && LooseFiles is null;

    public bool HasLargestFiles => LargestFiles.Count > 0;

    public bool HasOldFiles => OldFiles.Count > 0;

    public bool HasTypes => Types.Count > 0;

    public string OldFilesHint =>
        Localization.Loc.F("Files of {0} or more that nobody opened or changed for {1} months.",
            ByteSize.Format((long)(OldFileMinMegabytes * ByteSize.Megabyte)), Math.Round(OldFileMonths));

    partial void OnLooseFilesChanged(DiskLooseFilesRow? value) => OnPropertyChanged(nameof(HasLooseFiles));

    partial void OnMoreFoldersTextChanged(string? value) => OnPropertyChanged(nameof(HasMoreFolders));

    partial void OnOldFileMinMegabytesChanged(double value) => OnPropertyChanged(nameof(OldFilesHint));

    partial void OnOldFileMonthsChanged(double value) => OnPropertyChanged(nameof(OldFilesHint));

    [RelayCommand]
    private void OpenFolder(DiskFolderRow? row)
    {
        if (row is not null && row.CanOpen)
        {
            NavigateTo(row.Node);
        }
    }

    [RelayCommand]
    private void GoToBreadcrumb(DiskBreadcrumb? crumb)
    {
        if (crumb is not null)
        {
            NavigateTo(crumb.Node);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void GoUp()
    {
        if (CurrentFolder?.Parent is { } parent)
        {
            NavigateTo(parent);
        }
    }

    private bool CanGoUp() => CurrentFolder?.Parent is not null;

    /// <summary>Shows <paramref name="folder"/>: its breadcrumb, its sub-folders largest first and its own files as one line.</summary>
    private void NavigateTo(FolderNode folder)
    {
        CurrentFolder = folder;
        Breadcrumbs.Clear();
        var trail = DiskSpaceAggregation.Breadcrumbs(folder);
        for (int i = 0; i < trail.Count; i++)
        {
            Breadcrumbs.Add(new DiskBreadcrumb(trail[i], i == trail.Count - 1));
        }

        var children = DiskSpaceAggregation.SortedChildren(folder);
        FolderRows.Clear();
        foreach (var child in children.Take(MaxFolderRows))
        {
            FolderRows.Add(FolderRow(child));
        }

        int hidden = children.Count - MaxFolderRows;
        MoreFoldersText = hidden > 0
            ? DiskSpaceText.Count(hidden, Localization.Loc.T("…and {0} smaller folder"), Localization.Loc.T("…and {0} smaller folders"))
                + $" ({ByteSize.Format(children.Skip(MaxFolderRows).Sum(c => c.TotalBytes))})"
            : null;
        LooseFiles = folder.OwnFiles > 0 ? new DiskLooseFilesRow(folder) : null;
        CurrentFolderText = $"{ByteSize.Format(folder.TotalBytes)} · {DiskSpaceText.Files(folder.TotalFiles)}";
        OnPropertyChanged(nameof(IsFolderEmpty));
    }

    private void FillFileLists()
    {
        LargestFiles.Clear();
        OldFiles.Clear();
        Types.Clear();
        if (_scan is not { } scan)
        {
            return;
        }

        foreach (var f in scan.LargestFiles)
        {
            LargestFiles.Add(FileRow(f));
        }

        foreach (var f in scan.OldFiles)
        {
            OldFiles.Add(FileRow(f));
        }

        long all = scan.Types.Sum(t => t.Bytes);
        foreach (var t in scan.Types.Where(t => t.Bytes > 0))
        {
            Types.Add(new DiskTypeRow(t, all));
        }

        OnPropertyChanged(nameof(HasLargestFiles));
        OnPropertyChanged(nameof(HasOldFiles));
        OnPropertyChanged(nameof(HasTypes));
        OnPropertyChanged(nameof(OldFilesHint));
    }

    private DiskFolderRow FolderRow(FolderNode node)
    {
        if (_rows.TryGetValue(node.FullPath, out var existing) && existing is DiskFolderRow folder && ReferenceEquals(folder.Node, node))
        {
            folder.Refresh();
            return folder;
        }

        var row = new DiskFolderRow(node, _service.WhyProtected(node.FullPath), UpdateSelection);
        _rows[node.FullPath] = row;
        return row;
    }

    private DiskFileRow FileRow(DiskFileEntry entry)
    {
        if (_rows.TryGetValue(entry.Path, out var existing) && existing is DiskFileRow file)
        {
            return file;
        }

        var row = new DiskFileRow(entry, _service.WhyProtected(entry.Path), UpdateSelection);
        _rows[entry.Path] = row;
        return row;
    }

    /// <summary>Forgets every row (and so every tick); used when a new scan replaces the old one.</summary>
    private void ResetRows()
    {
        _rows.Clear();
        UpdateSelection();
    }
}
