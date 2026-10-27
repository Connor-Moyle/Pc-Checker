using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Assistant;
using PcChecker.Core.Overclocking;

namespace PcChecker.App.ViewModels;

/// <summary>Claude advisor card: asks for a staged plan, applies steps one at a time and feeds results back.</summary>
public sealed partial class OverclockViewModel
{
    [ObservableProperty] private string _advisorGoal = "Quiet and cool, with a small performance gain";
    [ObservableProperty] private bool _advisorConfigured;
    [ObservableProperty] private bool _advisorBusy;
    [ObservableProperty] private OverclockPlan? _plan;
    [ObservableProperty] private string? _planSummary;
    [ObservableProperty] private string? _planDisclaimer;
    [ObservableProperty] private string? _lastStressSummary;
    [ObservableProperty] private string? _advisorError;

    public ObservableCollection<PlanStepItem> PlanSteps { get; } = [];
    public ObservableCollection<string> PlanWarnings { get; } = [];
    public ObservableCollection<string> PlanAssumptions { get; } = [];

    public IReadOnlyList<string> GoalSuggestions { get; } =
    [
        "Quiet and cool, with a small performance gain",
        "Maximum frames per second, temperatures stay under 80 °C",
        "Lower power and heat at the same performance (undervolt)",
        "Safe first overclock for a beginner",
    ];

    public bool HasPlan => Plan is not null;

    public bool HasLastStressSummary => !string.IsNullOrWhiteSpace(LastStressSummary);

    public bool HasAdvisorError => !string.IsNullOrWhiteSpace(AdvisorError);

    partial void OnPlanChanged(OverclockPlan? value) => OnPropertyChanged(nameof(HasPlan));

    partial void OnLastStressSummaryChanged(string? value) => OnPropertyChanged(nameof(HasLastStressSummary));

    partial void OnAdvisorErrorChanged(string? value) => OnPropertyChanged(nameof(HasAdvisorError));

    [RelayCommand]
    private Task GetPlan() => RequestPlanAsync(previousResults: null);

    [RelayCommand]
    private Task SendResultsBack() => RequestPlanAsync(LastStressSummary);

    [RelayCommand]
    private void GoToAssistant() => _navigation.Navigate(typeof(AssistantPage));

    [RelayCommand]
    private void ApplyStep(PlanStepItem? step)
    {
        if (step is null)
        {
            return;
        }

        SelectedProfile = null;
        LoadIntoSliders(step.Step.Target);
        NewProfileName = step.Step.Target.Name;
        StatusMessage = $"Loaded \"{step.Step.Title}\" into the sliders. Review the values, then press Apply in the GPU card.";
        step.ResultText = "Loaded into the sliders";
    }

    [RelayCommand]
    private async Task RunStepStress(PlanStepItem? step)
    {
        if (step is null || StressRunning)
        {
            return;
        }

        StressMinutes = Math.Clamp(step.Step.StressSeconds / 60.0, 0.5, 30);
        SelectedStressTarget = step.Step.Target.CpuAllCoreRatio is not null || step.Step.Target.CpuVoltageOffsetMv is not null ? StressTarget.Both : StressTarget.Gpu;
        // StartStress returns without running when the dialog is cancelled or the battery check refuses; then the
        // result is still the one of an earlier, unrelated test and must not mark this step.
        var before = StressResult;
        await StartStressCommand.ExecuteAsync(null);
        if (StressResult is { } r && !ReferenceEquals(r, before))
        {
            step.IsApplied = r.Passed;
            step.ResultText = r.Passed ? "Stress test passed" : r.AbortedByWatchdog ? "Aborted by the watchdog" : "Stress test failed";
        }
    }

    private async Task RequestPlanAsync(string? previousResults)
    {
        AdvisorConfigured = _assistant.IsConfigured;
        if (!AdvisorConfigured)
        {
            AdvisorError = "Claude is not configured. Add your Anthropic API key on the Ask Claude page first.";
            return;
        }

        var caps = Capabilities;
        if (caps is null)
        {
            AdvisorError = "Run detection first so the advisor knows your hardware.";
            return;
        }

        AdvisorBusy = true;
        AdvisorError = null;
        try
        {
            var json = AssistantContextBuilder.BuildHardwareProfileJson(_hardware.Last, _sensors.Latest, caps);
            var request = new OverclockAdvisorRequest(json, caps, Settings.Current.Overclock, AdvisorGoal, previousResults);
            var result = await _assistant.CreateOverclockPlanAsync(request, CancellationToken.None);
            if (!result.Success || result.Value is null)
            {
                AdvisorError = result.Message;
                return;
            }

            ShowPlan(result.Value);
            _notifications.Success("Plan ready", $"{result.Value.Steps.Count} steps. Apply them one at a time and stress test each.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Advisor request failed.");
            AdvisorError = "The advisor request failed: " + ex.Message;
        }
        finally
        {
            AdvisorBusy = false;
        }
    }

    private void ShowPlan(OverclockPlan plan)
    {
        Plan = plan;
        PlanSummary = plan.Summary;
        PlanDisclaimer = plan.Disclaimer;
        PlanSteps.Clear();
        foreach (var step in plan.Steps.OrderBy(s => s.Order))
        {
            PlanSteps.Add(new PlanStepItem(step));
        }

        PlanWarnings.Clear();
        foreach (var w in plan.Warnings)
        {
            PlanWarnings.Add(w);
        }

        PlanAssumptions.Clear();
        foreach (var a in plan.Assumptions)
        {
            PlanAssumptions.Add(a);
        }
    }
}
