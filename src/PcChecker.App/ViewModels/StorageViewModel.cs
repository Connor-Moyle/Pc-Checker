using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Settings;
using PcChecker.Core.Storage;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Storage page: physical drives with health, a read-only surface scan with a live block map, a benchmark against
/// typical figures, check disk / repair / optimize tools with streamed output, and recent disk error events.
/// </summary>
public sealed partial class StorageViewModel : ViewModelBase
{
    private const int EventDays = 30;

    private readonly IStorageToolsService _storage;
    private readonly IDriveInventoryService _drives;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;

    /// <summary>Set while the demo showcase runs so its pre-baked scan and benchmark do not pop snackbars into screenshots.</summary>
    private bool _quietNotifications;
    private readonly ILogger<StorageViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedDevice))]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunBenchmarkCommand))]
    private StorageDeviceItem? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedVolume))]
    [NotifyCanExecuteChangedFor(nameof(RunBenchmarkCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckDiskScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(SpotFixCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScheduleFullRepairCommand))]
    [NotifyCanExecuteChangedFor(nameof(RepairVolumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(OptimizeCommand))]
    private VolumeOption? _selectedVolume;

    [ObservableProperty]
    private string _devicesSummary = "No drives found yet.";

    [ObservableProperty]
    private string _eventsSummary = "No disk errors in the last 30 days.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEvents))]
    private int _eventCount;

    [ObservableProperty]
    private Severity _eventsWorstSeverity = Severity.Info;

    public StorageViewModel(IStorageToolsService storage, IDriveInventoryService drives, IDialogService dialogs, INotificationService notifications,
        ISettingsService settings, ILogger<StorageViewModel> logger)
        : base(settings)
    {
        _storage = storage;
        _drives = drives;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        ScanEstimateText = "Select a drive to see how long a scan would take.";
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy))
            {
                RaiseToolStateChanged();
            }
        };
    }

    public ObservableCollection<StorageDeviceItem> Devices { get; } = [];

    public ObservableCollection<VolumeOption> DeviceVolumes { get; } = [];

    public ObservableCollection<VendorLink> VendorTools { get; } = [];

    public ObservableCollection<DiskEventItem> DiskEvents { get; } = [];

    public bool HasSelectedDevice => SelectedDevice is not null;

    public bool HasSelectedVolume => SelectedVolume is not null;

    public bool HasEvents => EventCount > 0;

    public bool HasVendorTools => VendorTools.Count > 0;

    public string EventsTitle => $"Disk error events (last {EventDays} days)";

    /// <summary>True while any long operation (scan, benchmark, tool) runs, used to disable the other tool buttons.</summary>
    public bool IsAnyToolRunning => IsScanning || IsBenchmarking || IsToolRunning;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        if (AppOptions.Current.IsDemo && Devices.Count > 0)
        {
            await RunDemoShowcaseAsync(cancellationToken);
        }
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunBusyAsync(LoadAsync, "Looking at your drives…");

    /// <summary>Refresh rebuilds the device list, which re-selects a device and clears results, so it waits while a scan, benchmark or tool runs.</summary>
    private bool CanRefresh() => !IsAnyToolRunning && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task Refresh() => RefreshAsync(CancellationToken.None);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var devices = await _storage.GetDevicesAsync(cancellationToken);
        var tools = _storage.GetVendorTools(devices);
        int previous = SelectedDevice?.Number ?? -1;

        Devices.Clear();
        foreach (var device in devices.OrderBy(d => d.Number < 0 ? 1 : 0).ThenBy(d => d.Number))
        {
            Devices.Add(new StorageDeviceItem(device, StorageVendorRules.ToolFor(device.Model)));
        }

        VendorTools.Clear();
        foreach (var tool in tools)
        {
            VendorTools.Add(tool);
        }

        OnPropertyChanged(nameof(HasVendorTools));
        DevicesSummary = BuildDevicesSummary(devices);
        SelectedDevice = Devices.FirstOrDefault(d => d.Number == previous) ?? Devices.FirstOrDefault(d => d.IsSystemDisk) ?? Devices.FirstOrDefault();

        try
        {
            var events = await _storage.GetDiskErrorEventsAsync(EventDays, cancellationToken);
            DiskEvents.Clear();
            foreach (var evt in events)
            {
                DiskEvents.Add(new DiskEventItem(evt));
            }

            EventCount = DiskEvents.Count;
            EventsWorstSeverity = DiskEventCatalog.Worst(events);
            EventsSummary = BuildEventsSummary(events);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disk events could not be read.");
            EventsSummary = "Disk events could not be read: " + ex.Message;
        }
    }

    partial void OnSelectedDeviceChanged(StorageDeviceItem? value)
    {
        DeviceVolumes.Clear();
        if (value is not null)
        {
            foreach (var letter in value.Device.Volumes)
            {
                string label = $"{letter}  —  {value.Model} ({value.TypeText})";
                bool system = value.IsSystemDisk && string.Equals(StorageCodes.NormalizeDriveLetter(letter), StorageCodes.NormalizeDriveLetter(SafeSystemRoot()), StringComparison.OrdinalIgnoreCase);
                DeviceVolumes.Add(new VolumeOption(letter, label, value.MediaType, value.BusType, system));
            }
        }

        SelectedVolume = DeviceVolumes.FirstOrDefault();
        UpdateDriveDetails(value);
        UpdateScanEstimate();
        ClearScanResult();
        ClearBenchmarkResult();
    }

    [RelayCommand]
    private void OpenDiskManagement()
    {
        var result = ShellLauncher.Open("diskmgmt.msc");
        if (!result.Success)
        {
            _notifications.Warning("Disk Management", result.Message);
        }
    }

    [RelayCommand]
    private void OpenVendorTool(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var result = ShellLauncher.Open(url);
        if (!result.Success)
        {
            _notifications.Warning("Vendor tool", result.Message);
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Storage page operation failed.");

    private void RaiseToolStateChanged()
    {
        OnPropertyChanged(nameof(IsAnyToolRunning));
        RefreshCommand.NotifyCanExecuteChanged();
        StartScanCommand.NotifyCanExecuteChanged();
        RunBenchmarkCommand.NotifyCanExecuteChanged();
        CheckDiskScanCommand.NotifyCanExecuteChanged();
        SpotFixCommand.NotifyCanExecuteChanged();
        ScheduleFullRepairCommand.NotifyCanExecuteChanged();
        RepairVolumeCommand.NotifyCanExecuteChanged();
        OptimizeCommand.NotifyCanExecuteChanged();
        ReadSmartNowCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Shows a notification unless the demo showcase is running.</summary>
    private void Notify(Action show)
    {
        if (!_quietNotifications)
        {
            show();
        }
    }

    /// <summary>In demo mode, pre-run a quick scan and a benchmark so the page (and screenshots) show real content. Notifications stay quiet meanwhile.</summary>
    private async Task RunDemoShowcaseAsync(CancellationToken cancellationToken)
    {
        _quietNotifications = true;
        try
        {
            var hdd = Devices.FirstOrDefault(d => d.MediaType == "HDD");
            if (hdd is not null)
            {
                SelectedDevice = hdd;
                SelectedScanMode = ScanMode.Quick;
                await RunScanCoreAsync(hdd, cancellationToken);
            }

            if (SelectedVolume is { } volume)
            {
                await RunBenchmarkCoreAsync(volume, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Navigating away during the showcase is fine.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Demo showcase could not complete.");
        }
        finally
        {
            _quietNotifications = false;
        }
    }

    private static string BuildDevicesSummary(IReadOnlyList<StorageDevice> devices)
    {
        if (devices.Count == 0)
        {
            return "No physical drives were found. PC Checker needs administrator rights to list drives.";
        }

        // Storage spaces are made from pool members, so only physical drives count toward the total.
        var physical = devices.Where(d => d.Drive?.Role != DriveRole.StorageSpace).ToList();
        int warn = physical.Count(d => d.Health == HealthStatus.Warning);
        int crit = physical.Count(d => d.Health == HealthStatus.Critical);
        int asleep = physical.Count(d => d.Drive?.Power == DrivePowerState.Asleep);
        long total = physical.Sum(d => d.SizeBytes);
        string health = crit > 0 ? Loc.F("{0} with a Bad verdict", crit)
            : warn > 0 ? Loc.F("{0} need caution", warn)
            : Loc.T("no problems reported");
        string sleeping = asleep > 0 ? " " + Loc.F("{0} asleep (not woken).", asleep) : string.Empty;
        string count = physical.Count == 1 ? Loc.T("1 physical drive") : Loc.F("{0} physical drives", physical.Count);
        return Loc.F("{0}, {1} in total; {2}.", count, DriveText.Decimal(total), health) + sleeping;
    }

    private static string BuildEventsSummary(IReadOnlyList<DiskErrorEvent> events)
    {
        if (events.Count == 0)
        {
            return $"No disk errors were logged in the last {EventDays} days. That is what a healthy system looks like.";
        }

        var worst = DiskEventCatalog.Worst(events);
        int serious = events.Count(e => DiskEventCatalog.Explain(e.EventId).Severity >= Severity.High);
        return worst switch
        {
            >= Severity.High => $"{events.Count} disk event{(events.Count == 1 ? string.Empty : "s")} in the last {EventDays} days, {serious} serious. Read the explanations below and run a surface scan on the affected drive.",
            Severity.Medium => $"{events.Count} disk event{(events.Count == 1 ? string.Empty : "s")} in the last {EventDays} days. Nothing critical, but worth a look if they repeat.",
            _ => $"{events.Count} informational disk event{(events.Count == 1 ? string.Empty : "s")} in the last {EventDays} days. Nothing to worry about.",
        };
    }

    private static string? SafeSystemRoot()
    {
        try
        {
            return System.IO.Path.GetPathRoot(Environment.SystemDirectory);
        }
        catch
        {
            return null;
        }
    }
}
