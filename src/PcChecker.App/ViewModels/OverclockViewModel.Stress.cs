using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Overclocking;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>Stress test card: target, duration, abort temperatures, live progress and the verdict.</summary>
public sealed partial class OverclockViewModel
{
    private CancellationTokenSource? _stressCts;

    [ObservableProperty] private StressTarget _selectedStressTarget = StressTarget.Cpu;
    [ObservableProperty] private double _stressMinutes = 2;
    [ObservableProperty] private double _abortCpuTemp = 90;
    [ObservableProperty] private double _abortGpuTemp = 87;

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

    public IReadOnlyList<StressTarget> StressTargets { get; } = Enum.GetValues<StressTarget>();

    public bool StressIdle => !StressRunning;

    public bool HasStressResult => StressResult is not null;

    public string StressTargetHelp => SelectedStressTarget switch
    {
        StressTarget.Cpu => "Burns every CPU core with a checked calculation; any wrong answer counts as an error.",
        StressTarget.Gpu => "Guided test: PC Checker cannot create GPU load itself, so start your game or benchmark and it will watch temperatures and clocks.",
        _ => "CPU burn plus GPU monitoring: start your game or benchmark for the GPU half.",
    };

    public string StressDurationText => StressMinutes < 1 ? $"{StressMinutes * 60:0} seconds" : $"{StressMinutes:0.#} minutes";

    partial void OnSelectedStressTargetChanged(StressTarget value) => OnPropertyChanged(nameof(StressTargetHelp));

    partial void OnStressMinutesChanged(double value) => OnPropertyChanged(nameof(StressDurationText));

    partial void OnStressResultChanged(StressTestResult? value) => OnPropertyChanged(nameof(HasStressResult));

    [RelayCommand(CanExecute = nameof(StressIdle))]
    private async Task StartStress()
    {
        if (!ConsentAccepted)
        {
            return;
        }

        var s = Settings.Current.Overclock;
        int seconds = (int)Math.Round(Math.Clamp(StressMinutes, 0.5, 30) * 60);
        if (IsProfileApplied)
        {
            var ok = await _dialogs.ConfirmAsync("Start the stress test?",
                $"\"{ActiveProfileName}\" is applied. If the CPU reaches {AbortCpuTemp:0} °C or the GPU reaches {AbortGpuTemp:0} °C the test stops and the overclock is reset to defaults automatically.",
                "Start");
            if (!ok)
            {
                return;
            }
        }

        _stressCts?.Dispose();
        _stressCts = new CancellationTokenSource();
        StressRunning = true;
        StressResult = null;
        StressSummary = null;
        StressProgress = 0;
        StressDetail = "Starting…";

        var progress = new Progress<ProgressUpdate>(u =>
        {
            if (u.Percent is { } p)
            {
                StressProgress = p;
            }

            StressDetail = u.Detail ?? u.Stage;
        });

        try
        {
            var result = await _overclock.RunStressTestAsync(SelectedStressTarget, seconds, AbortCpuTemp, AbortGpuTemp, progress, _stressCts.Token);
            ShowStressResult(result);
            if (result.AbortedByWatchdog && IsProfileApplied)
            {
                ActiveProfileName = null;
                LoadIntoSliders(OverclockProfile.Stock());
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

    private void ShowStressResult(StressTestResult result)
    {
        StressResult = result;
        StressSummary = result.Summary;
        LastStressSummary = $"{result.Target} test, {result.DurationSeconds} s: {result.Summary}";
        if (result.AbortedByWatchdog)
        {
            StressTitle = "Stopped by the watchdog";
            StressSeverity = InfoBarSeverity.Error;
            _notifications.Warning("Stress test aborted", "A temperature limit was reached; the overclock was reverted.");
        }
        else if (result.Passed)
        {
            StressTitle = "Passed";
            StressSeverity = InfoBarSeverity.Success;
            _notifications.Success("Stress test passed", "No errors and temperatures stayed under the limits.");
        }
        else
        {
            StressTitle = result.Errors > 0 ? "Failed: calculation errors" : "Completed with warnings";
            StressSeverity = result.Errors > 0 ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        }

        StressDetail = $"Finished in {result.DurationSeconds} s" + (result.MaxCpuTempC is { } c ? $" · CPU max {c:0} °C" : string.Empty) + (result.MaxGpuTempC is { } g ? $" · GPU max {g:0} °C" : string.Empty) + $" · {result.Errors} errors";
    }
}
