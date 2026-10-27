using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.ViewModels.Sensors;
using PcChecker.Core.Common;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using PcChecker.Core.Storage;

namespace PcChecker.App.ViewModels;

/// <summary>
/// View model for the Temperatures page: gauges for the hot parts, one expander per hardware item with its
/// temperature rows, an "all sensors" grid in advanced mode, an inline threshold editor and recent alerts.
/// Rows are updated in place from the sensor service's background snapshots.
/// </summary>
public sealed partial class SensorsViewModel : ViewModelBase
{
    private const int MaxAlerts = 20;
    private const int SparklinePoints = 60;

    private readonly ISensorService _sensors;
    private readonly INotificationService _notifications;
    private readonly ILogger<SensorsViewModel> _logger;
    private readonly PawnIoInstallFlow _pawnIoFlow;
    private readonly Dictionary<string, SensorRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SensorGroupViewModel> _groups = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _statsSince = DateTimeOffset.MinValue;
    private int _uiPending;
    private bool _subscribed;

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private string? _unavailableReason;

    [ObservableProperty]
    private string? _warningText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText))]
    private bool _isPaused;

    [ObservableProperty]
    private string _statusText = Loc.T("Waiting for the first reading…");

    [ObservableProperty]
    private string _windowText = "last 30 minutes";

    [ObservableProperty]
    private double _cpuTemp = double.NaN;

    [ObservableProperty]
    private double _gpuTemp = double.NaN;

    [ObservableProperty]
    private double _driveTemp = double.NaN;

    [ObservableProperty]
    private double _boardTemp = double.NaN;

    [ObservableProperty]
    private string _cpuLabel = "CPU";

    [ObservableProperty]
    private string _gpuLabel = "GPU";

    [ObservableProperty]
    private string _driveLabel = Loc.T("Hottest drive");

    [ObservableProperty]
    private string _boardLabel = Loc.T("Motherboard");

    [ObservableProperty]
    private bool _hasBoardTemp;

    [ObservableProperty]
    private bool _hasGpuTemp;

    [ObservableProperty]
    private bool _hasDriveTemp;

    [ObservableProperty]
    private double _cpuWarn;

    [ObservableProperty]
    private double _cpuCrit;

    [ObservableProperty]
    private double _gpuWarn;

    [ObservableProperty]
    private double _gpuCrit;

    [ObservableProperty]
    private double _diskWarn;

    [ObservableProperty]
    private double _diskCrit;

    [ObservableProperty]
    private bool _isThresholdPanelOpen;

    [ObservableProperty]
    private double _editCpuWarn;

    [ObservableProperty]
    private double _editCpuCrit;

    [ObservableProperty]
    private double _editGpuWarn;

    [ObservableProperty]
    private double _editGpuCrit;

    [ObservableProperty]
    private double _editDiskWarn;

    [ObservableProperty]
    private double _editDiskCrit;

    [ObservableProperty]
    private bool _editAlertsEnabled;

    [ObservableProperty]
    private double _editPollIntervalMs;

    [ObservableProperty]
    private double _editHistoryMinutes;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private bool _hasGroups;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlerts))]
    private int _alertCount;

    public SensorsViewModel(ISensorService sensors, ISettingsService settings, INotificationService notifications, PawnIoInstallFlow pawnIoFlow,
        ILogger<SensorsViewModel> logger)
        : base(settings)
    {
        _pawnIoFlow = pawnIoFlow;
        _sensors = sensors;
        _notifications = notifications;
        _logger = logger;
        AllSensorsView = CollectionViewSource.GetDefaultView(AllSensors);
        AllSensorsView.Filter = FilterRow;
        LoadThresholds(settings.Current.Monitoring);
        settings.Changed += (_, s) => DispatcherHelper.PostToUi(() => LoadThresholds(s.Monitoring));
    }

    public ObservableCollection<SensorGroupViewModel> Groups { get; } = [];

    public ObservableCollection<SensorRowViewModel> AllSensors { get; } = [];

    public ICollectionView AllSensorsView { get; }

    public ObservableCollection<AlertRowViewModel> Alerts { get; } = [];

    public bool HasAlerts => AlertCount > 0;

    public string PauseText => IsPaused ? Loc.T("Resume") : Loc.T("Pause");

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Subscribe();
        await RunBusyAsync(async ct =>
        {
            _sensors.Start();
            await WaitForFirstSnapshotAsync(TimeSpan.FromSeconds(6), ct);
            await RefreshPawnIoAsync(ct);
        }, Loc.T("Starting hardware monitoring…"));
    }

    public override Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_sensors.Latest is { } latest)
        {
            ApplySnapshot(latest);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (IsPaused)
        {
            StatusText = Loc.T("Paused — readings keep being recorded in the background.");
        }
        else if (_sensors.Latest is { } latest)
        {
            ApplySnapshot(latest);
        }
    }

    [RelayCommand]
    private void ResetMinMax()
    {
        _statsSince = DateTimeOffset.Now;
        (_sensors as ISensorServiceExtras)?.ResetMinMax();
        if (_sensors.Latest is { } latest)
        {
            ApplySnapshot(latest);
        }

        _notifications.Info(Loc.T("Statistics reset"), Loc.T("Minimum, maximum and average start again from now."));
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        await RunBusyAsync(async ct =>
        {
            if (_sensors is ISensorServiceExtras extras)
            {
                extras.Restart();
            }
            else
            {
                _sensors.Stop();
                _sensors.Start();
            }

            await WaitForFirstSnapshotAsync(TimeSpan.FromSeconds(8), ct);
        }, Loc.T("Restarting the hardware monitor…"));
    }

    [ObservableProperty]
    private bool _pawnIoNeedsAttention;

    [ObservableProperty]
    private string? _pawnIoTitle;

    [ObservableProperty]
    private string? _pawnIoText;

    private async Task RefreshPawnIoAsync(CancellationToken ct)
    {
        try
        {
            var status = await Task.Run(_pawnIoFlow.GetStatus, ct);
            PawnIoNeedsAttention = status.NeedsAttention;
            PawnIoTitle = status.State == PcChecker.Core.Sensors.PawnIo.PawnIoState.InstalledNotRunning
                ? Loc.T("PawnIO is installed but not running")
                : Loc.T("CPU and motherboard sensors need PawnIO");
            PawnIoText = status.Summary;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "PawnIO status unavailable.");
        }
    }

    [RelayCommand]
    private async Task InstallPawnIoAsync()
    {
        if (!await _pawnIoFlow.ConfirmInstallAsync())
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage);
            var result = await _pawnIoFlow.InstallAndReloadAsync(progress, ct);
            if (!result.Success)
            {
                ErrorMessage = result.Message;
            }

            await WaitForFirstSnapshotAsync(TimeSpan.FromSeconds(8), ct);
            await RefreshPawnIoAsync(ct);
        }, Loc.T("Installing PawnIO…"), cancellable: false);
    }

    [RelayCommand]
    private async Task ReloadSensorsAsync()
    {
        if (!await _pawnIoFlow.ConfirmReloadAsync())
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _pawnIoFlow.ReloadAsync(ct);
            if (result.Success)
            {
                _notifications.Info(Loc.T("Sensors reloaded"), result.Message);
            }
            else
            {
                ErrorMessage = result.Message;
            }

            await WaitForFirstSnapshotAsync(TimeSpan.FromSeconds(8), ct);
            await RefreshPawnIoAsync(ct);
        }, Loc.T("Reloading sensors…"));
    }

    [RelayCommand]
    private void ToggleThresholds() => IsThresholdPanelOpen = !IsThresholdPanelOpen;

    // The editor can also be opened from its expander header, so fill it with the saved values whenever it opens.
    partial void OnIsThresholdPanelOpenChanged(bool value)
    {
        if (value)
        {
            var m = Settings.Current.Monitoring;
            EditCpuWarn = m.CpuWarnC;
            EditCpuCrit = m.CpuCritC;
            EditGpuWarn = m.GpuWarnC;
            EditGpuCrit = m.GpuCritC;
            EditDiskWarn = m.DiskWarnC;
            EditDiskCrit = m.DiskCritC;
            EditAlertsEnabled = m.AlertsEnabled;
            EditPollIntervalMs = m.PollIntervalMs;
            EditHistoryMinutes = m.HistoryMinutes;
        }
    }

    [RelayCommand]
    private async Task SaveThresholdsAsync()
    {
        if (EditCpuWarn >= EditCpuCrit || EditGpuWarn >= EditGpuCrit || EditDiskWarn >= EditDiskCrit)
        {
            ErrorMessage = Loc.T("Each warning temperature must be lower than its critical temperature.");
            return;
        }

        var m = Settings.Current.Monitoring;
        m.CpuWarnC = Math.Clamp(EditCpuWarn, 30, 120);
        m.CpuCritC = Math.Clamp(EditCpuCrit, 31, 125);
        m.GpuWarnC = Math.Clamp(EditGpuWarn, 30, 120);
        m.GpuCritC = Math.Clamp(EditGpuCrit, 31, 125);
        m.DiskWarnC = Math.Clamp(EditDiskWarn, 25, 90);
        m.DiskCritC = Math.Clamp(EditDiskCrit, 26, 95);
        m.AlertsEnabled = EditAlertsEnabled;
        m.PollIntervalMs = (int)Math.Clamp(EditPollIntervalMs, 250, 10_000);
        m.HistoryMinutes = (int)Math.Clamp(EditHistoryMinutes, 1, 240);

        try
        {
            await Settings.SaveAsync();
            LoadThresholds(m);
            IsThresholdPanelOpen = false;
            ErrorMessage = null;
            _notifications.Success(Loc.T("Thresholds saved"), Loc.T("New limits apply to the gauges, colours and alerts right away."));
            if (_sensors.Latest is { } latest)
            {
                ApplySnapshot(latest);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving monitoring settings failed.");
            ErrorMessage = Loc.T("The settings could not be saved:") + " " + ex.Message;
        }
    }

    [RelayCommand]
    private void ClearAlerts()
    {
        Alerts.Clear();
        AlertCount = 0;
    }

    partial void OnSearchTextChanged(string? value) => AllSensorsView.Refresh();

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Temperatures page failed.");

    private bool FilterRow(object item)
    {
        if (item is not SensorRowViewModel row || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return row.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.HardwareName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.KindText.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        _sensors.SnapshotUpdated += OnSnapshotUpdated;
        _sensors.AlertRaised += OnAlertRaised;
    }

    private async Task WaitForFirstSnapshotAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (_sensors.Latest is { } latest && latest.Readings.Count > 0)
            {
                ApplySnapshot(latest);
                break;
            }

            if (!_sensors.IsAvailable && _sensors.UnavailableReason is { } reason && !reason.Contains("starting", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            await Task.Delay(150, cancellationToken);
        }

        RefreshAvailability();
    }

    private void RefreshAvailability()
    {
        IsAvailable = _sensors.IsAvailable;
        UnavailableReason = _sensors.IsAvailable ? null : _sensors.UnavailableReason ?? Loc.T("Hardware sensors are not available on this PC.");
        var warnings = (_sensors as ISensorServiceExtras)?.Warnings;
        WarningText = warnings is { Count: > 0 } ? string.Join(" ", warnings) : null;
    }

    private void OnSnapshotUpdated(object? sender, SensorSnapshot snapshot)
    {
        if (IsPaused || Interlocked.Exchange(ref _uiPending, 1) == 1)
        {
            return;
        }

        DispatcherHelper.PostToUi(() =>
        {
            try
            {
                if (!IsPaused)
                {
                    ApplySnapshot(snapshot);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Applying a sensor snapshot to the page failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _uiPending, 0);
            }
        });
    }

    private void OnAlertRaised(object? sender, TemperatureAlert alert)
    {
        DispatcherHelper.PostToUi(() =>
        {
            _rows.TryGetValue(alert.SensorId, out var row);
            var item = new AlertRowViewModel(alert, row?.Name);
            Alerts.Insert(0, item);
            while (Alerts.Count > MaxAlerts)
            {
                Alerts.RemoveAt(Alerts.Count - 1);
            }

            AlertCount = Alerts.Count;
            if (alert.Severity == Severity.Critical)
            {
                _notifications.Error(item.Title, item.Message);
            }
            else
            {
                _notifications.Warning(item.Title, item.Message);
            }
        });
    }

    private void ApplySnapshot(SensorSnapshot snapshot)
    {
        var monitoring = Settings.Current.Monitoring;
        var window = TimeSpan.FromMinutes(Math.Max(1, monitoring.HistoryMinutes));
        var now = snapshot.Timestamp;
        var touched = new HashSet<SensorGroupViewModel>();
        string? cpuName = TemperatureRoles.CpuNameOf(snapshot.Readings);

        foreach (var reading in snapshot.Readings)
        {
            bool isTemperature = reading.Kind == SensorKind.Temperature;
            var thresholds = isTemperature ? TemperatureRoles.ThresholdsFor(reading, monitoring, cpuName) : null;

            // A separate step so it also applies on top of role-based limits: extra drive sensors get higher limits.
            thresholds = SensorMath.AdjustForSensor(reading, thresholds, monitoring);

            if (!_rows.TryGetValue(reading.Id, out var row))
            {
                row = new SensorRowViewModel(reading);
                _rows[reading.Id] = row;
                AllSensors.Add(row);
                if (isTemperature)
                {
                    GroupFor(reading).Rows.Add(row);
                }
            }

            row.Update(reading, thresholds);
            if (isTemperature)
            {
                var history = _sensors.GetHistory(reading.Id, window);
                if (_statsSince > DateTimeOffset.MinValue)
                {
                    history = history.Where(h => h.Time >= _statsSince).ToList();
                }

                var values = history.Select(h => h.Value).ToList();
                row.UpdateStats(SensorMath.Summarize(values), SensorMath.Downsample(values, SparklinePoints));
                touched.Add(GroupFor(reading));
            }
            else
            {
                row.UpdateSessionMinMax(reading);
            }
        }

        foreach (var group in touched)
        {
            group.RefreshSummary();
        }

        UpdateGauges(snapshot, monitoring);
        WindowText = Loc.F("last {0} min", monitoring.HistoryMinutes);
        StatusText = Loc.F("Updated {0} · {1} sensors · {2} parts with temperatures", now.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture),
            snapshot.Readings.Count, Groups.Count);
        if (!IsAvailable && _sensors.IsAvailable)
        {
            RefreshAvailability();
        }
    }

    private SensorGroupViewModel GroupFor(SensorReading reading)
    {
        string key = reading.Hardware + "|" + reading.HardwareName;
        if (_groups.TryGetValue(key, out var group))
        {
            return group;
        }

        group = new SensorGroupViewModel(reading.HardwareName, reading.Hardware);
        _groups[key] = group;
        int index = 0;
        while (index < Groups.Count && (Groups[index].Order < group.Order
            || (Groups[index].Order == group.Order && string.Compare(Groups[index].HardwareName, group.HardwareName, StringComparison.OrdinalIgnoreCase) <= 0)))
        {
            index++;
        }

        Groups.Insert(index, group);
        HasGroups = true;
        return group;
    }

    private void UpdateGauges(SensorSnapshot snapshot, MonitoringSettings monitoring)
    {
        var temps = snapshot.OfKind(SensorKind.Temperature).Where(r => r.Value is { } v && !double.IsNaN(v)).ToList();

        var cpu = TemperatureRoles.PickCpuTemperature(temps);
        CpuTemp = cpu?.Value ?? double.NaN;
        CpuLabel = cpu is null ? "CPU" : "CPU · " + cpu.Name;

        // The headline is the primary (discrete) GPU; integrated graphics stay visible in the sensor list below.
        var primaryGpu = GpuSelection.PrimaryGpuKey(snapshot.Readings);
        var gpu = Pick(temps.Where(r => r.Hardware == HardwareKind.Gpu && GpuSelection.HardwareKey(r) == primaryGpu), "gpu core", "core", "hot spot");
        HasGpuTemp = gpu is not null;
        GpuTemp = gpu?.Value ?? double.NaN;
        GpuLabel = gpu is null ? "GPU" : "GPU · " + gpu.Name;

        // The drive gauge uses each drive's own temperature, never a controller sensor or a reported threshold.
        var drive = DriveTemperatureRules.HottestDrive(temps);
        HasDriveTemp = drive is not null;
        DriveTemp = drive?.Value ?? double.NaN;
        DriveLabel = drive is null ? Loc.T("Hottest drive") : Shorten(drive.HardwareName);

        var board = Pick(temps.Where(r => r.Hardware == HardwareKind.Motherboard), "vrm", "chipset", "pch", "system", "motherboard");
        HasBoardTemp = board is not null;
        BoardTemp = board?.Value ?? double.NaN;
        BoardLabel = board is null ? Loc.T("Motherboard") : Loc.F("Board · {0}", board.Name);

        (CpuWarn, CpuCrit) = TemperatureRoles.CpuThresholds(monitoring, cpu?.HardwareName);
        GpuWarn = monitoring.GpuWarnC;
        GpuCrit = monitoring.GpuCritC;
        DiskWarn = monitoring.DiskWarnC;
        DiskCrit = monitoring.DiskCritC;
    }

    /// <summary>Prefers the first reading whose name matches one of <paramref name="preferred"/> (in order), else the hottest.</summary>
    private static SensorReading? Pick(IEnumerable<SensorReading> readings, params string[] preferred)
    {
        var list = readings.ToList();
        foreach (var term in preferred)
        {
            var match = list.FirstOrDefault(r => r.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return list.OrderByDescending(r => r.Value).FirstOrDefault();
    }

    private static string Shorten(string name) => name.Length <= 26 ? name : name[..24] + "…";

    private void LoadThresholds(MonitoringSettings m)
    {
        CpuWarn = m.CpuWarnC;
        CpuCrit = m.CpuCritC;
        GpuWarn = m.GpuWarnC;
        GpuCrit = m.GpuCritC;
        DiskWarn = m.DiskWarnC;
        DiskCrit = m.DiskCritC;
        WindowText = Loc.F("last {0} min", m.HistoryMinutes);
    }
}
