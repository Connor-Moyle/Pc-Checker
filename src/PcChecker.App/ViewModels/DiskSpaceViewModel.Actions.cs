using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;

namespace PcChecker.App.ViewModels;

/// <summary>Duplicates, selection, moving to the Recycle Bin, and the per-item Explorer and copy actions.</summary>
public sealed partial class DiskSpaceViewModel
{
    /// <summary>Duplicate groups added to the page at a time (most wasted space first); Show more adds the next ones.</summary>
    private const int DuplicatePageSize = 40;

    private DuplicateScanResult? _duplicates;
    private bool _bulkSelecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDuplicatesEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowNoDuplicates))]
    private bool _duplicatesSearched;

    [ObservableProperty]
    private string _duplicatesSummary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecycleSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))]
    private int _selectedCount;

    [ObservableProperty]
    private string _selectedText = Loc.T("Nothing selected");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreDuplicates))]
    private string? _moreDuplicatesText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private string? _resultMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private bool _resultSuccess;

    public ObservableCollection<DiskDuplicateGroupRow> DuplicateGroups { get; } = [];

    /// <summary>One line per item of the last removal.</summary>
    public ObservableCollection<string> ResultLines { get; } = [];

    public bool HasDuplicates => DuplicateGroups.Count > 0;

    public bool HasMoreDuplicates => !string.IsNullOrEmpty(MoreDuplicatesText);

    public bool ShowDuplicatesEmpty => !DuplicatesSearched;

    public bool ShowNoDuplicates => DuplicatesSearched && DuplicateGroups.Count == 0;

    public bool HasResultLines => ResultLines.Count > 0;

    public bool ShowSuccessResult => ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    public bool ShowFailureResult => !ResultSuccess && !string.IsNullOrWhiteSpace(ResultMessage);

    [RelayCommand]
    private Task FindDuplicatesAsync() => RunBusyAsync(async ct =>
    {
        if (_scan is not { } scan)
        {
            return;
        }

        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : "\n" + p.Detail));
        long minBytes = ScanOptions().DuplicateMinBytes;
        var candidates = await Task.Run(() => DuplicateFinder.ActionableCandidates(scan.DuplicateCandidates, minBytes, IsProtected), ct);
        var result = await _service.FindDuplicatesAsync(candidates, minBytes, progress, ct);
        ClearDuplicates();
        ApplyDuplicates(result with { Groups = result.Groups.Where(g => g.Files.Any(f => !IsProtected(f.Path))).ToList() });
        SelectedTabIndex = DuplicatesTabIndex;
    }, Loc.T("Looking for duplicate files…"));

    /// <summary>Adds the next page of duplicate groups.</summary>
    [RelayCommand]
    private void ShowMoreDuplicates()
    {
        if (_duplicates is { } duplicates)
        {
            ShowDuplicateGroups(duplicates.Groups, DuplicateGroups.Count + DuplicatePageSize);
        }
    }

    /// <summary>Ticks every copy except the newest in each group.</summary>
    [RelayCommand]
    private void KeepNewest() => SelectDuplicates(g => DuplicateSelection.KeepNewest(g, IsProtected));

    /// <summary>Ticks every copy except the oldest in each group.</summary>
    [RelayCommand]
    private void KeepOldest() => SelectDuplicates(g => DuplicateSelection.KeepOldest(g, IsProtected));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ClearSelection() => ChangeSelection(() =>
    {
        foreach (var row in _rows.Values.Where(r => r.IsSelected).ToList())
        {
            row.IsSelected = false;
        }
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RecycleSelectedAsync()
    {
        var plan = DiskSpaceRules.CollapseNested(_rows.Values.Where(r => r.IsSelected && r.CanSelect), r => r.Path)
            .Select(r => new RecycleRequest(r.Path, r.IsFolder, r.SizeBytes))
            .ToList();
        if (plan.Count == 0)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("Move to the Recycle Bin?"), ConfirmationText(plan), Loc.T("Move to Recycle Bin"), isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : "\n" + p.Detail));
            var result = await _service.RecycleAsync(plan, OwnerWindow(), progress, ct);
            ShowResult(result);
            if (result.Value is { } value && value.Recycled > 0 && _scan is { } scan)
            {
                var removed = value.Items.Where(i => i.Success).Select(i => i.Request).ToList();
                var gone = DiskSpaceRules.RemovedFilter(removed.Select(r => r.Path));
                _scan = DiskSpaceRules.WithoutRemoved(scan, removed);
                foreach (var path in _rows.Keys.Where(gone).ToList())
                {
                    _rows.Remove(path);
                }

                RemoveFromDuplicates(removed);
                UpdateSummary(stale: true);
                NavigateTo(NearestRemaining(_scan.Root, CurrentFolder?.FullPath));
                FillFileLists();
                UpdateSelection();
                await LoadDrivesAsync(CancellationToken.None);
            }
        }, Loc.T("Moving to the Recycle Bin…"), cancellable: false);
    }

    [RelayCommand]
    private void ShowInExplorer(object? item)
    {
        var (path, isFolder) = PathOf(item);
        if (path is null)
        {
            return;
        }

        var result = _service.ShowInExplorer(path, isFolder);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
        else if (AppOptions.Current.IsDemo)
        {
            _notifications.Info(Loc.T("Demo mode"), result.Message);
        }
    }

    [RelayCommand]
    private void CopyPath(object? item)
    {
        var (path, _) = PathOf(item);
        if (path is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(path);
            _notifications.Success(Loc.T("Copied"), path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
            ErrorMessage = Loc.T("The clipboard is busy; try again in a moment.");
        }
    }

    [RelayCommand]
    private void ShowCurrentFolderInExplorer() => ShowInExplorer(CurrentFolder);

    [RelayCommand]
    private void CopyCurrentFolderPath() => CopyPath(CurrentFolder);

    private const int DuplicatesTabIndex = 4;

    private bool HasSelection() => SelectedCount > 0;

    /// <summary>The folder at <paramref name="path"/>, or its closest parent that is still in the tree.</summary>
    private static FolderNode NearestRemaining(FolderNode root, string? path)
    {
        for (var p = path; p is not null; p = DiskSpacePaths.ParentOf(p))
        {
            if (DiskSpaceAggregation.Find(root, p) is { } node)
            {
                return node;
            }
        }

        return root;
    }

    private bool IsProtected(string path) => _service.WhyProtected(path) is not null;

    /// <summary>The main window's handle, so a prompt from Windows (item too big for the Recycle Bin) opens in front of it.</summary>
    private static nint OwnerWindow()
    {
        try
        {
            return Application.Current?.MainWindow is { } window ? new WindowInteropHelper(window).Handle : 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static (string? Path, bool IsFolder) PathOf(object? item) => item switch
    {
        DiskItemRow row => (row.Path, row.IsFolder),
        FolderNode node => (node.FullPath, true),
        DiskBreadcrumb crumb => (crumb.Node.FullPath, true),
        _ => (null, false),
    };

    private void ApplyDuplicates(DuplicateScanResult result)
    {
        // Keep at least as many groups on screen as before (after a removal), so the list does not jump back.
        int shown = Math.Max(DuplicatePageSize, DuplicateGroups.Count);
        _duplicates = result;
        ShowDuplicateGroups(result.Groups, shown);

        var summary = result.Groups.Count == 0
            ? Loc.F("No identical files of {0} or more.", ByteSize.Format(ScanOptions().DuplicateMinBytes))
            : DiskSpaceText.Count(result.Groups.Count,
                Loc.T("{0} group of identical files"), Loc.T("{0} groups of identical files"))
              + " · " + Loc.F("{0} could be freed by keeping one copy of each.", ByteSize.Format(result.WastedBytes));
        if (result.FilesUnreadable > 0)
        {
            summary += " " + DiskSpaceText.Count(result.FilesUnreadable,
                Loc.T("{0} file was in use or unreadable and left out."), Loc.T("{0} files were in use or unreadable and left out."));
        }

        DuplicatesSummary = summary;
        DuplicatesSearched = true;
        OnPropertyChanged(nameof(ShowNoDuplicates));
    }

    /// <summary>
    /// Shows the first <paramref name="count"/> groups. Each group is a card with its file rows, so they are added a page
    /// at a time rather than all at once after a large scan.
    /// </summary>
    private void ShowDuplicateGroups(IReadOnlyList<DuplicateGroup> groups, int count)
    {
        count = Math.Min(count, groups.Count);
        if (DuplicateGroups.Count > count || DuplicateGroups.Where((row, i) => !ReferenceEquals(row.Group, groups[i])).Any())
        {
            DuplicateGroups.Clear();
        }

        for (int i = DuplicateGroups.Count; i < count; i++)
        {
            DuplicateGroups.Add(new DiskDuplicateGroupRow(groups[i], groups[i].Files.Select(FileRow).ToList()));
        }

        int hidden = groups.Count - count;
        MoreDuplicatesText = hidden > 0
            ? DiskSpaceText.Count(hidden, Loc.T("Show {0} more group"), Loc.T("Show {0} more groups"))
              + $" ({ByteSize.Format(groups.Skip(count).Sum(g => g.WastedBytes))})"
            : null;
        OnPropertyChanged(nameof(HasDuplicates));
    }

    private void ClearDuplicates()
    {
        _duplicates = null;
        DuplicateGroups.Clear();
        MoreDuplicatesText = null;
        DuplicatesSummary = string.Empty;
        DuplicatesSearched = false;
        OnPropertyChanged(nameof(HasDuplicates));
    }

    private void RemoveFromDuplicates(IReadOnlyList<RecycleRequest> removed)
    {
        if (_duplicates is not { } duplicates)
        {
            return;
        }

        var gone = DiskSpaceRules.RemovedFilter(removed.Select(r => r.Path));
        var remaining = duplicates.Groups
            .Select(g => g with { Files = g.Files.Where(f => !gone(f.Path)).ToList() })
            .Where(g => g.Files.Count > 1)
            .ToList();
        ApplyDuplicates(duplicates with { Groups = remaining });
    }

    private void SelectDuplicates(Func<DuplicateGroup, IReadOnlyList<DiskFileEntry>> pick) => ChangeSelection(() =>
    {
        foreach (var group in DuplicateGroups)
        {
            var chosen = new HashSet<string>(pick(group.Group).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var row in group.Files)
            {
                row.IsSelected = row.CanSelect && chosen.Contains(row.Path);
            }
        }
    });

    /// <summary>Ticks or unticks many rows and recounts the selection once at the end.</summary>
    private void ChangeSelection(Action change)
    {
        _bulkSelecting = true;
        try
        {
            change();
        }
        finally
        {
            _bulkSelecting = false;
            UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        if (_bulkSelecting)
        {
            return;
        }

        var selected = DiskSpaceRules.CollapseNested(_rows.Values.Where(r => r.IsSelected), r => r.Path);
        SelectedCount = selected.Count;
        SelectedText = selected.Count == 0
            ? Loc.T("Nothing selected")
            : DiskSpaceText.Count(selected.Count, Loc.T("{0} item selected"), Loc.T("{0} items selected"))
              + " · " + ByteSize.Format(selected.Sum(r => r.SizeBytes));
    }

    private static string ConfirmationText(IReadOnlyList<RecycleRequest> plan)
    {
        const int shown = 8;
        long bytes = plan.Sum(p => p.SizeBytes);
        var text = new StringBuilder();
        text.Append(Loc.F("{0} ({1}) will be moved to the Recycle Bin:", DiskSpaceText.Items(plan.Count), ByteSize.Format(bytes))).Append("\n\n");
        foreach (var item in plan.OrderByDescending(p => p.SizeBytes).Take(shown))
        {
            text.Append($"• {DiskSpaceFormat.ShortenPath(item.Path, 70)} ({ByteSize.Format(item.SizeBytes)})");
            text.Append(item.IsFolder ? " — " + Loc.T("whole folder") : string.Empty).Append('\n');
        }

        if (plan.Count > shown)
        {
            text.Append("• ").Append(Loc.F("and {0} more", plan.Count - shown)).Append('\n');
        }

        if (plan.Any(p => FileTypeClassifier.ClassifyFolder(p.IsFolder ? p.Path : DiskSpacePaths.ParentOf(p.Path) ?? p.Path) == FolderKind.GameLibrary))
        {
            text.Append('\n').Append(Loc.T("Some items belong to an installed game. Uninstalling it from its launcher (Steam, Xbox, Epic…) is cleaner.")).Append('\n');
        }

        if (plan.Any(p => DiskSpaceRules.IsInCloudFolder(p.Path)))
        {
            text.Append('\n').Append(Loc.T("Some items are in OneDrive. Removing them here also removes them from OneDrive online.")).Append('\n');
        }

        text.Append('\n').Append(Loc.T("You can restore them from the Recycle Bin. The space is only freed when the Recycle Bin is emptied (Cleanup page or the Recycle Bin itself)."));
        return text.ToString();
    }

    private void ShowResult(OperationResult<RecycleResult> result)
    {
        ResultLines.Clear();
        if (result.Value is { } value)
        {
            foreach (var item in value.Items.Where(i => !i.Success))
            {
                ResultLines.Add($"{item.Request.Path}: {item.Message}");
            }
        }

        bool allOk = result.Success && (result.Value?.Failed ?? 0) == 0;
        ResultSuccess = allOk;
        ResultMessage = result.Message;
        if (allOk)
        {
            _notifications.Success(Loc.T("Moved to the Recycle Bin"), result.Message);
        }
        else if (result.Success)
        {
            _notifications.Warning(Loc.T("Some items were kept"), result.Message);
        }
        else
        {
            _notifications.Error(Loc.T("Nothing was moved"), result.Message.Split('\n')[0]);
        }
    }

    private void ClearResult()
    {
        ResultLines.Clear();
        ResultMessage = null;
    }
}
