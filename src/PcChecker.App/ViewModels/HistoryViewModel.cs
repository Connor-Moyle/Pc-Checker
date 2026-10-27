using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.History;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// History page: picks a time range, loads the recorded samples, draws one chart per metric, lists the notable
/// events and exports the range as CSV. Recording itself happens in <see cref="IHistoryService"/>.
/// </summary>
public sealed partial class HistoryViewModel : ViewModelBase
{
    /// <summary>Short ranges follow the recorder live, but reload at most this often.</summary>
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromMinutes(1);
    private const int MaxEventRows = 50;

    private readonly IHistoryService _history;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<HistoryViewModel> _logger;
    private HistoryData? _data;
    private DateTimeOffset _lastLoad = DateTimeOffset.MinValue;
    private int _liveReloadPending;
    private bool _subscribed;
    private volatile bool _isPageVisible;

    [ObservableProperty]
    private HistoryRangeOption _selectedRange;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoData))]
    private bool _hasData;

    [ObservableProperty]
    private bool _hasLoaded;

    [ObservableProperty]
    private bool _isRecordingOff;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _cpuPeakText = "—";

    [ObservableProperty]
    private string _cpuPeakSubtitle = string.Empty;

    [ObservableProperty]
    private string _gpuPeakText = "—";

    [ObservableProperty]
    private string _gpuPeakSubtitle = string.Empty;

    [ObservableProperty]
    private string _energyText = "—";

    [ObservableProperty]
    private string _energySubtitle = string.Empty;

    [ObservableProperty]
    private string _eventCountText = "0";

    [ObservableProperty]
    private string _eventSubtitle = string.Empty;

    [ObservableProperty]
    private string _storageText = string.Empty;

    [ObservableProperty]
    private string? _warningText;

    public HistoryViewModel(IHistoryService history, ISettingsService settings, IDialogService dialogs, INotificationService notifications,
        INavigationService navigation, ILogger<HistoryViewModel> logger)
        : base(settings)
    {
        _history = history;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
        Ranges =
        [
            new(Loc.T("Last hour"), HistoryRange.LastHour),
            new(Loc.T("Last 24 hours"), HistoryRange.Last24Hours),
            new(Loc.T("Last 7 days"), HistoryRange.Last7Days),
            new(Loc.T("Last 30 days"), HistoryRange.Last30Days),
            new(Loc.T("Last 90 days"), HistoryRange.Last90Days),
        ];
        _selectedRange = Ranges[1];
        UpdateSubtitle(settings.Current);
        settings.Changed += (_, s) => DispatcherHelper.PostToUi(() => UpdateSubtitle(s));
    }

    public IReadOnlyList<HistoryRangeOption> Ranges { get; }

    public ObservableCollection<HistoryChartItem> Charts { get; } = [];

    public ObservableCollection<HistoryEventRow> Events { get; } = [];

    public bool HasNoData => HasLoaded && !HasData;

    public bool HasEvents => Events.Count > 0;

    public bool HasWarning => !string.IsNullOrEmpty(WarningText);

    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            _history.Recorded += OnRecorded;
        }

        return LoadAsync(cancellationToken);
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    partial void OnSelectedRangeChanged(HistoryRangeOption value)
    {
        if (IsInitialized)
        {
            _ = LoadAsync(CancellationToken.None);
        }
    }

    partial void OnWarningTextChanged(string? value) => OnPropertyChanged(nameof(HasWarning));

    partial void OnHasLoadedChanged(bool value) => OnPropertyChanged(nameof(HasNoData));

    [RelayCommand]
    private Task RefreshNowAsync() => LoadAsync(CancellationToken.None);

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var data = _data;
        if (data is null || data.IsEmpty)
        {
            _notifications.Warning("Nothing to export", "There is no recorded history in this period.");
            return;
        }

        var path = await _dialogs.SaveFileAsync("Export history", $"pc-history-{DateTime.Now:yyyyMMdd-HHmm}.csv", "CSV files|*.csv|All files|*.*");
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, HistoryCsvExporter.Build(data.Samples), System.Text.Encoding.UTF8);
            _notifications.Success("Exported", Loc.F("{0} rows written to {1}.", data.Samples.Count, Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "History CSV export failed.");
            ErrorMessage = Loc.T("The CSV file could not be written:") + " " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!Directory.Exists(_history.DataDirectory))
        {
            _notifications.Info("Nothing saved yet", "PC Checker has not written any history on this PC yet.");
            return;
        }

        var result = ShellLauncher.Open(_history.DataDirectory);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }

    [RelayCommand]
    private void OpenSettings() => _navigation.Navigate(typeof(SettingsPage));

    [RelayCommand]
    private async Task TurnOnRecordingAsync()
    {
        Settings.Current.History ??= new HistorySettings();
        Settings.Current.History.Enabled = true;
        try
        {
            await Settings.SaveAsync();
            _history.Start();
            _notifications.Success("Recording history", "PC Checker now records history while it is open or in the tray.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Turning on history recording failed.");
            ErrorMessage = ex.Message;
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "History page failed.");

    private Task LoadAsync(CancellationToken external) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, external);
        var range = SelectedRange.Range;
        var to = DateTimeOffset.UtcNow;
        var data = await _history.LoadAsync(to - range.Span, to, linked.Token);
        var storage = await _history.GetStorageInfoAsync(linked.Token);
        Apply(data, storage, range);
    }, "Loading history…");

    /// <summary>
    /// Page shown or hidden (another page, minimised to the tray). Live reloads only run while the page is visible;
    /// coming back to a short range that has new samples catches up straight away.
    /// </summary>
    public void OnPageVisibilityChanged(bool visible)
    {
        _isPageVisible = visible;
        if (visible && IsInitialized && SelectedRange.Range.Span <= TimeSpan.FromHours(24)
            && _history.LastRecorded is { } last && last.ToUniversalTime() > _lastLoad)
        {
            QueueLiveReload();
        }
    }

    /// <summary>Reloads a short range quietly as new samples arrive, without the busy overlay.</summary>
    private void OnRecorded(object? sender, EventArgs e)
    {
        if (!_isPageVisible || SelectedRange.Range.Span > TimeSpan.FromHours(24) || DateTimeOffset.UtcNow - _lastLoad < LiveRefreshInterval)
        {
            return;
        }

        QueueLiveReload();
    }

    private void QueueLiveReload()
    {
        if (Interlocked.Exchange(ref _liveReloadPending, 1) == 1)
        {
            return;
        }

        DispatcherHelper.PostToUi(async () =>
        {
            try
            {
                if (_isPageVisible && !IsBusy && IsInitialized && !IsMainWindowMinimized())
                {
                    var range = SelectedRange.Range;
                    var to = DateTimeOffset.UtcNow;
                    var data = await _history.LoadAsync(to - range.Span, to, CancellationToken.None);
                    var storage = await _history.GetStorageInfoAsync(CancellationToken.None);
                    if (range == SelectedRange.Range && !IsBusy)
                    {
                        Apply(data, storage, range);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Refreshing the History page after a new sample failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _liveReloadPending, 0);
            }
        });
    }

    private static bool IsMainWindowMinimized() =>
        System.Windows.Application.Current?.MainWindow is { } window
        && (window.WindowState == System.Windows.WindowState.Minimized || !window.IsVisible);

    private void Apply(HistoryData data, HistoryStorageInfo storage, HistoryRange range)
    {
        _data = data;
        _lastLoad = DateTimeOffset.UtcNow;
        var monitoring = Settings.Current.Monitoring;
        var options = Settings.Current.History ?? new HistorySettings();

        var bucket = HistoryRollup.DisplayBucketFor(range.Span);
        var display = HistoryRollup.Compact(data.Samples, bucket);
        var interval = TimeSpan.FromSeconds(HistorySettings.ClampInterval(options.SampleIntervalSeconds));
        var gap = HistoryMath.GapThreshold(bucket > interval ? bucket : interval);
        Charts.Clear();
        foreach (var metric in data.Metrics)
        {
            Charts.Add(BuildChart(metric, data, display, gap, monitoring));
        }

        Events.Clear();
        foreach (var e in HistoryEventDetector.Detect(data.Samples, HistoryThresholds.From(monitoring)).Take(MaxEventRows))
        {
            Events.Add(HistoryEventRow.From(e));
        }

        OnPropertyChanged(nameof(HasEvents));
        ApplySummary(data);
        HasData = !data.IsEmpty;
        HasLoaded = true;
        WarningText = data.CorruptLines > 0
            ? Loc.F("{0} damaged lines in the history files were skipped (for example after a crash or power cut).", data.CorruptLines)
            : null;
        StorageText = storage.Files == 0
            ? Loc.T("Nothing stored yet.")
            : Loc.F("{0} days stored, {1} on disk.", storage.Files, ByteSize.Format(storage.Bytes));
        UpdateSubtitle(Settings.Current);
    }

    private void UpdateSubtitle(AppSettings settings)
    {
        var options = settings.History ?? new HistorySettings();
        IsRecordingOff = !options.Enabled;
        Subtitle = IsRecordingOff
            ? Loc.T("Shows the history saved while recording was on.")
            : Loc.F("A sample every {0}, kept for {1} days. Nothing leaves this PC.", IntervalText(HistorySettings.ClampInterval(options.SampleIntervalSeconds)),
                HistorySettings.ClampRetention(options.RetentionDays));
    }

    private void ApplySummary(HistoryData data)
    {
        var culture = CultureInfo.CurrentCulture;
        var cpu = HistoryMath.Summarize(data.Series(HistoryMetrics.CpuTemp));
        var gpu = HistoryMath.Summarize(data.Series(HistoryMetrics.GpuTemp));
        CpuPeakText = cpu is null ? "—" : cpu.Max.ToString("0", culture);
        CpuPeakSubtitle = cpu is null ? Loc.T("No readings") : When(cpu.MaxTime);
        GpuPeakText = gpu is null ? "—" : gpu.Max.ToString("0", culture);
        GpuPeakSubtitle = gpu is null ? Loc.T("No readings") : When(gpu.MaxTime);

        double kwh = HistoryMath.EnergyKwh(data.Samples);
        var general = Settings.Current.General;
        EnergyText = kwh <= 0 ? "—" : kwh.ToString(kwh < 10 ? "0.00" : "0.0", culture);
        EnergySubtitle = kwh <= 0
            ? Loc.T("No power readings")
            : Loc.F("About {0} at your electricity price", general.CurrencySymbol + (kwh * general.PricePerKwh).ToString("0.00", culture));

        int critical = Events.Count(e => e.Severity >= Severity.Critical);
        EventCountText = Events.Count.ToString(culture);
        EventSubtitle = Events.Count == 0
            ? Loc.T("Nothing unusual in this period")
            : critical > 0 ? Loc.F("{0} reached a critical level", critical) : Loc.T("Listed below the charts");
    }

    private HistoryChartItem BuildChart(string metric, HistoryData data, IReadOnlyList<HistorySample> display, TimeSpan gap, MonitoringSettings monitoring)
    {
        var culture = CultureInfo.CurrentCulture;
        var stats = HistoryMath.Summarize(data.Series(metric));
        var (min, max) = HistoryMath.AxisRange(metric, stats);
        double limit = metric switch
        {
            HistoryMetrics.CpuTemp => monitoring.CpuWarnC,
            HistoryMetrics.GpuTemp => monitoring.GpuWarnC,
            _ => double.NaN,
        };
        if (!double.IsNaN(limit) && limit > max && stats is not null && stats.Max > limit - 15)
        {
            // Show the warning line when the readings came within 15 °C of it.
            max = Math.Ceiling(limit + 2);
        }

        string unit = Unit(metric);
        string headline = stats is null ? "—" : Value(stats.Last, metric) + " " + unit;
        string statsText = stats is null
            ? Loc.T("No readings in this period")
            : Loc.F("Average {0}, highest {1}, lowest {2}", Value(stats.Average, metric), Value(stats.Max, metric), Value(stats.Min, metric));
        // Health checks are occasional and free space is read hourly: join those points instead of breaking the line.
        var hourlyGap = HistoryMath.GapThreshold(TimeSpan.FromHours(1));
        var metricGap = metric == HistoryMetrics.Health ? TimeSpan.MaxValue
            : HistoryMetrics.IsDiskFree(metric) && hourlyGap > gap ? hourlyGap
            : gap;
        return new HistoryChartItem(metric, Title(metric), Tone(metric), unit, headline.Trim(), statsText,
            HistoryMath.Series(display, metric), data.From, data.To, min, max, limit, metricGap);
    }

    private static string Title(string metric) => metric switch
    {
        HistoryMetrics.CpuTemp => Loc.T("CPU temperature (peak)"),
        HistoryMetrics.GpuTemp => Loc.T("GPU temperature (peak)"),
        HistoryMetrics.CpuLoad => Loc.T("CPU load"),
        HistoryMetrics.GpuLoad => Loc.T("GPU load"),
        HistoryMetrics.Power => Loc.T("Power draw"),
        HistoryMetrics.Ram => Loc.T("Memory in use"),
        HistoryMetrics.Health => Loc.T("Health score"),
        _ when HistoryMetrics.IsDiskFree(metric) => Loc.F("Free space on {0}", HistoryMetrics.DriveOf(metric)),
        _ => metric,
    };

    private static string Tone(string metric) => metric switch
    {
        HistoryMetrics.CpuTemp or HistoryMetrics.GpuTemp => "temperature",
        HistoryMetrics.CpuLoad or HistoryMetrics.GpuLoad => "load",
        HistoryMetrics.Power => "power",
        HistoryMetrics.Ram => "memory",
        HistoryMetrics.Health => "health",
        _ => "disk",
    };

    private static string Unit(string metric) => metric == HistoryMetrics.Health ? string.Empty : HistoryMetrics.UnitOf(metric);

    private static string Value(double value, string metric) =>
        value.ToString(HistoryMetrics.IsDiskFree(metric) && value < 100 ? "0.0" : "0", CultureInfo.CurrentCulture);

    private static string When(DateTimeOffset time) => time.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture);

    private static string IntervalText(int seconds) => seconds switch
    {
        60 => Loc.T("minute"),
        < 60 => Loc.F("{0} seconds", seconds),
        _ when seconds % 60 == 0 => Loc.F("{0} minutes", seconds / 60),
        _ => Loc.F("{0} seconds", seconds),
    };
}
