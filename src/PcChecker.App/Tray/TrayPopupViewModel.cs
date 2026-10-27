using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.Core.Common;
using PcChecker.Core.Fans;
using PcChecker.Core.Power;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.App.Tray;

/// <summary>What the tray popup's buttons do; implemented by <see cref="TrayIconService"/>.</summary>
public interface ITrayActions
{
    void ShowMainWindow();
    void ToggleOverlay();
    Task QuickCleanAsync();
    Task<bool> SwitchFanProfileAsync(string profileName);
    void ExitApplication();
}

/// <summary>
/// The compact tray popup: CPU, GPU and hottest-drive temperatures with colours, CPU/GPU load, RAM, power draw, the
/// active fan profile with a quick switch, the overlay toggle and the alert count. Refreshed by the tray service.
/// </summary>
public sealed partial class TrayPopupViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ISensorService _sensors;
    private readonly IPowerService _power;
    private readonly IFanControlService _fans;
    private readonly ITrayActions _actions;
    private bool _loadingProfiles;

    [ObservableProperty] private string _cpuTemp = TrayText.Placeholder;
    [ObservableProperty] private HealthStatus _cpuLevel;
    [ObservableProperty] private string _gpuTemp = TrayText.Placeholder;
    [ObservableProperty] private HealthStatus _gpuLevel;
    [ObservableProperty] private string _driveTemp = TrayText.Placeholder;
    [ObservableProperty] private HealthStatus _driveLevel;
    [ObservableProperty] private string _cpuLoad = TrayText.Placeholder;
    [ObservableProperty] private string _gpuLoad = TrayText.Placeholder;
    [ObservableProperty] private string _ramUse = TrayText.Placeholder;
    [ObservableProperty] private string _powerDraw = TrayText.Placeholder;
    [ObservableProperty] private int _alertCount;
    [ObservableProperty] private string _alertsText = TrayText.Alerts(0);
    [ObservableProperty] private bool _overlayOn;
    [ObservableProperty] private bool _canSwitchFans;
    [ObservableProperty] private string _fanNote = string.Empty;
    [ObservableProperty] private string? _selectedFanProfile;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string _sensorNote = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QuickCleanCommand))]
    private bool _isCleaning;

    public TrayPopupViewModel(ISettingsService settings, ISensorService sensors, IPowerService power, IFanControlService fans, ITrayActions actions)
    {
        _settings = settings;
        _sensors = sensors;
        _power = power;
        _fans = fans;
        _actions = actions;
    }

    public ObservableCollection<string> FanProfiles { get; } = [];

    /// <summary>Re-reads every value. Call on the UI thread.</summary>
    public void Refresh(bool overlayVisible)
    {
        var monitoring = _settings.Current.Monitoring;
        var readings = TrayReadings.From(_sensors.Latest);
        CpuTemp = TrayText.Temperature(readings.CpuTempC);
        CpuLevel = TrayText.Level(readings.CpuTempC, monitoring.CpuWarnC, monitoring.CpuCritC);
        GpuTemp = TrayText.Temperature(readings.GpuTempC);
        GpuLevel = TrayText.Level(readings.GpuTempC, monitoring.GpuWarnC, monitoring.GpuCritC);
        DriveTemp = TrayText.Temperature(readings.DriveTempC);
        DriveLevel = TrayText.Level(readings.DriveTempC, monitoring.DiskWarnC, monitoring.DiskCritC);
        CpuLoad = TrayText.Percent(readings.CpuLoadPercent);
        GpuLoad = TrayText.Percent(readings.GpuLoadPercent);
        RamUse = TrayText.Percent(readings.RamPercent);

        var power = _power.Latest;
        PowerDraw = power?.EstimatedTotalW is { } w ? TrayText.Watts(w, estimated: true) : TrayText.Placeholder;
        AlertCount = TrayText.ActiveAlerts(readings, monitoring);
        AlertsText = TrayText.Alerts(AlertCount);
        OverlayOn = overlayVisible;
        SensorNote = _sensors.IsAvailable ? string.Empty : (_sensors.UnavailableReason ?? "Sensors are not available.");
        RefreshFanProfiles();
    }

    private void RefreshFanProfiles()
    {
        _loadingProfiles = true;
        try
        {
            var names = _fans.BuiltInProfiles.Select(p => p.Name)
                .Concat(_settings.Current.Fans.Profiles.Select(p => p.Name))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!FanProfiles.SequenceEqual(names))
            {
                FanProfiles.Clear();
                foreach (var name in names)
                {
                    FanProfiles.Add(name);
                }
            }

            CanSwitchFans = _fans.IsAvailable && FanProfiles.Count > 0;
            FanNote = _fans.IsAvailable ? string.Empty : (_fans.UnavailableReason ?? "Fan control is not available on this PC.");
            var active = _fans.ActiveProfile?.Name ?? _settings.Current.Fans.ActiveProfileName;
            SelectedFanProfile = FanProfiles.FirstOrDefault(n => string.Equals(n, active, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _loadingProfiles = false;
        }
    }

    partial void OnSelectedFanProfileChanged(string? oldValue, string? newValue)
    {
        if (_loadingProfiles || string.IsNullOrWhiteSpace(newValue))
        {
            return;
        }

        _ = SwitchAsync(oldValue, newValue);
    }

    private async Task SwitchAsync(string? previous, string name)
    {
        Status = $"Applying \"{name}\"…";
        bool ok = await _actions.SwitchFanProfileAsync(name);
        Status = ok ? $"Fan profile \"{name}\" is active." : null;
        if (!ok)
        {
            _loadingProfiles = true;
            SelectedFanProfile = previous;
            _loadingProfiles = false;
        }
    }

    [RelayCommand]
    private void OpenApp() => _actions.ShowMainWindow();

    [RelayCommand]
    private void ToggleOverlay() => _actions.ToggleOverlay();

    [RelayCommand(CanExecute = nameof(CanQuickClean))]
    private async Task QuickCleanAsync()
    {
        IsCleaning = true;
        Status = "Cleaning temporary files…";
        try
        {
            await _actions.QuickCleanAsync();
        }
        finally
        {
            IsCleaning = false;
            Status = null;
        }
    }

    private bool CanQuickClean() => !IsCleaning;

    [RelayCommand]
    private void Exit() => _actions.ExitApplication();
}
