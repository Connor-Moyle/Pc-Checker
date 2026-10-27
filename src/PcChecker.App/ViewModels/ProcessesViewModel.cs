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
/// Processes page, Task Manager style: a live list grouped into Apps, Background processes and Windows processes (or a
/// flat Details list), refreshed at the chosen update speed while the page is visible, with heat-map shading, a column
/// chooser, search, summary tiles, "what is this?" explanations and informational flags. The actions are in the
/// Actions and TaskActions parts, the column chooser and settings in the Columns part.
/// </summary>
public sealed partial class ProcessesViewModel : ViewModelBase
{
    private static readonly TimeSpan FirstSampleGap = TimeSpan.FromMilliseconds(700);
    private const int IconsPerRefresh = 12;

    private readonly IProcessService _processes;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<ProcessesViewModel> _logger;
    private readonly ProcessIconCache _icons = new();
    private readonly PowerTrendTracker _trend = new();
    private readonly Dictionary<string, RunningProcessRow> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _windowsDirectory = WindowsDirectory();
    private DispatcherTimer? _timer;
    private bool _sampling;
    private bool _needsBaseline;
    private ProcessUpdateSpeed _resumeSpeed = ProcessUpdateSpeed.Normal;
    private ProcessSnapshot _last = ProcessSnapshot.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(PauseText))]
    [NotifyPropertyChangedFor(nameof(PauseIcon))]
    private ProcessUpdateSpeed _updateSpeed = ProcessUpdateSpeed.Normal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProcessesView))]
    private bool _isDetailsView;

    [ObservableProperty]
    private string _statusText = Loc.T("Reading running programs…");

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFlaggedToggle))]
    private bool _showFlaggedOnly;

    [ObservableProperty]
    private ProcessSortColumn _sortColumn = ProcessSortColumn.Cpu;

    [ObservableProperty]
    private bool _sortDescending = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
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
    [NotifyPropertyChangedFor(nameof(HasNotResponding))]
    private string? _notRespondingText;

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
        LoadSettings();
    }

    public ObservableCollection<RunningProcessRow> Rows { get; } = [];

    /// <summary>A program (or group) is selected; section headings do not count.</summary>
    public bool HasSelection => SelectedRow is { IsSection: false };

    public bool IsProcessesView => !IsDetailsView;

    public bool IsPaused => UpdateSpeed == ProcessUpdateSpeed.Paused;

    public bool HasFlagged => FlaggedCount > 0;

    public bool HasNotResponding => !string.IsNullOrEmpty(NotRespondingText);

    /// <summary>The "Only flagged" switch stays visible while it is on, so the filter can be cleared after the last flagged process ends.</summary>
    public bool ShowFlaggedToggle => HasFlagged || ShowFlaggedOnly;

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    public string FlaggedTitle => FlaggedCount == 1
        ? Loc.T("1 process may be worth a closer look")
        : Loc.F("{0} processes may be worth a closer look", FlaggedCount);

    public string PauseText => IsPaused ? Loc.T("Resume updates") : Loc.T("Pause updates");

    public Wpf.Ui.Controls.SymbolRegular PauseIcon => IsPaused ? Wpf.Ui.Controls.SymbolRegular.Play24 : Wpf.Ui.Controls.SymbolRegular.Pause24;

    public string CpuHeader => Loc.F("Processor use across all {0} logical processors", Environment.ProcessorCount);

    public override Task InitializeAsync(CancellationToken cancellationToken) =>
        RunBusyAsync(async ct =>
        {
            // CPU and disk rates need two readings; take a baseline first so the first list is not all zeros.
            await _processes.SampleAsync(ct);
            await Task.Delay(FirstSampleGap, ct);
            Apply(await _processes.SampleAsync(ct));
        }, Loc.T("Reading running programs…"));

    public override Task RefreshAsync(CancellationToken cancellationToken) => SampleNowAsync();

    /// <summary>Called by the page when it becomes visible: starts the live updates.</summary>
    public void StartLive()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = UpdateSpeedRules.Interval(UpdateSpeed) ?? TimeSpan.FromSeconds(1) };
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
    private void TogglePause() => SetUpdateSpeed(IsPaused ? _resumeSpeed : ProcessUpdateSpeed.Paused);

    [RelayCommand]
    private void SetUpdateSpeed(ProcessUpdateSpeed speed) => UpdateSpeed = speed;

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

    /// <summary>Expands or collapses the selected group (Right and Left arrow keys).</summary>
    public void SetSelectedExpanded(bool expanded)
    {
        if (SelectedRow?.Group is not { } group || _expandedGroups.Contains(group.Key) == expanded)
        {
            return;
        }

        if (expanded)
        {
            _expandedGroups.Add(group.Key);
        }
        else
        {
            _expandedGroups.Remove(group.Key);
        }

        RebuildRows();
    }

    partial void OnSearchTextChanged(string? value) => RebuildRows();

    partial void OnShowFlaggedOnlyChanged(bool value) => RebuildRows();

    partial void OnWarningTextChanged(string? value) => OnPropertyChanged(nameof(HasWarning));

    partial void OnSelectedRowChanged(RunningProcessRow? value) => RefreshSelectionState();

    partial void OnIsDetailsViewChanged(bool value)
    {
        Settings.Current.Processes.DetailsView = value;
        SaveSettings();
        LoadColumnOptions();
        RebuildRows();
    }

    partial void OnUpdateSpeedChanged(ProcessUpdateSpeed value)
    {
        if (value != ProcessUpdateSpeed.Paused)
        {
            _resumeSpeed = value;
        }

        foreach (var option in SpeedOptions)
        {
            option.IsSelected = option.Speed == value;
        }

        Settings.Current.Processes.UpdateSpeed = value;
        SaveSettings();
        if (_timer is not null && UpdateSpeedRules.Interval(value) is { } interval)
        {
            _timer.Interval = interval;
        }

        if (value == ProcessUpdateSpeed.Paused)
        {
            _needsBaseline = true;
        }
        else if (IsInitialized)
        {
            _ = SampleNowAsync();
        }

        UpdateStatus();
    }

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
        var elapsed = _last.Timestamp == DateTimeOffset.MinValue ? TimeSpan.Zero : snapshot.Timestamp - _last.Timestamp;
        _trend.Update(snapshot.Processes.ToDictionary(p => p.Key, PowerUsageRules.Score, StringComparer.Ordinal), elapsed);
        _last = snapshot;
        WarningText = snapshot.Warnings.Count > 0 ? string.Join(" ", snapshot.Warnings) : null;
        GpuAvailable = snapshot.GpuAvailable;
        UpdateSummary();
        RebuildRows();
        UpdateStatus();
    }

    private double Trend(ProcessInfo process) => _trend.Get(process.Key) ?? PowerUsageRules.Score(process);

    private void UpdateSummary()
    {
        var summary = ProcessText.Summarize(_last, _processes.OwnProcessId);
        TotalCpuText = ProcessMath.FormatPercent(summary.TotalCpuPercent);
        MemoryUsedText = summary.MemoryTotalBytes > 0 ? ByteSize.Format(summary.MemoryUsedBytes) : "—";
        MemoryTotalText = summary.MemoryTotalBytes > 0 ? Loc.F("of {0}", ByteSize.Format(summary.MemoryTotalBytes)) : null;
        MemorySubtitle = summary.MemoryTotalBytes > 0 ? Loc.F("{0} % in use", Math.Round(summary.MemoryPercent)) : null;
        ProcessCountText = summary.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var groups = ProcessGrouping.Build(_last.Processes, _windowsDirectory);
        AppCountText = Loc.F("{0} apps · {1} in the background", groups.Count(g => g.Category == ProcessCategory.App),
            groups.Count(g => g.Category == ProcessCategory.Background));
        if (summary.TopConsumer is { } top)
        {
            TopConsumerText = ProcessSorting.TitleOf(top);
            TopConsumerDetail = Loc.F("{0} CPU · {1}", ProcessMath.FormatPercent(top.CpuPercent), ByteSize.Format(top.PrivateBytes));
        }
        else
        {
            TopConsumerText = "—";
            TopConsumerDetail = null;
        }

        var frozen = _last.Processes.Where(p => p.HasWindow && p.State == ProcessRunState.NotResponding).Select(ProcessSorting.TitleOf).Distinct().ToList();
        NotRespondingText = frozen.Count == 0 ? null
            : frozen.Count == 1 ? Loc.F("{0} is not responding. Wait a moment, or select it and press End task.", frozen[0])
            : Loc.F("{0} are not responding. Wait a moment, or select one and press End task.", string.Join(", ", frozen));
    }

    private void UpdateStatus()
    {
        string last = _last.Timestamp == DateTimeOffset.MinValue ? "—" : _last.Timestamp.ToLocalTime().ToString("T");
        StatusText = UpdateSpeed switch
        {
            ProcessUpdateSpeed.Paused => Loc.T("Paused. Choose an update speed or press Resume updates to continue."),
            ProcessUpdateSpeed.High => Loc.F("Updates twice a second · last at {0}", last),
            ProcessUpdateSpeed.Low => Loc.F("Updates every 4 seconds · last at {0}", last),
            _ => Loc.F("Updates every second · last at {0}", last),
        };
    }

    /// <summary>Filters, sorts and groups the last snapshot and updates the visible rows in place.</summary>
    private void RebuildRows()
    {
        var context = new ProcessRowContext(_processes.OwnProcessId, _windowsDirectory, DateTimeOffset.Now, _last.MemoryTotalBytes,
            IsDetailsView, _last.ConnectionsAvailable, Trend);
        var visible = _last.Processes.Where(p => RunningProcessRow.Matches(p, SearchText));
        if (ShowFlaggedOnly)
        {
            visible = visible.Where(p => ProcessSuspicionRules.Evaluate(p, ProcessCatalog.Lookup(p.Name), _windowsDirectory).Count > 0);
        }

        var list = visible.ToList();
        var entries = IsDetailsView
            ? ProcessSorting.Sort(list, SortColumn, SortDescending, Trend).Select(p => new ProcessListEntry(p, null, false)).ToList()
            : ProcessGrouping.Arrange(list, _windowsDirectory, SortColumn, SortDescending, _expandedGroups, Trend);
        var target = new List<RunningProcessRow>(entries.Count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        int iconBudget = IconsPerRefresh;
        foreach (var entry in entries)
        {
            string key = entry.Key + (entry.IsChild ? "/child" : string.Empty);
            if (!_rows.TryGetValue(key, out var row))
            {
                row = new RunningProcessRow(entry.Key, entry.IsGroup, entry.IsChild, entry.IsSection);
                _rows[key] = row;
            }

            if (entry.Section is { } section)
            {
                row.UpdateSection(section, entry.SectionCount);
            }
            else
            {
                row.Update(entry, context);
                row.IsExpanded = entry.Group is { } g && _expandedGroups.Contains(g.Key);
                row.Icon ??= _icons.Get(row.Process?.Path, ref iconBudget);
            }

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

        TrySelectPendingProcess();
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
