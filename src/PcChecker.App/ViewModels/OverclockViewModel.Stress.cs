using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Overclocking.GpuLoad;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Stress test card: target (CPU / GPU / both), the built-in GPU load (adapter, intensity, result checking) or the
/// guided "own game" option, duration, abort temperatures, live readouts and the verdict. A stock stress test needs
/// no overclock consent; consent only gates applying clocks.
/// </summary>
public sealed partial class OverclockViewModel
{
    private CancellationTokenSource? _stressCts;
    private bool _adaptersLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StressTargetHelp), nameof(ShowGpuStressOptions), nameof(ShowBuiltInGpuOptions), nameof(ShowAdapterPicker), nameof(ShowSingleAdapter), nameof(GpuLoadUnavailable))]
    private StressTarget _selectedStressTarget = StressTarget.Gpu;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StressTargetHelp), nameof(ShowBuiltInGpuOptions), nameof(ShowAdapterPicker), nameof(ShowSingleAdapter), nameof(GpuLoadUnavailable))]
    private bool _useOwnGame;

    [ObservableProperty] private GpuAdapterChoice? _selectedGpuAdapter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IntensityNote))]
    private GpuLoadIntensity _selectedIntensity = GpuLoadIntensity.Medium;

    [ObservableProperty] private bool _verifyGpuResults = true;
    [ObservableProperty] private double _stressMinutes = 2;
    // Initialised from OverclockSettings.MaxCpuTempC / MaxGpuTempLimitC in the constructor.
    [ObservableProperty] private double _abortCpuTemp;
    [ObservableProperty] private double _abortGpuTemp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StressIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartStressCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopStressCommand))]
    private bool _stressRunning;

    [ObservableProperty] private double _stressProgress;
    [ObservableProperty] private string _stressDetail = "Not started.";
    [ObservableProperty] private StressTestResult? _stressResult;
    [ObservableProperty] private string? _stressSummary;
    [ObservableProperty] private string _stressTitle = "Result";
    [ObservableProperty] private InfoBarSeverity _stressSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStressPowerMessage))]
    private string? _stressPowerMessage;

    [ObservableProperty] private InfoBarSeverity _stressPowerSeverity = InfoBarSeverity.Warning;

    // Live readouts while a test runs.
    [ObservableProperty] private bool _showLiveReadouts;
    [ObservableProperty] private bool _liveShowsGpuLoad;
    [ObservableProperty] private bool _liveShowsCpu;
    [ObservableProperty] private string _liveElapsedText = "0:00";
    [ObservableProperty] private string _liveRemainingText = string.Empty;
    [ObservableProperty] private string _liveDispatchRateText = "—";
    [ObservableProperty] private string _liveAdapterText = string.Empty;
    [ObservableProperty] private string _liveErrorsText = "0";
    [ObservableProperty] private string _liveErrorsDetail = string.Empty;
    [ObservableProperty] private string _liveGpuText = "—";
    [ObservableProperty] private string _liveGpuDetail = string.Empty;
    [ObservableProperty] private string _liveCpuText = "—";
    [ObservableProperty] private string _liveCpuDetail = string.Empty;

    public IReadOnlyList<StressTarget> StressTargets { get; } = Enum.GetValues<StressTarget>();

    public IReadOnlyList<GpuLoadIntensity> Intensities { get; } = Enum.GetValues<GpuLoadIntensity>();

    public ObservableCollection<GpuAdapterChoice> GpuAdapters { get; } = [];

    public ObservableCollection<string> StressReasons { get; } = [];

    public IReadOnlyList<string> StressWarnings { get; } =
    [
        "Fans get loud and the PC gets warm: that is the point of the test.",
        "On a laptop, plug in the charger. The test drains a battery fast and will not start below 20 %.",
        "If the graphics driver resets, the screen goes black for a few seconds; the test then stops and reports it.",
        "You can stop at any time with Stop; everything shuts down within a second.",
    ];

    public bool StressIdle => !StressRunning;

    public bool HasStressResult => StressResult is not null;

    public bool HasStressReasons => StressReasons.Count > 0;

    public bool HasStressPowerMessage => !string.IsNullOrWhiteSpace(StressPowerMessage);

    public bool ShowGpuStressOptions => SelectedStressTarget != StressTarget.Cpu;

    public bool ShowBuiltInGpuOptions => ShowGpuStressOptions && !UseOwnGame;

    public bool ShowAdapterPicker => ShowBuiltInGpuOptions && GpuAdapters.Count > 1;

    public bool ShowSingleAdapter => ShowBuiltInGpuOptions && GpuAdapters.Count == 1;

    public bool GpuLoadUnavailable => ShowBuiltInGpuOptions && _adaptersLoaded && GpuAdapters.Count == 0;

    public string SingleAdapterText => GpuAdapters.Count == 1 ? GpuAdapters[0].DisplayName : string.Empty;

    public string StressTargetHelp => (SelectedStressTarget, UseOwnGame) switch
    {
        (StressTarget.Cpu, _) => "Burns every CPU core with a checked calculation; any wrong answer counts as an error.",
        (StressTarget.Gpu, false) => "Built-in GPU load: PC Checker runs a DirectX 11 compute workload on your graphics card (NVIDIA, AMD or Intel) and checks the results, so an unstable card shows up as errors. No game needed.",
        (StressTarget.Gpu, true) => "Guided test: start your own game or benchmark and PC Checker will watch temperatures and clocks while it runs.",
        (_, false) => "CPU burn and the built-in GPU load together: the hottest test, closest to a demanding game that also loads the processor.",
        _ => "CPU burn plus GPU monitoring: start your game or benchmark for the GPU half.",
    };

    public string IntensityNote => SelectedIntensity switch
    {
        GpuLoadIntensity.Light => "Light: a small workload about half the time. Warm, not hot; good for a first check of a new setting.",
        GpuLoadIntensity.Medium => "Medium: near-constant load, similar to a demanding game. A sensible default for checking an overclock.",
        _ => "Heavy: the card is busy all the time with a large buffer in video memory. Hottest and loudest; use it to confirm stability.",
    };

    public string StressDurationText => StressMinutes < 1 ? $"{StressMinutes * 60:0} seconds" : $"{StressMinutes:0.#} minutes";

    private IStressTestRunner? StressRunner => _overclock as IStressTestRunner;

    partial void OnStressMinutesChanged(double value) => OnPropertyChanged(nameof(StressDurationText));

    partial void OnStressResultChanged(StressTestResult? value) => OnPropertyChanged(nameof(HasStressResult));

    /// <summary>Loads the graphics adapters for the GPU load and checks the power source (page initialisation).</summary>
    private async Task LoadStressOptionsAsync(CancellationToken cancellationToken)
    {
        if (StressRunner is not { } runner)
        {
            return;
        }

        bool cancelled = false;
        try
        {
            var adapters = await runner.ListGpuAdaptersAsync(cancellationToken);
            int? keep = SelectedGpuAdapter?.Index;
            GpuAdapters.Clear();
            foreach (var a in adapters)
            {
                GpuAdapters.Add(a);
            }

            SelectedGpuAdapter = GpuAdapters.FirstOrDefault(a => a.Index == keep) ?? GpuAdapters.FirstOrDefault(a => a.IsRecommended) ?? GpuAdapters.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            // A cancelled listing says nothing about the GPU load, so it must not show "unavailable".
            cancelled = true;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graphics adapters for the GPU stress test could not be listed.");
        }
        finally
        {
            _adaptersLoaded |= !cancelled;
            OnPropertyChanged(nameof(ShowAdapterPicker));
            OnPropertyChanged(nameof(ShowSingleAdapter));
            OnPropertyChanged(nameof(SingleAdapterText));
            OnPropertyChanged(nameof(GpuLoadUnavailable));
        }

        ApplyPreflight(runner.CheckPreflight());
    }

    private void ApplyPreflight(StressPreflight preflight)
    {
        StressPowerMessage = preflight.Message;
        StressPowerSeverity = preflight.CanRun ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
    }

    [RelayCommand(CanExecute = nameof(StressIdle))]
    private async Task StartStress()
    {
        var runner = StressRunner;
        var preflight = runner?.CheckPreflight() ?? StressPreflight.Ok;
        ApplyPreflight(preflight);
        if (!preflight.CanRun)
        {
            _notifications.Warning("Plug in first", preflight.Message ?? "The stress test cannot start on a low battery.");
            return;
        }

        int seconds = (int)Math.Round(Math.Clamp(StressMinutes, 0.5, 30) * 60);
        var request = new StressTestRequest(SelectedStressTarget, seconds, AbortCpuTemp, AbortGpuTemp)
        {
            GpuMode = UseOwnGame ? GpuStressMode.Guided : GpuStressMode.BuiltIn,
            GpuAdapterIndex = SelectedGpuAdapter?.Index ?? -1,
            Intensity = SelectedIntensity,
            VerifyGpuResults = VerifyGpuResults,
        };

        if (!await ConfirmStressAsync(request, preflight))
        {
            return;
        }

        _stressCts?.Dispose();
        _stressCts = new CancellationTokenSource();
        StressRunning = true;
        StressResult = null;
        StressSummary = null;
        StressReasons.Clear();
        OnPropertyChanged(nameof(HasStressReasons));
        StressProgress = 0;
        StressDetail = "Starting…";
        ResetLiveReadouts(request, seconds);

        var progress = new Progress<ProgressUpdate>(u =>
        {
            if (u.Percent is { } p)
            {
                StressProgress = p;
            }

            StressDetail = u.Detail ?? u.Stage;
        });
        var live = new Progress<StressLiveStatus>(s => UpdateLiveReadouts(request, s, seconds));

        try
        {
            if (runner is not null)
            {
                var outcome = await runner.RunStressTestAsync(request, live, progress, _stressCts.Token);
                ShowStressOutcome(outcome);
            }
            else
            {
                var result = await _overclock.RunStressTestAsync(SelectedStressTarget, seconds, AbortCpuTemp, AbortGpuTemp, progress, _stressCts.Token);
                ShowStressResult(result, null, result.AbortedByWatchdog && (await CurrentlyAppliedAsync()).IsStock);
            }
        }
        catch (OperationCanceledException)
        {
            StressDetail = "Stopped.";
            StressSummary = "The test was stopped before it finished, so there is no verdict.";
            StressTitle = "Stopped";
            StressSeverity = InfoBarSeverity.Informational;
            StressResult = new StressTestResult(SelectedStressTarget, 0, null, null, false, 0, false, StressSummary, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stress test failed.");
            ErrorMessage = "The stress test could not run: " + ex.Message;
        }
        finally
        {
            StressRunning = false;
            StressProgress = 100;
            _stressCts?.Dispose();
            _stressCts = null;
            UpdateAdvancedInfo(Capabilities);
        }
    }

    [RelayCommand(CanExecute = nameof(StressRunning))]
    private void StopStress()
    {
        try
        {
            _stressCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Explains what is about to happen; asked when an overclock is applied or the built-in GPU load will run.</summary>
    private async Task<bool> ConfirmStressAsync(StressTestRequest request, StressPreflight preflight)
    {
        var lines = new List<string>();
        if (request.LoadsGpu)
        {
            string card = SelectedGpuAdapter?.Name ?? "your graphics card";
            lines.Add($"{card} will run at {SelectedIntensity.ToString().ToLowerInvariant()} load for {StressDurationText}. Fans will get loud and the screen may stutter while it runs.");
            lines.Add("If the graphics driver resets, the screen goes black for a few seconds while Windows recovers it; the test then stops and reports a failure.");
        }

        if (IsProfileApplied)
        {
            lines.Add($"\"{ActiveProfileName}\" is applied. If the CPU reaches {AbortCpuTemp:0} °C or the GPU reaches {AbortGpuTemp:0} °C{(request.LoadsGpu ? ", or the driver resets," : string.Empty)} the test stops and the overclock is reset to defaults automatically.");
        }

        if (preflight.OnBattery && preflight.HasMessage)
        {
            lines.Add(preflight.Message!);
        }

        if (lines.Count == 0)
        {
            return true;
        }

        lines.Add("You can stop at any time.");
        return await _dialogs.ConfirmAsync("Start the stress test?", string.Join("\n\n", lines), "Start");
    }

    private void ResetLiveReadouts(StressTestRequest request, int seconds)
    {
        ShowLiveReadouts = true;
        LiveShowsGpuLoad = request.LoadsGpu;
        LiveShowsCpu = request.LoadsCpu || !request.LoadsGpu;
        LiveElapsedText = "0:00";
        LiveRemainingText = $"of {FormatDuration(TimeSpan.FromSeconds(seconds))}";
        LiveDispatchRateText = "—";
        LiveAdapterText = request.LoadsGpu ? SelectedGpuAdapter?.Name ?? "Starting…" : string.Empty;
        LiveErrorsText = "0";
        LiveErrorsDetail = request.LoadsGpu && request.LoadsCpu ? "GPU 0 · CPU 0" : request.LoadsGpu ? "Wrong GPU values" : "Wrong CPU results";
        LiveGpuText = "—";
        LiveGpuDetail = request.WatchesGpu ? "Waiting for sensors…" : "Not under test";
        LiveCpuText = "—";
        LiveCpuDetail = request.LoadsCpu ? "Waiting for sensors…" : "Not under test";
    }

    private void UpdateLiveReadouts(StressTestRequest request, StressLiveStatus s, int seconds)
    {
        var culture = CultureInfo.CurrentCulture;
        LiveElapsedText = FormatDuration(s.Elapsed);
        LiveRemainingText = $"of {FormatDuration(TimeSpan.FromSeconds(seconds))}";
        if (request.LoadsGpu)
        {
            LiveDispatchRateText = s.DispatchesPerSecond > 0 ? s.DispatchesPerSecond.ToString("0.0", culture) : "—";
            LiveAdapterText = s.GpuAdapterName ?? LiveAdapterText;
        }

        long errors = s.GpuErrors + s.CpuErrors;
        LiveErrorsText = errors.ToString("N0", culture);
        LiveErrorsDetail = request.LoadsGpu && request.LoadsCpu
            ? $"GPU {s.GpuErrors.ToString("N0", culture)} · CPU {s.CpuErrors.ToString("N0", culture)}"
            : request.LoadsCpu ? $"in {s.CpuChecks.ToString("N0", culture)} CPU checks" : "Wrong GPU values";

        LiveGpuText = s.GpuTempC is { } g ? g.ToString("0", culture) : "—";
        LiveGpuDetail = string.Join(" · ", new[]
        {
            s.GpuClockMhz is { } gc ? $"{gc.ToString("N0", culture)} MHz" : null,
            s.GpuLoadPercent is { } gl ? $"{gl:0} % load" : null,
        }.Where(t => t is not null));
        LiveCpuText = s.CpuTempC is { } c ? c.ToString("0", culture) : "—";
        LiveCpuDetail = s.CpuClockMhz is { } cc ? $"{cc.ToString("N0", culture)} MHz" : string.Empty;
    }

    private void ShowStressOutcome(StressTestOutcome outcome)
    {
        ShowStressResult(outcome.Result, outcome.Verdict, outcome.OverclockReverted);
        StressReasons.Clear();
        foreach (var reason in outcome.Verdict.Reasons)
        {
            StressReasons.Add(reason);
        }

        OnPropertyChanged(nameof(HasStressReasons));
        if (outcome.Gpu is { DriverReset: true })
        {
            _notifications.Warning("Graphics driver reset", outcome.OverclockReverted
                ? "The GPU is not stable at these settings; the overclock was reset to defaults."
                : "The GPU is not stable at these settings.");
        }

        if (outcome.OverclockReverted && IsProfileApplied)
        {
            ActiveProfileName = null;
            LoadIntoSliders(OverclockProfile.Stock());
        }
    }

    private void ShowStressResult(StressTestResult result, StressVerdict? verdict, bool overclockReverted)
    {
        StressResult = result;
        StressSummary = result.Summary;
        LastStressSummary = $"{result.Target} test, {result.DurationSeconds} s: {result.Summary}";
        if (result.AbortedByWatchdog)
        {
            StressTitle = verdict?.Title ?? "Stopped by the watchdog";
            StressSeverity = InfoBarSeverity.Error;
            if (IsProfileApplied && !overclockReverted)
            {
                // The revert failed, so the overclock is still live: say so instead of showing stock values.
                ErrorMessage = "A temperature limit was reached, but the overclock could not be reverted. Press Reset to defaults.";
                _notifications.Error("Stress test aborted", ErrorMessage);
            }
            else
            {
                _notifications.Warning("Stress test aborted", IsProfileApplied
                    ? "A temperature limit was reached; the overclock was reverted."
                    : "A temperature limit was reached and the test stopped.");
                if (IsProfileApplied)
                {
                    ActiveProfileName = null;
                    LoadIntoSliders(OverclockProfile.Stock());
                }
            }
        }
        else if (result.Passed)
        {
            StressTitle = verdict?.Title ?? "Passed";
            StressSeverity = InfoBarSeverity.Success;
            _notifications.Success("Stress test passed", "No errors and temperatures stayed under the limits.");
        }
        else
        {
            StressTitle = verdict?.Title ?? (result.Errors > 0 ? "Failed: calculation errors" : "Completed with warnings");
            StressSeverity = verdict?.Title is "Completed with warnings" or "Not started" || (verdict is null && result.Errors == 0)
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Error;
        }

        StressDetail = result.DurationSeconds == 0 && !result.Passed
            ? "Not started."
            : $"Finished in {FormatDuration(TimeSpan.FromSeconds(result.DurationSeconds))}"
              + (result.MaxCpuTempC is { } c ? $" · CPU max {c:0} °C" : string.Empty)
              + (result.MaxGpuTempC is { } g ? $" · GPU max {g:0} °C" : string.Empty)
              + $" · {result.Errors} errors";
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
