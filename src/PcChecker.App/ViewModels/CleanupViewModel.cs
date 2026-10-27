using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.Debloat;
using PcChecker.Core.Optimization;
using PcChecker.Core.Settings;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Cleanup page: scan junk-file categories, tick the ones to remove, preview with a dry run, then clean with confirmation.
/// Selection is remembered in <see cref="CleanupSettings.EnabledCategoryIds"/>.
/// </summary>
public sealed partial class CleanupViewModel : ViewModelBase
{
    private readonly ITempFileScanner _scanner;
    private readonly ITempFileCleaner _cleaner;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly IDebloatService _debloat;
    private readonly INavigationService _navigation;
    private readonly ILogger<CleanupViewModel> _logger;
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(400);
    private bool _loadingSelection;
    private CleanupScanResult? _lastScan;
    private CancellationTokenSource? _saveDebounce;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CleanButtonText))]
    [NotifyPropertyChangedFor(nameof(DryRunHint))]
    private bool _dryRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReclaimableText))]
    private long _reclaimableBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalFilesText))]
    private int _totalFiles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedText))]
    [NotifyPropertyChangedFor(nameof(SelectedSizeText))]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSizeText))]
    private long _selectedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanned))]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    private string? _lastScanText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanNote))]
    private string? _scanNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunResult))]
    private string? _runSummary;

    [ObservableProperty]
    private InfoBarSeverity _runSeverity = InfoBarSeverity.Success;

    [ObservableProperty]
    private string _runTitle = "Cleanup finished";

    /// <summary>Set after a run in which File Explorer kept icon or thumbnail cache files open.</summary>
    [ObservableProperty]
    private bool _isRestartExplorerSuggested;

    [ObservableProperty]
    private CleanupGroupFilter _selectedGroupFilter;

    public CleanupViewModel(ISettingsService settings, ITempFileScanner scanner, ITempFileCleaner cleaner,
        IDialogService dialogs, INotificationService notifications, IDebloatService debloat, INavigationService navigation,
        ILogger<CleanupViewModel> logger)
        : base(settings)
    {
        _scanner = scanner;
        _cleaner = cleaner;
        _dialogs = dialogs;
        _notifications = notifications;
        _debloat = debloat;
        _navigation = navigation;
        _logger = logger;

        GroupFilters =
        [
            new(null, Loc.T("All categories")),
            new(CleanupGroup.Windows, Loc.T("Windows")),
            new(CleanupGroup.Graphics, Loc.T("Graphics drivers (AMD, NVIDIA, Intel)")),
            new(CleanupGroup.Browsers, Loc.T("Browsers")),
            new(CleanupGroup.Apps, Loc.T("Apps")),
            new(CleanupGroup.Games, Loc.T("Game launchers")),
            new(CleanupGroup.Developer, Loc.T("Developer tools")),
            new(CleanupGroup.Privacy, Loc.T("Privacy")),
        ];
        _selectedGroupFilter = GroupFilters[0];

        var selected = new HashSet<string>(
            CleanupRules.ResolveSelection(settings.Current.Cleanup.EnabledCategoryIds, scanner.Categories, IsAdvancedMode),
            StringComparer.OrdinalIgnoreCase);
        Categories = new ObservableCollection<CleanupCategoryItem>(
            scanner.Categories.Select(c => new CleanupCategoryItem(c, selected.Contains(c.Id), OnItemSelectionChanged)));
        ApplyVisibility();
        RecalculateTotals();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsAdvancedMode))
            {
                ApplyVisibility();
                RecalculateTotals();
            }
        };
    }

    public ObservableCollection<CleanupCategoryItem> Categories { get; }

    public ObservableCollection<string> SkippedPaths { get; } = [];

    public ObservableCollection<string> ErrorPaths { get; } = [];

    public string ReclaimableText => ByteSize.Format(ReclaimableBytes);

    public string TotalFilesText => TotalFiles.ToString("N0");

    public string SelectedText => $"{SelectedCount} of {Categories.Count(c => c.IsVisible)}";

    public string SelectedSizeText => SelectedCount == 0 ? "Nothing selected" : ByteSize.Format(SelectedBytes) + " selected";

    public bool HasScanned => !string.IsNullOrEmpty(LastScanText);

    public bool HasRunResult => !string.IsNullOrEmpty(RunSummary);

    public bool HasRunDetails => SkippedPaths.Count > 0 || ErrorPaths.Count > 0;

    public bool HasSkippedPaths => SkippedPaths.Count > 0;

    public bool HasErrorPaths => ErrorPaths.Count > 0;

    public bool HasScanNote => !string.IsNullOrEmpty(ScanNote);

    public string CleanButtonText => DryRun ? "Preview cleanup" : "Clean selected";

    public string DryRunHint => DryRun
        ? "Dry run is on: nothing will be deleted, you only get a report."
        : "Dry run is off: cleaning really deletes files.";

    public int SkipNewerThanMinutes => Settings.Current.Cleanup.SkipFilesNewerThanMinutes;

    public override Task InitializeAsync(CancellationToken cancellationToken) => ScanAsync();

    public override Task RefreshAsync(CancellationToken cancellationToken) => ScanAsync();

    [RelayCommand]
    private Task ScanAsync() => RunBusyAsync(async ct =>
    {
        RunSummary = null;
        foreach (var item in Categories)
        {
            item.Reset();
        }

        var progress = new Progress<ProgressUpdate>(u => BusyText = FormatProgress("Scanning", u));
        // Every category the mode allows, whatever the group filter shows, so switching the filter needs no new scan.
        var ids = Categories.Where(c => c.IsAvailable).Select(c => c.Id).ToList();
        var result = await _scanner.ScanAsync(ids, progress, ct);
        ApplyScan(result);
    }, "Scanning for junk files…");

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var targets = Categories.Where(c => c.IsVisible && c.IsSelected && c.IsScanned).ToList();
        if (_lastScan is null || targets.Count == 0)
        {
            return;
        }

        long bytes = targets.Sum(t => t.TotalBytes);
        int files = targets.Sum(t => t.FileCount);
        if (!DryRun)
        {
            var message = $"{targets.Count} categories, about {ByteSize.Format(bytes)} in {files:N0} files, will be deleted permanently. "
                + "They do not go to the Recycle Bin.\n\n"
                + $"Files changed in the last {SkipNewerThanMinutes} minutes and files a program is still using are left alone. "
                + "Windows and your programs recreate temporary files when they need them.";
            if (targets.Any(t => t.Risk != RiskLevel.Safe))
            {
                message += "\n\nSome of the selected categories are marked Moderate or Advanced; read their descriptions before continuing.";
            }

            var tools = targets.Where(t => t.IsCommand).Select(t => t.Name).ToList();
            if (tools.Count > 0)
            {
                message += "\n\n" + Loc.F("Windows' own tools clean these: {0}. DISM can take up to 30 minutes.", string.Join(", ", tools));
            }

            foreach (var warning in targets.Select(t => t.Category.ConfirmWarning).Where(w => !string.IsNullOrWhiteSpace(w)).Distinct())
            {
                message += "\n\n" + warning;
            }

            if (!await _dialogs.ConfirmAsync("Delete these files?", message, "Delete files", isDanger: true))
            {
                return;
            }
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(u => BusyText = FormatProgress(DryRun ? "Checking" : "Cleaning", u));
            var result = await _cleaner.CleanAsync(_lastScan, targets.Select(t => t.Id), DryRun, progress, ct);
            ShowRunResult(result);
            if (!result.DryRun)
            {
                _notifications.Show("Cleanup finished", CleanupRules.Summarize(result), result.Errors.Count > 0 ? NotificationKind.Warning : NotificationKind.Success);
                // Fresh numbers after a real clean.
                var rescan = await _scanner.ScanAsync(Categories.Where(c => c.IsAvailable).Select(c => c.Id), progress, ct);
                ApplyScan(rescan);
            }
        }, DryRun ? "Checking what would be removed…" : "Cleaning…");
    }

    private bool CanClean() => HasScanned && SelectedCount > 0;

    [RelayCommand]
    private async Task EmptyRecycleBinAsync()
    {
        if (!await _dialogs.ConfirmAsync("Empty the Recycle Bin?", "Everything in the Recycle Bin is deleted permanently. This cannot be undone.", "Empty Recycle Bin", isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _cleaner.EmptyRecycleBinAsync(ct);
            if (result.Success)
            {
                _notifications.Success("Recycle Bin", result.Message);
                var bin = Categories.FirstOrDefault(c => c.IsRecycleBin);
                if (bin is not null)
                {
                    bin.Apply(new CategoryScanResult(bin.Category, 0, 0, [], [CleanupRules.Note("The Recycle Bin is empty.")]));
                    RecalculateTotals();
                }
            }
            else
            {
                ErrorMessage = result.Message;
            }
        }, "Emptying the Recycle Bin…");
    }

    [RelayCommand]
    private void SelectRecommended()
    {
        _loadingSelection = true;
        var defaults = new HashSet<string>(CleanupCatalog.DefaultIds, StringComparer.OrdinalIgnoreCase);
        foreach (var item in Categories)
        {
            item.IsSelected = item.IsVisible && defaults.Contains(item.Id);
        }

        _loadingSelection = false;
        PersistSelection();
        RecalculateTotals();
    }

    [RelayCommand]
    private void SelectNone()
    {
        _loadingSelection = true;
        foreach (var item in Categories)
        {
            item.IsSelected = false;
        }

        _loadingSelection = false;
        PersistSelection();
        RecalculateTotals();
    }

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var result = ShellLauncher.Open(path);
        if (!result.Success)
        {
            _notifications.Warning("Could not open folder", result.Message);
        }
    }

    [RelayCommand]
    private void OpenStorageSettings() => Launch("ms-settings:storagesense");

    [RelayCommand]
    private void OpenDiskCleanup() => Launch("cleanmgr.exe");

    [RelayCommand]
    private void OpenTempFilesSettings() => Launch("ms-settings:storagepolicies");

    private void Launch(string target)
    {
        var result = ShellLauncher.Open(target);
        if (!result.Success)
        {
            _notifications.Warning("Could not open", result.Message);
        }
    }

    private void ApplyScan(CleanupScanResult scan)
    {
        _lastScan = scan;
        var byId = scan.Categories.ToDictionary(c => c.Category.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var item in Categories)
        {
            if (byId.TryGetValue(item.Id, out var result))
            {
                item.Apply(result);
            }
        }

        // Biggest categories first, like the scan result itself.
        var order = scan.Categories.Select((c, i) => (c.Category.Id, i)).ToDictionary(t => t.Id, t => t.i, StringComparer.OrdinalIgnoreCase);
        var sorted = Categories.OrderBy(c => order.TryGetValue(c.Id, out var i) ? i : int.MaxValue).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int current = Categories.IndexOf(sorted[i]);
            if (current != i)
            {
                Categories.Move(current, i);
            }
        }

        LastScanText = scan.Captured.ToLocalTime().ToString("t");
        var users = scan.Categories.SelectMany(c => c.Errors).Where(CleanupRules.IsNote).Select(CleanupRules.NoteText)
            .FirstOrDefault(n => n.StartsWith("Also scanned", StringComparison.OrdinalIgnoreCase));
        ScanNote = users is null
            ? null
            : "PC Checker is running as administrator, so other accounts' temporary files were included. " + users;
        RecalculateTotals();
    }

    private void ShowRunResult(CleanupRunResult result)
    {
        SkippedPaths.Clear();
        ErrorPaths.Clear();
        foreach (var s in result.Skipped)
        {
            SkippedPaths.Add(s);
        }

        foreach (var e in result.Errors)
        {
            ErrorPaths.Add(e);
        }

        RunTitle = result.DryRun ? "Dry run finished" : "Cleanup finished";
        RunSeverity = result.Errors.Count > 0 ? InfoBarSeverity.Warning : result.DryRun ? InfoBarSeverity.Informational : InfoBarSeverity.Success;
        RunSummary = result.Messages.Count == 0
            ? CleanupRules.Summarize(result)
            : CleanupRules.Summarize(result) + " " + string.Join(" ", result.Messages);
        IsRestartExplorerSuggested = !result.DryRun && CleanupRules.ExplorerHeldFiles(result.Skipped) > 0;
        OnPropertyChanged(nameof(HasRunDetails));
        OnPropertyChanged(nameof(HasSkippedPaths));
        OnPropertyChanged(nameof(HasErrorPaths));
        _logger.LogInformation("Cleanup run ({Mode}): {Summary}", result.DryRun ? "dry run" : "real", RunSummary);
    }

    private void OnItemSelectionChanged(CleanupCategoryItem item)
    {
        if (_loadingSelection)
        {
            return;
        }

        PersistSelection();
        RecalculateTotals();
    }

    private void PersistSelection()
    {
        Settings.Current.Cleanup.EnabledCategoryIds = Categories.Where(c => c.IsSelected).Select(c => c.Id).ToList();

        // Ticking several boxes in a row should produce one disk write and one Changed event, not one per click.
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        var debounce = new CancellationTokenSource();
        _saveDebounce = debounce;
        _ = SaveSettingsAsync(debounce.Token);
    }

    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SaveDebounceDelay, cancellationToken);
            await Settings.SaveAsync();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection change; that one saves.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup selection could not be saved.");
        }
    }

    private void ApplyVisibility()
    {
        var group = SelectedGroupFilter?.Group;
        foreach (var item in Categories)
        {
            item.IsAvailable = !item.IsAdvanced || IsAdvancedMode;
            item.IsVisible = item.IsAvailable && (group is null || item.Group == group);
        }

        OnPropertyChanged(nameof(SelectedText));
    }

    partial void OnSelectedGroupFilterChanged(CleanupGroupFilter value)
    {
        ApplyVisibility();
        RecalculateTotals();
    }

    /// <summary>The group filter above the category list.</summary>
    public IReadOnlyList<CleanupGroupFilter> GroupFilters { get; }

    [RelayCommand]
    private async Task RestartExplorerAsync()
    {
        if (!await _dialogs.ConfirmAsync(Loc.T("Restart File Explorer?"),
                Loc.T("The taskbar and open File Explorer windows close for a moment and come back. This releases most icon and thumbnail cache files; scan and clean again afterwards."),
                Loc.T("Restart Explorer")))
        {
            return;
        }

        OperationResult? result = null;
        await RunBusyAsync(async ct => result = await _debloat.RestartExplorerAsync(ct), Loc.T("Restarting File Explorer…"), cancellable: false);
        if (result is null)
        {
            return;
        }

        IsRestartExplorerSuggested = false;
        if (result.Success)
        {
            _notifications.Success(Loc.T("File Explorer restarted"), Loc.T("Scan and clean again to remove the icon and thumbnail caches. Anything still in use is cleared after you sign out and back in."));
        }
        else
        {
            _notifications.Warning(Loc.T("File Explorer"), result.Message);
        }
    }

    [RelayCommand]
    private void DismissRestartExplorer() => IsRestartExplorerSuggested = false;

    [RelayCommand]
    private void OpenSystemProtection() => Launch(System.IO.Path.Combine(Environment.SystemDirectory, "SystemPropertiesProtection.exe"));

    [RelayCommand]
    private void OpenStorageRecommendations() => Launch("ms-settings:storagerecommendations");

    [RelayCommand]
    private void OpenDiagnosticDataSettings() => Launch("ms-settings:privacy-feedback");

    [RelayCommand]
    private void GoToOldDrivers() => _navigation.Navigate(typeof(DriverCleanupPage));

    [RelayCommand]
    private void GoToOptimize() => _navigation.Navigate(typeof(OptimizePage));

    private void RecalculateTotals()
    {
        var visible = Categories.Where(c => c.IsVisible).ToList();
        ReclaimableBytes = visible.Sum(c => c.TotalBytes);
        TotalFiles = visible.Sum(c => c.FileCount);
        var selected = visible.Where(c => c.IsSelected).ToList();
        SelectedCount = selected.Count;
        SelectedBytes = selected.Sum(c => c.TotalBytes);
        OnPropertyChanged(nameof(SelectedText));
    }

    private static string FormatProgress(string verb, ProgressUpdate update)
    {
        var text = $"{verb}: {update.Stage}";
        if (update.Percent is { } p)
        {
            text += $" ({p:0}%)";
        }

        if (!string.IsNullOrWhiteSpace(update.Detail))
        {
            text += " — " + update.Detail;
        }

        return text;
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Cleanup page operation failed.");
}
