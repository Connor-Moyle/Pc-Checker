using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Overclocking;
using PcChecker.App.Services;
using PcChecker.Core.Accessibility;

namespace PcChecker.App.ViewModels;

/// <summary>One row of the "Supported hardware" table with a colour for the support level.</summary>
public sealed class SupportMatrixRow
{
    public SupportMatrixRow(string feature, string vendor, string level, string note)
    {
        Feature = feature;
        Vendor = vendor;
        Level = level;
        Note = note;
        Brush = BrushFor(level);
    }

    public string Feature { get; }
    public string Vendor { get; }
    public string Level { get; }
    public string Note { get; }
    public Brush Brush { get; }

    public static Brush BrushFor(string level) => ThemeColors.Accent(level switch
    {
        SupportLevel.Full => AccentRole.Green,
        SupportLevel.Partial => AccentRole.Cyan,
        SupportLevel.Experimental => AccentRole.Purple,
        SupportLevel.MonitorOnly => AccentRole.Yellow,
        _ => AccentRole.Grey,
    });
}

/// <summary>One step of an assistant plan with its target profile and a "done" marker.</summary>
public sealed partial class PlanStepItem : ObservableObject
{
    [ObservableProperty]
    private bool _isApplied;

    [ObservableProperty]
    private string? _resultText;

    public PlanStepItem(OverclockStep step)
    {
        Step = step;
    }

    public OverclockStep Step { get; }
    public int Order => Step.Order;
    public string Title => $"Step {Step.Order}: {Step.Title}";
    public string Rationale => Step.Rationale;
    public string ExpectedGain => string.IsNullOrWhiteSpace(Step.ExpectedGain) ? "Not estimated" : Step.ExpectedGain;
    public string AbortIf => Step.AbortIf;
    public string StressText => Step.StressSeconds >= 60 ? $"{Step.StressSeconds / 60} min stress test" : $"{Step.StressSeconds} s stress test";
    public string Changes => string.Join(" · ", OverclockPresets.Describe(Step.Target));
}
