using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Assistant;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// View model for the Overclock page: consent gate, capability detection, live readouts, GPU/CPU tuning,
/// stress test and the Claude advisor. Split across partial files by card.
/// </summary>
public sealed partial class OverclockViewModel : ViewModelBase
{
    private const int HistoryLength = 60;

    private readonly IOverclockService _overclock;
    private readonly ISensorService _sensors;
    private readonly IPcAssistantService _assistant;
    private readonly IHardwareInventoryService _hardware;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<OverclockViewModel> _logger;
    private readonly DispatcherTimer _liveTimer;
    private bool _liveTickRunning;
    private bool _pageVisible;
    private double _abortCpuDefault;
    private double _abortGpuDefault;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConsentGate))]
    private bool _consentAccepted;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptConsentCommand))]
    private bool _consentChecked;

    [ObservableProperty]
    private OverclockCapabilities? _capabilities;

    [ObservableProperty]
    private string _gpuName = "Detecting…";

    [ObservableProperty]
    private string _gpuVendorText = "GPU";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuSupportBrush))]
    private string _gpuSupportLevel = SupportLevel.Unsupported;

    [ObservableProperty]
    private string _gpuSupportNote = string.Empty;

    [ObservableProperty]
    private string _cpuName = "Detecting…";

    [ObservableProperty]
    private string _cpuVendorText = "CPU";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuSupportBrush))]
    private string _cpuSupportLevel = SupportLevel.Unsupported;

    [ObservableProperty]
    private string _cpuSupportNote = string.Empty;

    [ObservableProperty]
    private string _memoryText = "Detecting…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(XmpBrush))]
    private string _xmpState = XmpEvaluation.Unknown;

    [ObservableProperty]
    private string _xmpAdvice = string.Empty;

    [ObservableProperty]
    private bool _canTuneGpu;

    [ObservableProperty]
    private bool _canTuneCpu;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecoveryMessage))]
    private string? _recoveryMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProfileApplied))]
    private string? _activeProfileName;

    // Live readouts
    [ObservableProperty] private string _gpuCoreClockText = "—";
    [ObservableProperty] private string _gpuMemoryClockText = "—";
    [ObservableProperty] private string _gpuTempText = "—";
    [ObservableProperty] private string _gpuPowerText = "—";
    [ObservableProperty] private string _gpuFanText = "—";
    [ObservableProperty] private string _cpuTempText = "—";
    [ObservableProperty] private string _cpuClockText = "—";
    [ObservableProperty] private double _gpuTempValue;
    [ObservableProperty] private double _cpuTempValue;

    public OverclockViewModel(ISettingsService settings, IOverclockService overclock, ISensorService sensors, IPcAssistantService assistant,
        IHardwareInventoryService hardware, IDialogService dialogs, INotificationService notifications, INavigationService navigation,
        ILogger<OverclockViewModel> logger)
        : base(settings)
    {
        _overclock = overclock;
        _sensors = sensors;
        _assistant = assistant;
        _hardware = hardware;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;

        _consentAccepted = settings.Current.Overclock.ConsentAccepted;
        if (!_consentAccepted && AppOptions.Current.ScreenshotsRequested)
        {
            // Unattended screenshot runs show the populated page; nothing is saved and nothing is applied.
            _consentAccepted = true;
        }

        _activeProfileName = settings.Current.Overclock.ActiveProfileName;
        _abortCpuDefault = _abortCpuTemp = settings.Current.Overclock.MaxCpuTempC;
        _abortGpuDefault = _abortGpuTemp = settings.Current.Overclock.MaxGpuTempLimitC;
        settings.Changed += OnOverclockSettingsChanged;
        LoadLimitsFromSettings();
        RebuildProfiles();

        _liveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _liveTimer.Tick += async (_, _) => await LiveTickAsync();
    }

    public ObservableCollection<string> Notes { get; } = [];
    public ObservableCollection<SupportMatrixRow> SupportRows { get; } = [];
    public ObservableCollection<double> GpuTempHistory { get; } = [];
    public ObservableCollection<double> CpuTempHistory { get; } = [];

    public bool ShowConsentGate => !ConsentAccepted;

    public bool IsProfileApplied => !string.IsNullOrWhiteSpace(ActiveProfileName);

    public bool HasRecoveryMessage => !string.IsNullOrWhiteSpace(RecoveryMessage);

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public System.Windows.Media.Brush GpuSupportBrush => SupportMatrixRow.BrushFor(GpuSupportLevel);

    public System.Windows.Media.Brush CpuSupportBrush => SupportMatrixRow.BrushFor(CpuSupportLevel);

    public System.Windows.Media.Brush XmpBrush => SupportMatrixRow.BrushFor(XmpState switch
    {
        XmpEvaluation.Enabled => SupportLevel.Full,
        XmpEvaluation.Disabled => SupportLevel.MonitorOnly,
        _ => SupportLevel.Unsupported,
    });

    public string Disclaimer =>
        "Overclocking pushes parts beyond the maker's defaults. It can cause crashes, data loss, extra heat and in rare cases permanent damage, and it may void your warranty. PC Checker applies limits, watches temperatures and can undo changes, but the decision and the risk are yours.";

    public IReadOnlyList<string> ConsentPoints { get; } =
    [
        "Instability: a too-high offset can freeze games or the whole PC. Save your work first.",
        "Heat: higher clocks and power limits raise temperatures; the watchdog stops the stress test and resets when limits are hit.",
        "Warranty: some makers void coverage for overclocked parts, even software overclocks.",
        "Safety features: user limits (Settings), a crash watchdog that reverts on the next start, a restore point before changes, and a one-click Reset to defaults.",
        "What PC Checker cannot do: change BIOS settings, tune integrated graphics, or generate GPU load for you (the GPU test is guided).",
    ];

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RunBusyAsync(async ct =>
        {
            await DetectAsync(ct);
            await LiveTickAsync();
        }, "Checking what this PC can tune…");
        StartLive();
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunBusyAsync(DetectAsync, "Detecting again…");

    [RelayCommand]
    private Task DetectAgain() => RefreshAsync(CancellationToken.None);

    [RelayCommand(CanExecute = nameof(ConsentChecked))]
    private async Task AcceptConsent()
    {
        Settings.Current.Overclock.ConsentAccepted = true;
        Settings.Current.Overclock.ConsentAcceptedUtc = DateTimeOffset.UtcNow;
        await Settings.SaveAsync();
        ConsentAccepted = true;
        _notifications.Success("Overclock unlocked", "You can now tune the GPU and CPU. Start small and stress test every change.");
    }

    [RelayCommand]
    private void DismissRecovery() => RecoveryMessage = null;

    [RelayCommand]
    private void DismissStatus() => StatusMessage = null;

    [RelayCommand]
    private void GoToMemoryPage() => _navigation.Navigate(typeof(MemoryPage));

    [RelayCommand]
    private void GoToFansPage() => _navigation.Navigate(typeof(FansPage));

    [RelayCommand]
    private void GoToSettingsPage() => _navigation.Navigate(typeof(SettingsPage));

    /// <summary>Shown on the page when the app reverted a profile after a crash (set by App startup).</summary>
    public void ShowRecoveryNotice(string message) => DispatcherHelper.PostToUi(() => RecoveryMessage = message);

    /// <summary>Page Loaded: live readouts poll the driver only while this page is on screen.</summary>
    public void OnPageLoaded()
    {
        _pageVisible = true;
        if (IsInitialized)
        {
            StartLive();
        }
    }

    /// <summary>Page Unloaded: stop polling while another page is shown (the view model is a singleton).</summary>
    public void OnPageUnloaded()
    {
        _pageVisible = false;
        _liveTimer.Stop();
    }

    private void OnOverclockSettingsChanged(object? sender, AppSettings settings) => DispatcherHelper.PostToUi(() => FollowSettingsDefaults(settings.Overclock));

    /// <summary>The stress-test abort temperatures follow Settings until the user types their own value.</summary>
    private void FollowSettingsDefaults(OverclockSettings overclock)
    {
        if (AbortCpuTemp == _abortCpuDefault)
        {
            AbortCpuTemp = overclock.MaxCpuTempC;
        }

        if (AbortGpuTemp == _abortGpuDefault)
        {
            AbortGpuTemp = overclock.MaxGpuTempLimitC;
        }

        _abortCpuDefault = overclock.MaxCpuTempC;
        _abortGpuDefault = overclock.MaxGpuTempLimitC;
        LoadLimitsFromSettings();
    }

    private async Task DetectAsync(CancellationToken cancellationToken)
    {
        var caps = await _overclock.DetectAsync(cancellationToken);
        Capabilities = caps;
        ApplyCapabilities(caps);
    }

    private void ApplyCapabilities(OverclockCapabilities caps)
    {
        GpuName = caps.GpuName ?? "No graphics card recognised";
        GpuVendorText = GpuVendorDetector.DisplayName(caps.GpuVendor);
        CanTuneGpu = caps.CanTuneGpu;
        GpuSupportLevel = caps.GpuMethod switch
        {
            GpuControlMethod.NvApi => SupportLevel.Full,
            GpuControlMethod.AmdAdl or GpuControlMethod.IntelIgcl => SupportLevel.Experimental,
            _ => SupportLevel.MonitorOnly,
        };
        GpuSupportNote = caps.GpuMethod switch
        {
            GpuControlMethod.NvApi => "Tuned through the NVIDIA driver (NVAPI).",
            GpuControlMethod.AmdAdl => "Tuned through AMD Overdrive8 (experimental).",
            GpuControlMethod.IntelIgcl => "Tuned through the Intel Graphics Control Library (experimental).",
            _ => "Readouts only; use the vendor's software for tuning.",
        };

        CpuName = caps.CpuName ?? "Processor not recognised";
        CpuVendorText = VendorSoftwareCatalog.CpuVendorName(caps.CpuName);
        CanTuneCpu = caps.CanTuneCpu && caps.CpuMethod is CpuControlMethod.IntelXtuCli or CpuControlMethod.RyzenAdj;
        CpuSupportLevel = caps.CpuMethod switch
        {
            CpuControlMethod.IntelXtuCli => SupportLevel.Partial,
            CpuControlMethod.RyzenAdj => SupportLevel.Experimental,
            _ => SupportLevel.MonitorOnly,
        };
        CpuSupportNote = caps.CpuMethod switch
        {
            CpuControlMethod.IntelXtuCli => "Ratio, voltage offset and power limit through Intel XTU.",
            CpuControlMethod.RyzenAdj => "Power limits through ryzenadj; ratios stay in the BIOS.",
            CpuControlMethod.RyzenMaster or CpuControlMethod.RyzenMasterCli => "Ryzen Master is installed; open it for CPU tuning.",
            _ => "Readouts only; CPU settings are changed in the BIOS.",
        };

        XmpState = caps.XmpState;
        MemoryText = caps.MemoryConfiguredMhz is { } c && caps.MemoryMaxMhz is { } m
            ? $"Running at {c} MT/s, rated {m} MT/s"
            : "Memory speed not reported";
        XmpAdvice = caps.XmpState switch
        {
            XmpEvaluation.Disabled => "XMP / EXPO is off. Enabling it in the BIOS is the safest speed-up you can make.",
            XmpEvaluation.Enabled => "XMP / EXPO is active; your memory already runs at its rated speed.",
            _ => "Could not tell whether XMP / EXPO is on; check the BIOS memory page.",
        };

        Notes.Clear();
        foreach (var n in caps.Notes)
        {
            Notes.Add(n);
        }

        SupportRows.Clear();
        foreach (var (feature, vendor, level, note) in caps.SupportMatrix)
        {
            SupportRows.Add(new SupportMatrixRow(feature, vendor, level, note));
        }

        ApplyGpuLimits(caps);
        UpdateVendorTools(caps);
        UpdateAdvancedInfo(caps);
        AdvisorConfigured = _assistant.IsConfigured;
    }

    private void StartLive()
    {
        try
        {
            _sensors.Start();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sensor service could not be started from the Overclock page.");
        }

        if (_pageVisible && !_liveTimer.IsEnabled)
        {
            _liveTimer.Start();
        }
    }

    private async Task LiveTickAsync()
    {
        if (_liveTickRunning)
        {
            return;
        }

        _liveTickRunning = true;
        try
        {
            var state = await _overclock.GetStateAsync(CancellationToken.None);
            var live = SensorPicker.Pick(_sensors.Latest);
            GpuCoreClockText = Format(state.GpuCoreClockMhz, "0");
            GpuMemoryClockText = Format(state.GpuMemoryClockMhz, "0");
            GpuTempText = Format(state.GpuTempC, "0");
            GpuPowerText = Format(state.GpuPowerW, "0");
            GpuFanText = live.GpuFanPercent is { } fan ? fan.ToString("0") : live.GpuFanRpm is { } rpm ? $"{rpm:0} rpm" : "auto";
            CpuTempText = Format(state.CpuTempC, "0");
            CpuClockText = Format(state.CpuClockMhz, "0");
            if (state.GpuTempC is { } g)
            {
                GpuTempValue = g;
                Push(GpuTempHistory, g);
            }

            if (state.CpuTempC is { } c)
            {
                CpuTempValue = c;
                Push(CpuTempHistory, c);
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Live readout failed.");
        }
        finally
        {
            _liveTickRunning = false;
        }
    }

    private static void Push(ObservableCollection<double> history, double value)
    {
        history.Add(value);
        while (history.Count > HistoryLength)
        {
            history.RemoveAt(0);
        }
    }

    private static string Format(double? value, string format) => value is { } v && !double.IsNaN(v) ? v.ToString(format) : "—";

    private static string Format(int? value, string format) => value is { } v ? v.ToString(format) : "—";

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Overclock page operation failed.");
}
