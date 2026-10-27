using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.LiveUsage;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Live usage page (Task Manager's Performance tab): a list of CPU, memory, disks, active adapters and GPUs with
/// sparklines, and for the selected one a 60-second graph with Task Manager's details. Reads once a second while the
/// page or the compact window is showing and stops otherwise. The selected resource's graphs and details are in the
/// Details part.
/// </summary>
public sealed partial class LiveUsageViewModel : ViewModelBase
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ILiveUsageService _service;
    private readonly INotificationService _notifications;
    private readonly ILogger<LiveUsageViewModel> _logger;
    private readonly LiveUsagePreferencesStore _store;
    private readonly HashSet<object> _viewers = [];
    private DispatcherTimer? _timer;
    private bool _sampling;
    private bool _needsBaseline;
    private LiveUsageInventory? _inventory;
    private LiveUsageSample? _last;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText))]
    [NotifyPropertyChangedFor(nameof(PauseIcon))]
    private bool _isPaused;

    [ObservableProperty]
    private string _statusText = Loc.T("Reading your hardware…");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string? _warningText;

    [ObservableProperty]
    private bool _isCompactOpen;

    public LiveUsageViewModel(ILiveUsageService service, ISettingsService settings, INotificationService notifications, ILogger<LiveUsageViewModel> logger)
        : base(settings)
    {
        _service = service;
        _notifications = notifications;
        _logger = logger;
        _store = new LiveUsagePreferencesStore(settings.SettingsDirectory);
        Preferences = _store.Load();
        _perLogicalProcessor = Preferences.CpuGraph == CpuGraphMode.PerLogicalProcessor;
        _showKernelTimes = Preferences.ShowKernelTimes;
    }

    /// <summary>Raised when the user asks for the compact window (the window service opens or closes it).</summary>
    public event EventHandler? CompactViewRequested;

    public ObservableCollection<LiveResourceItem> Resources { get; } = [];

    /// <summary>Graph choices and the compact window's position; saved with <see cref="SavePreferences"/>.</summary>
    public LiveUsagePreferences Preferences { get; }

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    public string PauseText => IsPaused ? Loc.T("Resume") : Loc.T("Pause");

    public Wpf.Ui.Controls.SymbolRegular PauseIcon => IsPaused ? Wpf.Ui.Controls.SymbolRegular.Play24 : Wpf.Ui.Controls.SymbolRegular.Pause24;

    public override Task InitializeAsync(CancellationToken cancellationToken) =>
        RunBusyAsync(async ct =>
        {
            await LoadInventoryAsync(ct);

            // Demo mode supplies the last minute so the graphs are full straight away; a real PC starts empty.
            foreach (var earlier in _service.Backfill(LiveResourceItem.HistoryLength - 1))
            {
                Record(earlier);
            }

            Apply(await _service.SampleAsync(ct));
        }, Loc.T("Reading your hardware…"));

    public override Task RefreshAsync(CancellationToken cancellationToken) => RefreshDevicesAsync();

    /// <summary>Starts the once-a-second reading for <paramref name="viewer"/> (the page or the compact window).</summary>
    public void StartLive(object viewer)
    {
        _viewers.Add(viewer);
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _timer.Tick += OnTick;
        _timer.Start();
        if (_needsBaseline && IsInitialized && !IsPaused)
        {
            _ = SampleNowAsync();
        }
    }

    /// <summary>Stops reading once neither the page nor the compact window is showing.</summary>
    public void StopLive(object viewer)
    {
        _viewers.Remove(viewer);
        if (_viewers.Count > 0 || _timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
        _needsBaseline = true;
    }

    public void SavePreferences()
    {
        if (!_store.Save(Preferences))
        {
            _logger.LogDebug("The Live usage preferences could not be saved.");
        }
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (IsPaused)
        {
            _needsBaseline = true;
        }
        else
        {
            _ = SampleNowAsync();
        }

        UpdateStatus();
    }

    [RelayCommand]
    private void ToggleCompactView() => CompactViewRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private Task RefreshDevicesAsync() =>
        RunBusyAsync(async ct =>
        {
            await LoadInventoryAsync(ct);
            Apply(await _service.SampleAsync(ct));
        }, Loc.T("Looking for disks, adapters and graphics cards…"));

    private async Task LoadInventoryAsync(CancellationToken ct)
    {
        _inventory = await _service.GetInventoryAsync(ct);
        BuildResources(_inventory);
        WarningText = _inventory.Warnings.Count > 0 ? string.Join(" ", _inventory.Warnings) : null;
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (IsPaused || IsBusy || !IsInitialized)
        {
            return;
        }

        await SampleNowAsync();
    }

    private async Task SampleNowAsync()
    {
        if (_sampling || _inventory is null)
        {
            return;
        }

        _sampling = true;
        try
        {
            if (_needsBaseline)
            {
                _needsBaseline = false;
                _service.ResetRates();
            }

            Apply(await _service.SampleAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading live usage failed.");
            WarningText = Loc.T("The readings could not be refreshed. Trying again in a moment.");
        }
        finally
        {
            _sampling = false;
        }
    }

    /// <summary>Rebuilds the list from the inventory, keeping the history of resources that are still there.</summary>
    private void BuildResources(LiveUsageInventory inventory)
    {
        var old = Resources.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var items = new List<LiveResourceItem>
        {
            Reuse(old, "cpu", LiveResourceKind.Cpu, Loc.T("CPU"), inventory.Cpu.Name),
            Reuse(old, "memory", LiveResourceKind.Memory, Loc.T("Memory"), MemoryCaption(inventory.Memory)),
        };
        items.AddRange(inventory.Disks.Select(d =>
            Reuse(old, "disk:" + d.Index.ToString(CultureInfo.InvariantCulture), LiveResourceKind.Disk, DiskTitle(d), Loc.T(LiveUsageFormat.MediaLabel(d.Media)), d.Model)));
        items.AddRange(inventory.Networks.Select(n =>
            Reuse(old, "net:" + n.Id, LiveResourceKind.Network, LiveUsageFormat.NetworkLabel(n), n.Kind == NetworkKind.WiFi && n.Ssid is { } ssid ? ssid : n.AdapterName, n.AdapterName)));
        items.AddRange(inventory.Gpus.Select(g =>
            Reuse(old, "gpu:" + g.Luid, LiveResourceKind.Gpu, Loc.F("GPU {0}", g.Index), g.Name)));

        string? selectedId = SelectedResource?.Id ?? Preferences.SelectedResourceId;
        Resources.Clear();
        foreach (var item in items)
        {
            Resources.Add(item);
        }

        SelectedResource = Resources.FirstOrDefault(r => string.Equals(r.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? Resources.FirstOrDefault();
    }

    private static LiveResourceItem Reuse(Dictionary<string, LiveResourceItem> old, string id, LiveResourceKind kind, string title, string caption, string? device = null)
    {
        if (!old.TryGetValue(id, out var item))
        {
            item = new LiveResourceItem(id, kind, title, caption);
        }

        item.Title = title;
        item.Caption = caption;
        item.DeviceName = device ?? caption;
        return item;
    }

    private static string DiskTitle(DiskFacts disk) =>
        disk.DriveLetters.Count == 0
            ? Loc.F("Disk {0}", disk.Index)
            : Loc.F("Disk {0} ({1})", disk.Index, string.Join(" ", disk.DriveLetters));

    private static string MemoryCaption(MemoryFacts memory) =>
        memory.TotalBytes > 0 ? LiveUsageFormat.Bytes(memory.InstalledBytes ?? memory.TotalBytes) + (memory.MemoryType is { } type ? " " + type : string.Empty) : Loc.T("System memory");

    /// <summary>Adds a reading to the history and refreshes the list and the selected resource.</summary>
    private void Apply(LiveUsageSample sample)
    {
        Record(sample);
        WarningText = sample.Warnings.Count > 0 ? string.Join(" ", sample.Warnings) : _inventory?.Warnings.Count > 0 ? string.Join(" ", _inventory.Warnings) : null;
        foreach (var item in Resources)
        {
            item.Spark = item.Main.ToPaddedArray();
            item.SparkMaximum = item.Kind == LiveResourceKind.Network ? LiveUsageMath.NiceScale(item.Main.Max(), 100_000) : 100;
        }

        RefreshSelected();
        UpdateStatus();
    }

    /// <summary>Adds one reading of every resource to its history and updates the list's value lines.</summary>
    private void Record(LiveUsageSample sample)
    {
        _last = sample;
        foreach (var item in Resources)
        {
            switch (item.Kind)
            {
                case LiveResourceKind.Cpu:
                    RecordCpu(item, sample.Cpu);
                    break;
                case LiveResourceKind.Memory:
                    item.Main.Add(sample.Memory.UsedPercent);
                    item.ValueText = LiveUsageFormat.UsedOfTotal(sample.Memory.InUseBytes, sample.Memory.TotalBytes) + " (" + LiveUsageFormat.Percent(sample.Memory.UsedPercent) + ")";
                    break;
                case LiveResourceKind.Disk:
                    var disk = sample.Disks.FirstOrDefault(d => "disk:" + d.Index.ToString(CultureInfo.InvariantCulture) == item.Id);
                    item.Main.Add(disk?.ActivePercent ?? 0);
                    item.Second.Add(disk?.ReadBytesPerSecond ?? 0);
                    item.Third.Add(disk?.WriteBytesPerSecond ?? 0);
                    item.ValueText = LiveUsageFormat.Percent(disk?.ActivePercent ?? 0);
                    break;
                case LiveResourceKind.Network:
                    var net = sample.Networks.FirstOrDefault(n => "net:" + n.Id == item.Id);
                    item.Main.Add(net?.ReceiveBitsPerSecond ?? 0);
                    item.Second.Add(net?.SendBitsPerSecond ?? 0);
                    item.ValueText = Loc.F("S: {0}  R: {1}", LiveUsageFormat.BitsPerSecond(net?.SendBitsPerSecond ?? 0), LiveUsageFormat.BitsPerSecond(net?.ReceiveBitsPerSecond ?? 0));
                    if (net?.Ssid is { } ssid)
                    {
                        item.Caption = ssid;
                    }

                    break;
                case LiveResourceKind.Gpu:
                    RecordGpu(item, sample.Gpus.FirstOrDefault(g => "gpu:" + g.Luid == item.Id));
                    break;
            }
        }
    }

    private static void RecordCpu(LiveResourceItem item, CpuSample cpu)
    {
        item.Main.Add(cpu.UtilizationPercent);
        item.Second.Add(cpu.KernelPercent);
        if (item.Logical.Count != cpu.LogicalPercents.Count)
        {
            item.Logical.Clear();
            item.Logical.AddRange(cpu.LogicalPercents.Select(_ => new SampleRing(LiveResourceItem.HistoryLength)));
        }

        for (int i = 0; i < cpu.LogicalPercents.Count; i++)
        {
            item.Logical[i].Add(cpu.LogicalPercents[i]);
        }

        item.ValueText = cpu.SpeedMhz is { } mhz
            ? LiveUsageFormat.Percent(cpu.UtilizationPercent) + "  " + LiveUsageFormat.Speed(mhz)
            : LiveUsageFormat.Percent(cpu.UtilizationPercent);
    }

    private static void RecordGpu(LiveResourceItem item, GpuSample? gpu)
    {
        double use = gpu?.UtilizationPercent ?? 0;
        item.Main.Add(use);
        item.Third.Add(gpu?.DedicatedUsedBytes ?? 0);
        item.Fourth.Add(gpu?.SharedUsedBytes ?? 0);
        if (gpu is not null)
        {
            foreach (var (engine, value) in gpu.EnginePercents)
            {
                if (!item.Engines.TryGetValue(engine, out var ring))
                {
                    ring = new SampleRing(LiveResourceItem.HistoryLength);
                    item.Engines[engine] = ring;
                }

                ring.Add(value);
            }

            foreach (var (engine, ring) in item.Engines)
            {
                if (!gpu.EnginePercents.ContainsKey(engine))
                {
                    ring.Add(0);
                }
            }
        }

        item.ValueText = gpu?.TemperatureC is { } temp
            ? LiveUsageFormat.Percent(use) + string.Create(CultureInfo.InvariantCulture, $" ({temp:0} °C)")
            : LiveUsageFormat.Percent(use);
    }

    private void UpdateStatus() =>
        StatusText = IsPaused
            ? Loc.T("Paused. Press Resume to continue the graphs.")
            : _last is null
                ? Loc.T("Reading your hardware…")
                : Loc.F("Updates every second · last at {0}", _last.Timestamp.ToLocalTime().ToString("T", CultureInfo.CurrentCulture));

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Live usage page operation failed.");
}
