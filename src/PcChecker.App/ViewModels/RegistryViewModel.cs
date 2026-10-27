using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Optimization;
using PcChecker.Core.RegistryHealth;
using PcChecker.Core.Settings;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Registry page: pick categories, scan (read-only), tick the safe leftovers and remove them with a backup and a restore
/// point first; restore or delete backups. Never scans on its own: the first visit shows the categories and backups.
/// </summary>
public sealed partial class RegistryViewModel : ViewModelBase
{
    private readonly IRegistryHealthService _service;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<RegistryViewModel> _logger;
    private RegistryScanResult? _lastScan;
    private IReadOnlyList<RegistryIssueCategory> _lastScannedCategories = [];
    private bool _bulkSelecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanned))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowNothingFound))]
    private string? _lastScanText;

    [ObservableProperty]
    private string _issueCountText = "—";

    [ObservableProperty]
    private string _safeToFixText = "—";

    [ObservableProperty]
    private string _reportOnlyText = "—";

    [ObservableProperty]
    private string _keysScannedText = "—";

    [ObservableProperty]
    private string _scanDurationText = "Not scanned yet";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanSummary))]
    private string? _scanSummary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedText))]
    [NotifyCanExecuteChangedFor(nameof(FixSelectedCommand))]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNothingFound))]
    [NotifyPropertyChangedFor(nameof(HasIssues))]
    private int _visibleIssueCount;

    [ObservableProperty]
    private string? _filterText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixResult))]
    private string? _fixMessage;

    [ObservableProperty]
    private string _fixTitle = "Registry fix finished";

    [ObservableProperty]
    private InfoBarSeverity _fixSeverity = InfoBarSeverity.Success;

    public RegistryViewModel(ISettingsService settings, IRegistryHealthService service, IDialogService dialogs,
        INotificationService notifications, ILogger<RegistryViewModel> logger)
        : base(settings)
    {
        _service = service;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;

        var supported = new HashSet<RegistryIssueCategory>(service.Categories);
        Categories = new ObservableCollection<RegistryCategoryItem>(
            RegistryIssueRules.All.Where(i => supported.Contains(i.Category))
                .Select(i => new RegistryCategoryItem(i, isSelected: true, OnCategorySelectionChanged)));
        ApplyModeVisibility();

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsAdvancedMode))
            {
                ApplyModeVisibility();
                RebuildGroups();
            }
            else if (e.PropertyName == nameof(FilterText))
            {
                ApplyFilter();
            }
        };
    }

    public ObservableCollection<RegistryCategoryItem> Categories { get; }

    public ObservableCollection<RegistryIssueGroup> Groups { get; } = [];

    public ObservableCollection<string> ScanWarnings { get; } = [];

    public ObservableCollection<string> FixDetails { get; } = [];

    public bool HasScanned => !string.IsNullOrEmpty(LastScanText);

    public bool HasIssues => VisibleIssueCount > 0;

    public bool ShowEmptyState => !HasScanned;

    public bool ShowNothingFound => HasScanned && VisibleIssueCount == 0;

    public bool HasScanSummary => !string.IsNullOrEmpty(ScanSummary);

    public bool HasScanWarnings => ScanWarnings.Count > 0;

    public bool HasFixResult => !string.IsNullOrEmpty(FixMessage);

    public bool HasFixDetails => FixDetails.Count > 0;

    public string SelectedText => SelectedCount switch
    {
        0 => "Nothing selected",
        1 => "1 entry selected",
        var n => $"{n} entries selected",
    };

    public string CategorySelectionText
    {
        get
        {
            var visible = Categories.Where(c => c.IsVisible).ToList();
            return $"{visible.Count(c => c.IsSelected)} of {visible.Count} categories selected";
        }
    }

    /// <summary>Shows the categories, the last scan of this session (if any) and the backups. Never scans by itself.</summary>
    public override Task InitializeAsync(CancellationToken cancellationToken) => RunBusyAsync(async ct =>
    {
        if (_lastScan is not null)
        {
            ApplyScan(_lastScan, _lastScannedCategories);
        }

        await LoadBackupsAsync(ct);
    }, "Loading registry backups…");

    public override Task RefreshAsync(CancellationToken cancellationToken) => ScanAsync();

    [RelayCommand]
    private async Task ScanAsync()
    {
        var categories = Categories.Where(c => c.IsVisible && c.IsSelected).Select(c => c.Category).ToList();
        if (categories.Count == 0)
        {
            _notifications.Warning("Nothing to check", "Tick at least one category first.");
            return;
        }

        await RunBusyAsync(async ct =>
        {
            FixMessage = null;
            FixDetails.Clear();
            OnPropertyChanged(nameof(HasFixDetails));
            var progress = new Progress<ProgressUpdate>(u => BusyText = FormatProgress("Checking", u));
            var result = await _service.ScanAsync(categories, progress, ct);
            ApplyScan(result, categories);
        }, "Checking the registry (read only)…");
    }

    [RelayCommand]
    private void SelectAllSafe() => SetSelection(item => item.CanSelect && item.Risk == RiskLevel.Safe);

    [RelayCommand]
    private void SelectAllFixable() => SetSelection(item => item.CanSelect);

    [RelayCommand]
    private void ClearSelection() => SetSelection(_ => false);

    [RelayCommand]
    private void SelectAllCategories()
    {
        foreach (var c in Categories.Where(c => c.IsVisible))
        {
            c.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearCategories()
    {
        foreach (var c in Categories)
        {
            c.IsSelected = false;
        }
    }

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var g in Groups)
        {
            g.IsExpanded = true;
        }
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var g in Groups)
        {
            g.IsExpanded = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanFix))]
    private async Task FixSelectedAsync()
    {
        var selected = Groups.SelectMany(g => g.Items).Where(i => i.IsVisible && i.IsSelected && i.CanSelect).Select(i => i.Issue).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        string what = RegistryIssueRules.Summarize(selected);
        string message =
            $"{selected.Count} leftover {(selected.Count == 1 ? "entry" : "entries")} will be removed ({what}).\n\n" +
            "A backup and a restore point are created first; you can undo from Backups on this page.\n\n" +
            "Each entry is checked again right before it is removed, and anything that changed since the scan is left alone.";
        if (selected.Any(s => s.Risk != RiskLevel.Safe))
        {
            message += "\n\nSome selected entries are Moderate or Advanced. They are still leftovers, but read them once before continuing.";
        }

        if (!await _dialogs.ConfirmAsync("Remove these registry leftovers?", message, "Back up and remove", isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(u => BusyText = FormatProgress("Fixing", u));
            var result = await _service.FixAsync(selected, progress, ct);
            ShowFixResult(result);
            await LoadBackupsAsync(ct);
            if (result.Fixed > 0 && _lastScannedCategories.Count > 0)
            {
                BusyText = "Checking again…";
                var rescan = await _service.ScanAsync(_lastScannedCategories, null, ct);
                ApplyScan(rescan, _lastScannedCategories);
            }
        }, "Backing up and removing…", cancellable: false);
    }

    private bool CanFix() => SelectedCount > 0;

    [RelayCommand]
    private void CopyKeyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Clipboard.SetText(path);
            _notifications.Show("Copied", "The registry path is on the clipboard. Paste it into Registry Editor's address bar to look at it.", NotificationKind.Info, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
            _notifications.Warning("Could not copy", "The clipboard is busy; try again.");
        }
    }

    [RelayCommand]
    private void OpenRegistryEditor()
    {
        var result = ShellLauncher.Open("regedit.exe");
        if (!result.Success)
        {
            _notifications.Warning("Could not open Registry Editor", result.Message);
        }
    }

    [RelayCommand]
    private void OpenInstalledApps()
    {
        var result = ShellLauncher.Open("ms-settings:appsfeatures");
        if (!result.Success)
        {
            _notifications.Warning("Could not open Installed apps", result.Message);
        }
    }

    private void SetSelection(Func<RegistryIssueItem, bool> predicate)
    {
        _bulkSelecting = true;
        try
        {
            foreach (var item in Groups.Where(g => g.IsVisible).SelectMany(g => g.Items))
            {
                item.IsSelected = item.IsVisible && item.CanSelect && predicate(item);
            }
        }
        finally
        {
            _bulkSelecting = false;
        }

        RecountSelection();
    }

    private void ApplyScan(RegistryScanResult scan, IReadOnlyList<RegistryIssueCategory> categories)
    {
        _lastScan = scan;
        _lastScannedCategories = categories;
        foreach (var c in Categories)
        {
            c.FoundCount = scan.CountsByCategory.TryGetValue(c.Category, out var n) ? n : null;
        }

        ScanWarnings.Clear();
        foreach (var w in scan.Warnings)
        {
            ScanWarnings.Add(w);
        }

        OnPropertyChanged(nameof(HasScanWarnings));
        KeysScannedText = scan.KeysScanned.ToString("N0");
        ScanDurationText = $"in {scan.Elapsed.TotalSeconds:0.0} s";
        RebuildGroups();
        LastScanText = scan.Captured.ToLocalTime().ToString("t");
        _logger.LogInformation("Registry scan shown: {Count} leftover(s), {Keys} keys.", scan.Issues.Count, scan.KeysScanned);
    }

    /// <summary>Builds the result groups from the last scan, honouring advanced mode and the search box.</summary>
    private void RebuildGroups()
    {
        Groups.Clear();
        if (_lastScan is null)
        {
            RecountTotals();
            return;
        }

        foreach (var group in RegistryIssueRules.All)
        {
            var items = _lastScan.Issues
                .Where(i => i.Category == group.Category && (IsAdvancedMode || !i.AdvancedOnly))
                .Select(i => new RegistryIssueItem(i, OnIssueSelectionChanged))
                .ToList();
            if (items.Count == 0)
            {
                continue;
            }

            Groups.Add(new RegistryIssueGroup(group, items) { IsExpanded = Groups.Count < 2 });
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        foreach (var group in Groups)
        {
            int visible = 0;
            foreach (var item in group.Items)
            {
                item.IsVisible = item.Matches(FilterText);
                if (!item.IsVisible)
                {
                    item.IsSelected = false;
                }

                visible += item.IsVisible ? 1 : 0;
            }

            group.VisibleCount = visible;
            group.IsVisible = visible > 0;
            group.NotifySelection();
        }

        RecountTotals();
    }

    private void RecountTotals()
    {
        var visible = Groups.SelectMany(g => g.Items).Where(i => i.IsVisible).ToList();
        VisibleIssueCount = visible.Count;
        if (_lastScan is null)
        {
            IssueCountText = SafeToFixText = ReportOnlyText = "—";
            ScanSummary = null;
        }
        else
        {
            IssueCountText = visible.Count.ToString("N0");
            SafeToFixText = visible.Count(i => i.CanSelect).ToString("N0");
            ReportOnlyText = visible.Count(i => !i.CanSelect).ToString("N0");
            var shown = _lastScan with { Issues = _lastScan.Issues.Where(i => IsAdvancedMode || !i.AdvancedOnly).ToList() };
            ScanSummary = RegistryIssueRules.DescribeScan(shown);
        }

        RecountSelection();
    }

    private void RecountSelection()
    {
        SelectedCount = Groups.SelectMany(g => g.Items).Count(i => i.IsVisible && i.IsSelected);
        foreach (var g in Groups)
        {
            g.NotifySelection();
        }
    }

    private void OnIssueSelectionChanged(RegistryIssueItem item)
    {
        if (!_bulkSelecting)
        {
            RecountSelection();
        }
    }

    private void OnCategorySelectionChanged(RegistryCategoryItem item) => OnPropertyChanged(nameof(CategorySelectionText));

    private void ApplyModeVisibility()
    {
        foreach (var c in Categories)
        {
            c.IsVisible = !c.IsAdvancedOnly || IsAdvancedMode;
        }

        OnPropertyChanged(nameof(CategorySelectionText));
    }

    private void ShowFixResult(RegistryFixResult result)
    {
        FixDetails.Clear();
        foreach (var m in result.Messages.Skip(1))
        {
            FixDetails.Add(m);
        }

        OnPropertyChanged(nameof(HasFixDetails));
        FixTitle = result.Fixed > 0 ? "Leftovers removed" : "Nothing was removed";
        FixSeverity = result.Failed > 0 ? InfoBarSeverity.Warning
            : result.Fixed == 0 ? InfoBarSeverity.Informational
            : InfoBarSeverity.Success;
        FixMessage = result.Messages.FirstOrDefault() ?? RegistryIssueRules.DescribeFix(result);
        _logger.LogInformation("Registry fix: {Fixed} fixed, {Skipped} skipped, {Failed} failed, backup {Backup}.", result.Fixed, result.Skipped, result.Failed, result.BackupId ?? "none");
        if (result.Fixed > 0)
        {
            _notifications.Success("Registry leftovers removed", RegistryIssueRules.DescribeFix(result));
        }
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

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Registry page operation failed.");
}
