using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Converters;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Audio;
using PcChecker.Core.Checkup;
using PcChecker.Core.Common;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.Displays;
using PcChecker.Core.Drivers;
using PcChecker.Core.Fans;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.History;
using PcChecker.Core.Lighting;
using PcChecker.Core.Logging;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Performance;
using PcChecker.Core.Power;
using PcChecker.Core.Reports;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using PcChecker.Core.Software;
using PcChecker.Core.Updates;
using PcChecker.Core.WindowsUpdate;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The Dashboard: health score, live temperatures and power, alerts, quick actions, controls at a glance, hardware
/// support, system summary and recent activity. Every service is read defensively so one unavailable part never
/// empties the page. Split across partial files: this one holds state and live data, the other the actions.
/// </summary>
public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly IHealthService _health;
    private readonly ISensorService _sensors;
    private readonly IPowerService _power;
    private readonly IHardwareInventoryService _hardware;
    private readonly IPerformanceAnalyzer _performance;
    private readonly ISoftwareInventoryService _software;
    private readonly IDriverService _drivers;
    private readonly IReportService _reports;
    private readonly IActionJournal _journal;
    private readonly IAppUpdateService _updates;
    private readonly IFanControlService _fans;
    private readonly ILightingService _lighting;
    private readonly IDisplayService _displays;
    private readonly IAudioService _audio;
    private readonly IOverclockService _overclock;
    private readonly IWindowsUpdateService _windowsUpdate;
    private readonly IHistoryService _history;
    private readonly IDiskSpaceService _diskSpace;
    private readonly ICheckupService _checkup;
    private readonly QuickActionRunner _quickActions;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly Dictionary<string, TemperatureAlert> _temperatureAlerts = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingRebootReason;
    private DateTimeOffset _lastUptimeRefresh;

    // Health hero
    [ObservableProperty] private int _healthScore;
    [ObservableProperty] private string _healthLabel = "Not checked yet";
    [ObservableProperty] private string _healthDetail = "Run a health check to score drives, battery, devices and recent errors.";
    [ObservableProperty] private bool _hasHealthReport;
    [ObservableProperty] private double _gaugeWarnAt = 101;
    [ObservableProperty] private double _gaugeCritAt = 102;

    // Live stats
    [ObservableProperty] private double? _cpuTempC;
    [ObservableProperty] private string _cpuTempText = "—";
    [ObservableProperty] private string? _cpuSubtitle;
    [ObservableProperty] private Brush _cpuTempBrush = TemperatureToBrushConverter.Unknown;
    [ObservableProperty] private double? _gpuTempC;
    [ObservableProperty] private string _gpuTempText = "—";
    [ObservableProperty] private string? _gpuSubtitle;
    [ObservableProperty] private Brush _gpuTempBrush = TemperatureToBrushConverter.Unknown;
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string? _ramSubtitle;
    [ObservableProperty] private Brush _ramBrush = TemperatureToBrushConverter.Unknown;
    [ObservableProperty] private Brush _diskBrush = TemperatureToBrushConverter.Unknown;
    [ObservableProperty] private string _powerText = "—";
    [ObservableProperty] private string? _powerSubtitle;
    [ObservableProperty] private string? _sensorsNote;

    // Update banner and notices
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string? _updateBannerText;

    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string? _lastScanText;

    public DashboardViewModel(ISettingsService settings, IHealthService health, ISensorService sensors, IPowerService power,
        IHardwareInventoryService hardware, IPerformanceAnalyzer performance, ISoftwareInventoryService software, IDriverService drivers,
        IReportService reports, IActionJournal journal, IAppUpdateService updates, IFanControlService fans, ILightingService lighting,
        IDisplayService displays, IAudioService audio, IOverclockService overclock, IWindowsUpdateService windowsUpdate, IHistoryService history,
        IDiskSpaceService diskSpace, ICheckupService checkup, QuickActionRunner quickActions, INavigationService navigation, IDialogService dialogs,
        INotificationService notifications, ILogger<DashboardViewModel> logger, Core.Startup.IStartupService startup, PawnIoInstallFlow pawnIo,
        SimpleModeHintViewModel simpleModeHint)
        : base(settings)
    {
        _startup = startup;
        _pawnIo = pawnIo;
        SimpleModeHint = simpleModeHint;
        checkup.Completed += OnCheckupCompleted;
        _health = health;
        _sensors = sensors;
        _power = power;
        _hardware = hardware;
        _performance = performance;
        _software = software;
        _drivers = drivers;
        _reports = reports;
        _journal = journal;
        _updates = updates;
        _fans = fans;
        _lighting = lighting;
        _displays = displays;
        _audio = audio;
        _overclock = overclock;
        _windowsUpdate = windowsUpdate;
        _history = history;
        _diskSpace = diskSpace;
        _checkup = checkup;
        _quickActions = quickActions;
        _navigation = navigation;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;

        QuickActions =
        [
            new("Full scan", "Hardware, health, performance, software and drivers in one go.", Wpf.Ui.Controls.SymbolRegular.Search24, FullScanCommand),
            new("Clean temp files", "Free space safely by category.", Wpf.Ui.Controls.SymbolRegular.Broom24, GoToCleanupCommand),
            new("Check drivers", "Find missing, old or broken drivers.", Wpf.Ui.Controls.SymbolRegular.Wrench24, GoToDriversCommand),
            new("Optimize", "Safe one-click speed-ups, all reversible.", Wpf.Ui.Controls.SymbolRegular.Rocket24, GoToOptimizeCommand),
            new("Export full report", "Save everything as HTML, JSON or Markdown.", Wpf.Ui.Controls.SymbolRegular.DocumentArrowDown20, ExportReportCommand),
            new("Temperatures", "Live graphs for every sensor.", Wpf.Ui.Controls.SymbolRegular.Temperature24, GoToSensorsCommand),
        ];

        _sensors.SnapshotUpdated += OnSnapshotUpdated;
        _sensors.AlertRaised += OnAlertRaised;
        _power.Updated += OnPowerUpdated;
        _history.Recorded += OnHistoryRecorded;
        _checkup.LastChanged += OnCheckupChanged;
        StartupNotices.Changed += (_, _) => DispatcherHelper.PostToUi(RebuildAlerts);
        UpdatePrompter.ResultChanged += (_, _) => DispatcherHelper.PostToUi(() => ShowUpdate(UpdatePrompter.LastResult));
    }

    public ObservableCollection<HealthItem> TopIssues { get; } = [];
    public ObservableCollection<DashboardAlert> Alerts { get; } = [];
    public ObservableCollection<ActionRecord> RecentActions { get; } = [];
    public ObservableCollection<KeyValuePair<string, string>> SystemSummary { get; } = [];
    public ObservableCollection<SupportRow> SupportRows { get; } = [];
    public IReadOnlyList<QuickAction> QuickActions { get; }

    public bool HasAlerts => Alerts.Count > 0;
    public bool HasNoAlerts => Alerts.Count == 0;
    public bool HasRecentActions => RecentActions.Count > 0;
    public bool HasNoRecentActions => RecentActions.Count == 0;
    public bool HasSystemSummary => SystemSummary.Count > 0;
    public bool HasSupportRows => SupportRows.Count > 0;
    public bool HasTopIssues => TopIssues.Count > 0;
    public bool IsDemoMode => AppOptions.Mode == AppMode.Demo;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        TryStartSensors();
        ShowUpdate(UpdatePrompter.LastResult);
        await RunBusyAsync(LoadAsync, "Getting to know this PC…");
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunBusyAsync(LoadAsync, "Refreshing…");

    /// <summary>First load: hardware summary, health (per settings), overclock capabilities, controls, journal, update banner.</summary>
    private async Task LoadAsync(CancellationToken ct)
    {
        if (_hardware.Last is null)
        {
            BusyText = "Reading hardware…";
            await Guard(() => _hardware.ScanAsync(null, ct), "hardware scan");
        }

        if (_health.Last is null && Settings.Current.General.CheckHealthOnStartup)
        {
            BusyText = "Checking health…";
            await Guard(() => _health.AnalyzeAsync(new Progress<ProgressUpdate>(p => DispatcherHelper.PostToUi(() => BusyText = "Checking health: " + p.Stage)), ct), "health check");
        }

        if (_overclock.Capabilities is null)
        {
            BusyText = "Detecting what this app can control…";
            await Guard(() => _overclock.DetectAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct), "capability detection");
        }

        BusyText = "Reading fans, lighting, displays and audio…";
        await RefreshControlsAsync(ct);
        BusyText = Loc.T("Reading updates, temperature history and drives…");
        await RefreshGlanceAsync(ct);
        await RefreshAttentionAsync(ct);
        await RefreshRecentActionsAsync();
        if (Settings.Current.Updates.AutoCheck)
        {
            await Guard(() => UpdatePrompter.CheckOnceAsync(_updates, ct).WaitAsync(TimeSpan.FromSeconds(8), ct), "update check");
        }

        if (!IsDemoMode)
        {
            _pendingRebootReason = await Task.Run(PendingRebootDetector.Detect, ct);
        }

        ApplyHealth(_health.Last);
        ApplySummary();
        ApplyLiveStats(_sensors.Latest);
        ApplyPower(_power.Latest);
        ApplySupport(_overclock.Capabilities);
        RebuildAlerts();
        LastScanText = _hardware.Last is { } hw ? "Hardware read " + RelativeTimeConverter.Format(hw.Captured, DateTimeOffset.Now) : null;
    }

    private void TryStartSensors()
    {
        try
        {
            _sensors.Start();
            SensorsNote = _sensors.IsAvailable ? null : _sensors.UnavailableReason ?? "Sensors are not available on this PC.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sensors could not be started.");
            SensorsNote = "Sensors could not be started: " + ex.Message;
        }
    }

    private void OnSnapshotUpdated(object? sender, SensorSnapshot snapshot) => DispatcherHelper.PostToUi(() => ApplyLiveStats(snapshot));

    private void OnPowerUpdated(object? sender, PowerSnapshot snapshot) => DispatcherHelper.PostToUi(() => ApplyPower(snapshot));

    private void OnAlertRaised(object? sender, TemperatureAlert alert)
    {
        lock (_temperatureAlerts)
        {
            // An Info-level alert (or a zero reading) means "back to normal" for that sensor.
            if (alert.Severity <= Severity.Low || alert.Value <= 0)
            {
                _temperatureAlerts.Remove(alert.SensorId);
            }
            else
            {
                _temperatureAlerts[alert.SensorId] = alert;
            }
        }

        DispatcherHelper.PostToUi(RebuildAlerts);
    }

    private void ApplyLiveStats(SensorSnapshot? snapshot)
    {
        var head = SnapshotSummary.Summarize(snapshot);
        var m = Settings.Current.Monitoring;

        CpuTempC = head.CpuTempC;
        CpuTempText = head.CpuTempC is { } cpu ? cpu.ToString("0") : "—";
        CpuSubtitle = head.CpuLoadPercent is { } cl ? $"{cl:0} % load · {Shorten(head.CpuName)}" : Shorten(head.CpuName);
        CpuTempBrush = TempBrush(head.CpuTempC, m.CpuWarnC, m.CpuCritC);

        GpuTempC = head.GpuTempC;
        GpuTempText = head.GpuTempC is { } gpu ? gpu.ToString("0") : "—";
        GpuSubtitle = head.GpuLoadPercent is { } gl ? $"{gl:0} % load · {Shorten(head.GpuName)}" : Shorten(head.GpuName);
        GpuTempBrush = TempBrush(head.GpuTempC, m.GpuWarnC, m.GpuCritC);

        var totalBytes = _hardware.Last?.TotalMemoryBytes ?? _performance.Last?.MemoryTotalBytes;
        if (head.MemoryUsedPercent is { } ramPercent)
        {
            RamText = ramPercent.ToString("0");
            RamSubtitle = head.MemoryUsedGb is { } usedGb
                ? $"{usedGb:0.0} GB used" + (totalBytes is { } t ? $" of {t / (1024.0 * 1024 * 1024):0} GB" : string.Empty)
                : totalBytes is { } tb ? $"of {ByteSize.Format(tb)}" : null;
            RamBrush = ramPercent >= 90 ? TemperatureToBrushConverter.Hot : ramPercent >= 75 ? TemperatureToBrushConverter.Warm : TemperatureToBrushConverter.Normal;
        }
        else if (_performance.Last is { } perf && perf.MemoryTotalBytes > 0)
        {
            RamText = perf.MemoryUsedPercent.ToString("0");
            RamSubtitle = $"of {ByteSize.Format(perf.MemoryTotalBytes)} (last scan)";
            RamBrush = TemperatureToBrushConverter.Normal;
        }

        if (snapshot is not null && head.TotalPowerW is { } w && _power.Latest is null)
        {
            PowerText = w.ToString("0");
            PowerSubtitle = "CPU + GPU only";
        }

        if (DateTimeOffset.Now - _lastUptimeRefresh > TimeSpan.FromSeconds(30))
        {
            _lastUptimeRefresh = DateTimeOffset.Now;
            UptimeText = ComputeUptime();
        }
    }

    private void ApplyPower(PowerSnapshot? snapshot)
    {
        if (snapshot?.EstimatedTotalW is { } watts)
        {
            PowerText = watts.ToString("0");
            var price = Settings.Current.General.PricePerKwh;
            var symbol = Settings.Current.General.CurrencySymbol;
            var perHour = watts / 1000.0 * price;
            PowerSubtitle = snapshot.OnBattery
                ? $"On battery · {snapshot.BatteryPercent?.ToString() ?? "?"} %"
                : $"≈ {symbol}{perHour:0.000} per hour · estimate";
        }
    }

    private void ApplyHealth(HealthReport? report)
    {
        TopIssues.Clear();
        if (report is null)
        {
            HasHealthReport = false;
            HealthScore = 0;
            HealthLabel = "Not checked yet";
            HealthDetail = "Run a health check to score drives, battery, devices and recent errors.";
            GaugeWarnAt = 101;
            GaugeCritAt = 102;
        }
        else
        {
            HasHealthReport = true;
            HealthScore = report.Score;
            HealthLabel = ReportText.ScoreLabel(report.Score);
            int problems = report.Items.Count(i => i.Status is HealthStatus.Warning or HealthStatus.Critical);
            HealthDetail = problems == 0
                ? $"Nothing needs attention. Checked {RelativeTimeConverter.Format(report.Captured, DateTimeOffset.Now)}."
                : $"{problems} item{(problems == 1 ? string.Empty : "s")} need{(problems == 1 ? "s" : string.Empty)} attention. Checked {RelativeTimeConverter.Format(report.Captured, DateTimeOffset.Now)}.";
            // The gauge colours by thresholds where "higher is worse"; a score is the opposite, so pick the band explicitly.
            (GaugeWarnAt, GaugeCritAt) = report.Score < 50 ? (0d, 0d) : report.Score < 75 ? (0d, 101d) : (101d, 102d);
            foreach (var item in report.Items.Where(i => i.Status is HealthStatus.Critical or HealthStatus.Warning)
                         .OrderBy(i => i.Status == HealthStatus.Critical ? 0 : 1).Take(3))
            {
                TopIssues.Add(item);
            }
        }

        OnPropertyChanged(nameof(HasTopIssues));
    }

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        if (UpdatePrompter.LastResult is { IsNewer: true } info)
        {
            await Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AppUpdateFlow>(App.Services).UpdateNowAsync(info, askBeforeRestart: false);
        }
    }

    private void ShowUpdate(AppUpdateInfo? info)
    {
        IsUpdateAvailable = info?.IsNewer == true;
        UpdateBannerText = info?.IsNewer == true
            ? Localization.Loc.F("PC Checker {0} is available (you have {1}).", info.LatestVersion, info.CurrentVersion) + " " + Localization.Loc.T(_updates.IsInstalledViaUpdater
                ? "Update now downloads it, checks it and restarts PC Checker."
                : "Download the new PcChecker.exe from the releases page.")
            : null;
    }

    private void RebuildAlerts()
    {
        Alerts.Clear();
        foreach (var notice in StartupNotices.All)
        {
            Alerts.Add(new DashboardAlert(notice.Title, notice.Message, notice.Severity, typeof(OverclockPage), "Overclock"));
        }

        TemperatureAlert[] temps;
        lock (_temperatureAlerts)
        {
            temps = _temperatureAlerts.Values.OrderByDescending(a => a.Severity).ToArray();
        }

        foreach (var alert in temps)
        {
            Alerts.Add(new DashboardAlert($"{alert.HardwareName} is running hot", $"{alert.Value:0} °C, above the {alert.Threshold:0} °C limit.", alert.Severity, typeof(SensorsPage), "Temperatures"));
        }

        if (_health.Last is { } health)
        {
            foreach (var item in health.Items.Where(i => i.Status == HealthStatus.Critical))
            {
                Alerts.Add(new DashboardAlert(item.Title, item.Recommendation ?? item.Detail, Severity.Critical, typeof(HealthPage), "Health"));
            }
        }

        if (_pendingRebootReason is { } reboot && !UpdateTileShowsRestart)
        {
            Alerts.Add(new DashboardAlert("Restart pending", reboot, Severity.Medium, typeof(StartupPage), "Startup"));
        }

        if (SensorsNote is { } note)
        {
            Alerts.Add(new DashboardAlert("Live sensors unavailable", note, Severity.Low, typeof(SensorsPage), "Temperatures"));
        }

        OnPropertyChanged(nameof(HasAlerts));
        OnPropertyChanged(nameof(HasNoAlerts));
    }

    private string ComputeUptime()
    {
        var boot = _hardware.Last?.OperatingSystem?.LastBootUpTime;
        var span = boot is { } b ? DateTime.Now - b : TimeSpan.FromMilliseconds(Environment.TickCount64);
        return ReportText.Duration(span);
    }

    private static Brush TempBrush(double? temp, double warn, double crit) =>
        temp is null ? TemperatureToBrushConverter.Unknown
        : temp >= crit ? TemperatureToBrushConverter.Hot
        : temp >= warn ? TemperatureToBrushConverter.Warm
        : temp < 40 ? TemperatureToBrushConverter.Cool
        : TemperatureToBrushConverter.Normal;

    private static string? Shorten(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var n = name.Replace("(R)", string.Empty).Replace("(TM)", string.Empty).Replace("NVIDIA GeForce", "GeForce").Replace("Intel Core", "Core").Trim();
        return n.Length > 28 ? n[..27] + "…" : n;
    }

    /// <summary>Runs a service call and logs failures instead of surfacing them; the Dashboard shows what it can.</summary>
    private async Task Guard(Func<Task> work, string what)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The {What} took too long and was skipped for now.", what);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The {What} failed; the Dashboard continues without it.", what);
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Dashboard operation failed.");
}
