using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Helpers;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Processes page: a live list of running programs refreshed every 2 seconds while the page is visible (pausable),
/// summary tiles, sorting, search, grouping by app, "what is this?" explanations and informational flags. Actions
/// (end task, end tree, priority, file location, web search, copy) are in the Actions part.
/// </summary>
public sealed partial class ProcessesViewModel : ViewModelBase
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FirstSampleGap = TimeSpan.FromMilliseconds(700);
    private const int IconsPerRefresh = 12;

    private readonly IProcessService _processes;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<ProcessesViewModel> _logger;
    private readonly ProcessIconCache _icons = new();
    private readonly Dictionary<string, RunningProcessRow> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _windowsDirectory = WindowsDirectory();
    private DispatcherTimer? _timer;
    private bool _sampling;
    private bool _needsBaseline;
    private ProcessSnapshot _last = ProcessSnapshot.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText))]
    [NotifyPropertyChangedFor(nameof(PauseIcon))]
    private bool _isPaused;

    [ObservableProperty]
    private string _statusText = Loc.T("Reading running programs\u2026");

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private bool _groupByApp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFlaggedToggle))]
    private bool _showFlaggedOnly;

    [ObservableProperty]
    private ProcessSortColumn _sortColumn = ProcessSortColumn.Cpu;

    [ObservableProperty]
    private bool _sortDescending = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(EndTaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(EndTreeCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetPriorityCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchOnlineCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyDetailsCommand))]
    private RunningProcessRow? _selectedRow;

    [ObservableProperty]
    private string _totalCpuText = "—";

    [ObservableProperty]
    private string _memoryUsedText = "—";

    [ObservableProperty]
    private string? _memoryTotalText;

    [ObservableProperty]
    private string? _memorySubtitle;

    [ObservableProperty]
    private string _processCountText = "—";

    [ObservableProperty]
    private string? _appCountText;

    [ObservableProperty]
    private string _topConsumerText = "—";

    [ObservableProperty]
    private string? _topConsumerDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFlagged))]
    [NotifyPropertyChangedFor(nameof(FlaggedTitle))]
    [NotifyPropertyChangedFor(nameof(ShowFlaggedToggle))]
    private int _flaggedCount;

    [ObservableProperty]
    private bool _gpuAvailable = true;

    [ObservableProperty]
    private string? _warningText;

    [ObservableProperty]
    private bool _hasNoRows;

    public ProcessesViewModel(IProcessService processes, ISettingsService settings, IDialogService dialogs, INotificationService notifications,
        ILogger<ProcessesViewModel> logger)
        : base(settings)
    {
        _processes = processes;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
    }

    public ObservableCollection<RunningProcessRow> Rows { get; } = [];

    public bool HasSelection => SelectedRow is not null;

    public bool HasFlagged => FlaggedCount > 0;

    /// <summary>The "Only flagged" switch stays visible while it is on, so the filter can be cleared after the last flagged process ends.</summary>
    public bool ShowFlaggedToggle => HasFlagged || ShowFlaggedOnly;

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    public string FlaggedTitle => FlaggedCount == 1
        ? Loc.T("1 process may be worth a closer look")
        : Loc.F("{0} processes may be worth a closer look", FlaggedCount);

    public string PauseText => IsPaused ? Loc.T("Resume") : Loc.T("Pause");

    public Wpf.Ui.Controls.SymbolRegular PauseIcon => IsPaused ? Wpf.Ui.Controls.SymbolRegular.Play24 : Wpf.Ui.Controls.SymbolRegular.Pause24;

    public string CpuHeader => Loc.F("Processor use across all {0} logical processors", Environment.ProcessorCount);

    public override Task InitializeAsync(CancellationToken cancellationToken) =>
        RunBusyAsync(async ct =>
        {
            // CPU and disk rates need two readings; take a baseline first so the first list is not all zeros.
            await _processes.SampleAsync(ct);
            await Task.Delay(FirstSampleGap, ct);
            Apply(await _processes.SampleAsync(ct));
        }, Loc.T("Reading running programs\u2026"));

    public override Task RefreshAsync(CancellationToken cancellationToken) => SampleNowAsync();

    /// <summary>Called by the page when it becomes visible: starts the 2-second refresh.</summary>
    public void StartLive()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _timer.Tick += OnTick;
        _timer.Start();
        if (_needsBaseline && !IsPaused && IsInitialized)
        {
            _ = SampleNowAsync();
        }
    }

    /// <summary>Called by the page when it is hidden: nothing is read in the background.</summary>
    public void StopLive()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
        _needsBaseline = true;
    }

    /// <summary>Column header clicked: same column flips the direction, a new column starts with its natural direction.</summary>
    public bool SortBy(string? column)
    {
        if (!ProcessSorting.TryParseColumn(column, out var parsed))
        {
            return SortDescending;
        }

        if (parsed == SortColumn)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = parsed;
            SortDescending = ProcessSorting.DefaultDescending(parsed);
        }

        RebuildRows();
        return SortDescending;
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        UpdateStatus();
        if (IsPaused)
        {
            _needsBaseline = true;
        }
        else
        {
            _ = SampleNowAsync();
        }
    }

    [RelayCommand]
    private void ToggleGroup(RunningProcessRow? row)
    {
        if (row?.Group is not { } group)
        {
            return;
        }

        if (!_expandedGroups.Remove(group.Key))
        {
            _expandedGroups.Add(group.Key);
        }

        RebuildRows();
    }

    partial void OnSearchTextChanged(string? value) => RebuildRows();

    partial void OnGroupByAppChanged(bool value) => RebuildRows();

    partial void OnShowFlaggedOnlyChanged(bool value) => RebuildRows();

    partial void OnWarningTextChanged(string? value) => OnPropertyChanged(nameof(HasWarning));

    private async void OnTick(object? sender, EventArgs e)
    {
        if (IsPaused || IsBusy)
        {
            return;
        }

        await SampleNowAsync();
    }

    private async Task SampleNowAsync()
    {
        if (_sampling)
        {
            return;
        }

        _sampling = true;
        try
        {
            if (_needsBaseline)
            {
                // After a pause or while the page was hidden the last reading is old: rates against it would be averages
                // over that whole time. Start a new baseline and measure a short interval instead.
                _needsBaseline = false;
                _processes.ResetRates();
                await _processes.SampleAsync(CancellationToken.None);
                await Task.Delay(FirstSampleGap);
            }

            Apply(await _processes.SampleAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the process list failed.");
            WarningText = Loc.T("The process list could not be refreshed. It will try again in a moment.");
        }
        finally
        {
            _sampling = false;
        }
    }

    private void Apply(ProcessSnapshot snapshot)
    {
        _last = snapshot;
        WarningText = snapshot.Warnings.Count > 0 ? string.Join(" ", snapshot.Warnings) : null;
        GpuAvailable = snapshot.GpuAvailable;
        UpdateSummary();
        RebuildRows();
        UpdateStatus();
    }

    private void UpdateSummary()
    {
        var summary = ProcessText.Summarize(_last, _processes.OwnProcessId);
        TotalCpuText = ProcessMath.FormatPercent(summary.TotalCpuPercent);
        MemoryUsedText = summary.MemoryTotalBytes > 0 ? ByteSize.Format(summary.MemoryUsedBytes) : "—";
        MemoryTotalText = summary.MemoryTotalBytes > 0 ? Loc.F("of {0}", ByteSize.Format(summary.MemoryTotalBytes)) : null;
        MemorySubtitle = summary.MemoryTotalBytes > 0 ? Loc.F("{0} % in use", Math.Round(summary.MemoryPercent)) : null;
        ProcessCountText = summary.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        AppCountText = Loc.F("{0} programs", ProcessSorting.GroupByApp(_last.Processes).Count);
        if (summary.TopConsumer is { } top)
        {
            TopConsumerText = ProcessCatalog.Lookup(top.Name)?.Title ?? StripExe(top.Name);
            TopConsumerDetail = Loc.F("{0} CPU \u00b7 {1}", ProcessMath.FormatPercent(top.CpuPercent), ByteSize.Format(top.PrivateBytes));
        }
        else
        {
            TopConsumerText = "—";
            TopConsumerDetail = null;
        }
    }

    private void UpdateStatus() =>
        StatusText = IsPaused
            ? Loc.T("Paused. Press Resume to continue live updates.")
            : Loc.F("Updates every 2 seconds \u00b7 last at {0}", _last.Timestamp == DateTimeOffset.MinValue ? "—" : _last.Timestamp.ToLocalTime().ToString("T"));

    /// <summary>Filters, sorts and groups the last snapshot and updates the visible rows in place.</summary>
    private void RebuildRows()
    {
        var now = DateTimeOffset.Now;
        int ownPid = _processes.OwnProcessId;
        var visible = _last.Processes.Where(p => RunningProcessRow.Matches(p, SearchText));
        if (ShowFlaggedOnly)
        {
            visible = visible.Where(p => ProcessSuspicionRules.Evaluate(p, ProcessCatalog.Lookup(p.Name), _windowsDirectory).Count > 0);
        }

        var entries = ProcessSorting.Arrange(visible.ToList(), GroupByApp, SortColumn, SortDescending, _expandedGroups);
        var target = new List<RunningProcessRow>(entries.Count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        int iconBudget = IconsPerRefresh;
        foreach (var entry in entries)
        {
            string key = entry.Key + (entry.IsChild ? "/child" : string.Empty);
            if (!_rows.TryGetValue(key, out var row))
            {
                row = new RunningProcessRow(entry.Key, entry.IsGroup, entry.IsChild);
                _rows[key] = row;
            }

            row.Update(entry, ownPid, _windowsDirectory, now);
            row.IsExpanded = entry.Group is { } g && _expandedGroups.Contains(g.Key);
            row.Icon ??= _icons.Get(row.Process?.Path, ref iconBudget);
            used.Add(key);
            target.Add(row);
        }

        foreach (string stale in _rows.Keys.Where(k => !used.Contains(k)).ToList())
        {
            _rows.Remove(stale);
        }

        Sync(Rows, target);
        HasNoRows = Rows.Count == 0;
        FlaggedCount = _last.Processes.Count(p => ProcessSuspicionRules.Evaluate(p, ProcessCatalog.Lookup(p.Name), _windowsDirectory).Count > 0);

        if (SelectedRow is not null && !Rows.Contains(SelectedRow))
        {
            SelectedRow = null;
        }
        else
        {
            RefreshSelectionState();
        }
    }

    /// <summary>Makes <paramref name="rows"/> equal to <paramref name="target"/> with moves instead of a reset.</summary>
    private static void Sync(ObservableCollection<RunningProcessRow> rows, IReadOnlyList<RunningProcessRow> target)
    {
        var keep = new HashSet<RunningProcessRow>(target);
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(rows[i]))
            {
                rows.RemoveAt(i);
            }
        }

        for (int i = 0; i < target.Count; i++)
        {
            var wanted = target[i];
            if (i < rows.Count && ReferenceEquals(rows[i], wanted))
            {
                continue;
            }

            int current = rows.IndexOf(wanted);
            if (current >= 0)
            {
                rows.Move(current, i);
            }
            else
            {
                rows.Insert(i, wanted);
            }
        }
    }

    /// <summary>Re-evaluates commands after the selected row's values changed (a process can become protected or gone).</summary>
    private void RefreshSelectionState()
    {
        EndTaskCommand.NotifyCanExecuteChanged();
        EndTreeCommand.NotifyCanExecuteChanged();
        SetPriorityCommand.NotifyCanExecuteChanged();
        OpenFileLocationCommand.NotifyCanExecuteChanged();
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static string WindowsDirectory()
    {
        try
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return string.IsNullOrWhiteSpace(folder) ? ProcessSuspicionRules.DefaultWindowsDirectory : folder;
        }
        catch
        {
            return ProcessSuspicionRules.DefaultWindowsDirectory;
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Processes page operation failed.");
}
