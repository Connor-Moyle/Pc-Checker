using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;

namespace PcChecker.App.ViewModels;

/// <summary>Duplicates, selection, moving to the Recycle Bin, and the per-item Explorer and copy actions.</summary>
public sealed partial class DiskSpaceViewModel
{
    /// <summary>Duplicate groups shown at once (most wasted space first).</summary>
    private const int MaxDuplicateGroups = 300;

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
    private string _selectedText = "Nothing selected";

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
        ApplyDuplicates(result with { Groups = result.Groups.Where(g => g.Files.Any(f => !IsProtected(f.Path))).ToList() });
        SelectedTabIndex = DuplicatesTabIndex;
    }, "Looking for duplicate files…");

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

        if (!await _dialogs.ConfirmAsync("Move to the Recycle Bin?", ConfirmationText(plan), "Move to Recycle Bin", isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : "\n" + p.Detail));
            var result = await _service.RecycleAsync(plan, progress, ct);
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
        }, "Moving to the Recycle Bin…", cancellable: false);
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
            _notifications.Info("Demo mode", result.Message);
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
            _notifications.Success("Copied", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
            ErrorMessage = "The clipboard is busy; try again in a moment.";
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

    private static (string? Path, bool IsFolder) PathOf(object? item) => item switch
    {
        DiskItemRow row => (row.Path, row.IsFolder),
        FolderNode node => (node.FullPath, true),
        DiskBreadcrumb crumb => (crumb.Node.FullPath, true),
        _ => (null, false),
    };

    private void ApplyDuplicates(DuplicateScanResult result)
    {
        _duplicates = result;
        DuplicateGroups.Clear();
        foreach (var group in result.Groups.Take(MaxDuplicateGroups))
        {
            DuplicateGroups.Add(new DiskDuplicateGroupRow(group, group.Files.Select(FileRow).ToList()));
        }

        var summary = result.Groups.Count == 0
            ? $"No identical files of {ByteSize.Format(ScanOptions().DuplicateMinBytes)} or more."
            : $"{DiskSpaceFormat.Count(result.Groups.Count, "group", "groups")} of identical files · {ByteSize.Format(result.WastedBytes)} could be freed by keeping one copy of each.";
        if (result.Groups.Count > MaxDuplicateGroups)
        {
            summary += $" Showing the {MaxDuplicateGroups} that waste the most.";
        }

        if (result.FilesUnreadable > 0)
        {
            summary += $" {DiskSpaceFormat.Count(result.FilesUnreadable, "file was", "files were")} in use or unreadable and left out.";
        }

        DuplicatesSummary = summary;
        DuplicatesSearched = true;
        OnPropertyChanged(nameof(HasDuplicates));
        OnPropertyChanged(nameof(ShowNoDuplicates));
    }

    private void ClearDuplicates()
    {
        _duplicates = null;
        DuplicateGroups.Clear();
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
            ? "Nothing selected"
            : $"{DiskSpaceFormat.Count(selected.Count, "item", "items")} selected · {ByteSize.Format(selected.Sum(r => r.SizeBytes))}";
    }

    private static string ConfirmationText(IReadOnlyList<RecycleRequest> plan)
    {
        const int shown = 8;
        long bytes = plan.Sum(p => p.SizeBytes);
        var text = new StringBuilder();
        text.Append($"{DiskSpaceFormat.Count(plan.Count, "item", "items")} ({ByteSize.Format(bytes)}) will be moved to the Recycle Bin:\n\n");
        foreach (var item in plan.OrderByDescending(p => p.SizeBytes).Take(shown))
        {
            text.Append($"• {DiskSpaceFormat.ShortenPath(item.Path, 70)} ({ByteSize.Format(item.SizeBytes)}){(item.IsFolder ? " — whole folder" : string.Empty)}\n");
        }

        if (plan.Count > shown)
        {
            text.Append($"• and {plan.Count - shown} more\n");
        }

        if (plan.Any(p => FileTypeClassifier.ClassifyFolder(p.IsFolder ? p.Path : DiskSpacePaths.ParentOf(p.Path) ?? p.Path) == FolderKind.GameLibrary))
        {
            text.Append("\nSome items belong to an installed game. Uninstalling it from its launcher (Steam, Xbox, Epic…) is cleaner.\n");
        }

        if (plan.Any(p => DiskSpaceRules.IsInCloudFolder(p.Path)))
        {
            text.Append("\nSome items are in OneDrive. Removing them here also removes them from OneDrive online.\n");
        }

        text.Append("\nYou can restore them from the Recycle Bin. The space is only freed when the Recycle Bin is emptied (Cleanup page or the Recycle Bin itself).");
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
            _notifications.Success("Moved to the Recycle Bin", result.Message);
        }
        else if (result.Success)
        {
            _notifications.Warning("Some items were kept", result.Message);
        }
        else
        {
            _notifications.Error("Nothing was moved", result.Message.Split('\n')[0]);
        }
    }

    private void ClearResult()
    {
        ResultLines.Clear();
        ResultMessage = null;
    }
}
