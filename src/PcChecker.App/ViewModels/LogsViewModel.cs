using System.IO;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.ViewModels.Logs;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Logs page: a live, filterable view of the in-app log buffer, the logging settings form and the action journal.
/// New entries arrive on logging threads, are queued, and drained onto the UI thread by a 250 ms timer.
/// </summary>
public sealed partial class LogsViewModel : ViewModelBase
{
    private const int DrainBatch = 500;
    private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);

    private readonly ILogService _logService;
    private readonly IActionJournal _journal;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<LogsViewModel> _logger;
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly List<LogEntry> _all = new();
    private readonly DispatcherTimer _drainTimer;
    private readonly DispatcherTimer _searchTimer;
    private LogFilter _filter = LogFilter.None;
    private bool _subscribed;
    private bool _demoSeeded;

    [ObservableProperty]
    private LevelChoice _selectedLevel = LevelChoice.For(LogLevelSetting.Verbose);

    [ObservableProperty]
    private string _selectedCategory = AllCategories;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonText))]
    [NotifyPropertyChangedFor(nameof(IsLive))]
    private bool _isPaused;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedEntry))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedCommand))]
    private LogEntryRow? _selectedEntry;

    [ObservableProperty]
    private string _statusText = "No log entries yet.";

    [ObservableProperty]
    private string _currentFileText = "No log file written yet.";

    [ObservableProperty]
    private string? _currentFilePath;

    [ObservableProperty]
    private int _pendingWhilePaused;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _bufferedCount;

    public LogsViewModel(
        ISettingsService settings,
        ILogService logService,
        IActionJournal journal,
        IDialogService dialogs,
        INotificationService notifications,
        ILogger<LogsViewModel> logger)
        : base(settings)
    {
        _logService = logService;
        _journal = journal;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;

        _drainTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = DrainInterval };
        _drainTimer.Tick += (_, _) => Drain();
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SearchDebounce };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter();
        };

        CategoryOptions = [AllCategories, .. LogCategories.All];
        LogDirectory = logService.LogDirectory;
        InitializeSettingsForm();
        // Reset settings and a setup import replace the whole Logging section; show what is saved now, so a later Save
        // cannot write the old values back.
        ReloadWhenReplaced(s => s.Logging, InitializeSettingsForm);
    }

    public const string AllCategories = "All categories";

    /// <summary>Rows currently shown (already filtered), oldest first.</summary>
    public ObservableCollection<LogEntryRow> Entries { get; } = new();

    public IReadOnlyList<LevelChoice> LevelOptions => LevelChoice.All;

    public IReadOnlyList<string> CategoryOptions { get; }

    public string LogDirectory { get; private set; }

    public bool IsLive => !IsPaused;

    public bool HasSelectedEntry => SelectedEntry is not null;

    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

    public bool HasEntries => Entries.Count > 0;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_subscribed)
        {
            _logService.EntryLogged += OnEntryLogged;
            _subscribed = true;
        }

        if (AppOptions.Current.IsDemo && !_demoSeeded)
        {
            _demoSeeded = true;
            try
            {
                DemoLogSamples.WriteSampleLines(_logService);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Demo log samples could not be written.");
            }
        }

        LoadSnapshot();
        _drainTimer.Start();
        RefreshFileInfo();
        await LoadJournalAsync(cancellationToken);
        _logger.LogDebug("Logs page initialized with {Count} buffered entries.", _all.Count);
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken)
    {
        LoadSnapshot();
        RefreshFileInfo();
        await LoadJournalAsync(cancellationToken);
    }

    /// <summary>Replaces the view with the service's current buffer, then drains anything that arrived meanwhile.</summary>
    private void LoadSnapshot()
    {
        var snapshot = _logService.Snapshot();
        var seen = new HashSet<LogEntry>(ReferenceEqualityComparer.Instance);
        _all.Clear();
        foreach (var entry in snapshot)
        {
            _all.Add(entry);
            seen.Add(entry);
        }

        // Entries logged between the subscription and the snapshot would otherwise show twice.
        var leftovers = new List<LogEntry>();
        while (_pending.TryDequeue(out var pending))
        {
            if (!seen.Contains(pending))
            {
                leftovers.Add(pending);
            }
        }

        _all.AddRange(leftovers);
        TrimAll();
        RebuildView();
    }

    private void OnEntryLogged(object? sender, LogEntry entry)
    {
        _pending.Enqueue(entry);
        // Keep the queue bounded even if the page is paused for a long time.
        if (_pending.Count > Capacity * 2)
        {
            _pending.TryDequeue(out _);
        }
    }

    private int Capacity => Math.Max(InMemoryLogSink.MinimumCapacity, Settings.Current.Logging.InAppBufferSize);

    private void Drain()
    {
        if (IsPaused)
        {
            PendingWhilePaused = _pending.Count;
            return;
        }

        int added = 0;
        bool viewChanged = false;
        while (added < DrainBatch && _pending.TryDequeue(out var entry))
        {
            _all.Add(entry);
            added++;
            if (_filter.Matches(entry))
            {
                Entries.Add(new LogEntryRow(entry));
                viewChanged = true;
            }
        }

        if (added == 0)
        {
            return;
        }

        if (TrimAll())
        {
            // The oldest entries fell out of the buffer; drop their rows from the view too.
            while (Entries.Count > Capacity)
            {
                Entries.RemoveAt(0);
            }

            viewChanged = true;
        }

        PendingWhilePaused = 0;
        if (viewChanged)
        {
            OnPropertyChanged(nameof(HasEntries));
        }

        UpdateStatus();
    }

    private bool TrimAll()
    {
        int excess = _all.Count - Capacity;
        if (excess <= 0)
        {
            return false;
        }

        _all.RemoveRange(0, excess);
        return true;
    }

    private void RebuildView()
    {
        Entries.Clear();
        foreach (var entry in _all)
        {
            if (_filter.Matches(entry))
            {
                Entries.Add(new LogEntryRow(entry));
            }
        }

        OnPropertyChanged(nameof(HasEntries));
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var counts = LogFilter.Count(_all);
        BufferedCount = counts.Total;
        ErrorCount = counts.Errors;
        WarningCount = counts.Warnings;
        var shown = Entries.Count;
        var parts = new List<string>
        {
            shown == counts.Total ? $"{counts.Total:N0} entries" : $"Showing {shown:N0} of {counts.Total:N0} entries",
        };
        if (counts.Errors > 0)
        {
            parts.Add($"{counts.Errors:N0} error{(counts.Errors == 1 ? string.Empty : "s")}");
        }

        if (counts.Warnings > 0)
        {
            parts.Add($"{counts.Warnings:N0} warning{(counts.Warnings == 1 ? string.Empty : "s")}");
        }

        if (!_filter.IsEmpty && shown == 0 && counts.Total > 0)
        {
            parts.Add("nothing matches the current filter");
        }

        StatusText = counts.Total == 0 ? "No log entries yet." : string.Join(" · ", parts);
    }

    private void ApplyFilter()
    {
        _filter = new LogFilter(SelectedLevel.Level ?? LogLevelSetting.Verbose,
            SelectedCategory == AllCategories ? null : SelectedCategory,
            string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim());
        RebuildView();
    }

    partial void OnSelectedLevelChanged(LevelChoice value) => ApplyFilter();

    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    partial void OnSearchTextChanged(string? value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            Drain();
            PendingWhilePaused = 0;
        }
    }

    private void RefreshFileInfo()
    {
        LogDirectory = _logService.LogDirectory;
        OnPropertyChanged(nameof(LogDirectory));
        if (_logService is SerilogLogService serilog && serilog.CurrentLogFile() is { } file)
        {
            CurrentFilePath = file.Path;
            CurrentFileText = LogText.FileSummary(file.Path, file.SizeBytes);
        }
        else
        {
            CurrentFilePath = null;
            CurrentFileText = Settings.Current.Logging.FileEnabled ? "No log file written yet." : "File logging is turned off.";
        }
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void ClearView()
    {
        _logService.Clear();
        while (_pending.TryDequeue(out _))
        {
        }

        _all.Clear();
        Entries.Clear();
        SelectedEntry = null;
        PendingWhilePaused = 0;
        OnPropertyChanged(nameof(HasEntries));
        UpdateStatus();
        _logger.LogInformation("Log view cleared by the user.");
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = null;
        SelectedCategory = AllCategories;
        SelectedLevel = LevelChoice.For(LogLevelSetting.Verbose);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedEntry))]
    private void CopySelected()
    {
        if (SelectedEntry is null)
        {
            return;
        }

        CopyToClipboard(SelectedEntry.ToClipboardText(), "Log line copied");
    }

    [RelayCommand]
    private void CopyVisible()
    {
        if (Entries.Count == 0)
        {
            _notifications.Info("Nothing to copy", "There are no log lines in the current view.");
            return;
        }

        var text = string.Join(Environment.NewLine, Entries.Select(e => e.ToClipboardText()));
        CopyToClipboard(text, $"{Entries.Count:N0} log lines copied");
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var dir = _logService.LogDirectory;
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            _notifications.Warning("Could not create the log folder", ex.Message);
            return;
        }

        var result = ShellLauncher.Open(dir);
        if (!result.Success)
        {
            _notifications.Warning("Could not open the log folder", result.Message);
        }
    }

    [RelayCommand]
    private void OpenCurrentFile()
    {
        if (CurrentFilePath is null)
        {
            OpenLogFolder();
            return;
        }

        var result = ShellLauncher.Open(CurrentFilePath);
        if (!result.Success)
        {
            _notifications.Warning("Could not open the log file", result.Message);
        }
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var suggested = $"pcchecker-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip";
        var path = await _dialogs.SaveFileAsync("Export diagnostics", suggested, "Zip archive (*.zip)|*.zip");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var written = await _logService.ExportZipAsync(path, ct);
            _logger.LogInformation("Diagnostics exported to {Path}.", written);
            _notifications.Success("Diagnostics exported", $"Saved to {written}. Attach this file to a bug report; it contains logs, the action journal and your settings without the API key.");
        }, "Packing logs, journal and settings…");
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Logs page operation failed.");

    private void CopyToClipboard(string text, string successTitle)
    {
        try
        {
            Clipboard.SetText(text);
            _notifications.Success(successTitle, "The text is on the clipboard.");
        }
        catch (Exception ex)
        {
            _notifications.Warning("Could not copy", ex.Message);
        }
    }
}
