using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Converters;
using PcChecker.Core.Debloat;
using PcChecker.Core.Optimization;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>One tweak row on the Debloat page: catalog data, the detected state, the tick box and button availability.</summary>
public sealed partial class DebloatTweakRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StateBrush), nameof(CanApply), nameof(CanRevert), nameof(IsNotApplicable), nameof(CanSelect))]
    private TweakStatus _status = TweakStatus.Unknown;

    [ObservableProperty]
    private string _stateDetail = "Checking…";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply), nameof(CanRevert))]
    private bool _isBusy;

    public DebloatTweakRow(DebloatTweak tweak)
    {
        Tweak = tweak;
    }

    public DebloatTweak Tweak { get; }
    public string Id => Tweak.Id;
    public string Title => Tweak.Title;
    public string Description => Tweak.Description;
    public string Category => Tweak.Category;
    public RiskLevel Risk => Tweak.Risk;
    public bool AdvancedOnly => Tweak.AdvancedOnly;
    public bool IsReversible => Tweak.Reversible;
    public bool IsOneWay => !Tweak.Reversible;
    public bool IsAppRemoval => Tweak.Operations.Count > 0 && Tweak.Operations.All(o => o is AppxRemovalOp);
    public bool NeedsRestart => Tweak.RequiresRestart;
    public bool RestartsExplorer => Tweak.RestartsExplorer;
    public bool IsLinked => Tweak.DelegatesToOptimizationId is not null;
    public bool IsWindows11Only => Tweak.Windows11Only;
    public string? Warning => Tweak.Warning;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Tweak.Warning);

    public string RiskText => Tweak.Risk switch
    {
        RiskLevel.Safe => "Safe",
        RiskLevel.Moderate => "Moderate",
        _ => "Advanced",
    };

    public string RiskToolTip => Tweak.Risk switch
    {
        RiskLevel.Safe => "Safe: a normal Windows setting with no side effects for most people.",
        RiskLevel.Moderate => "Moderate: something you might notice stops working. Read the note before applying.",
        _ => "Advanced: changes services, update behaviour or removes software. Only for people who know they don't need it.",
    };

    public string SourcesText => "Also in: " + string.Join(", ", Tweak.SourceTools);

    public string ApplyText => IsAppRemoval ? "Remove" : "Apply";

    public ControlAppearance ApplyAppearance => Tweak.Risk == RiskLevel.Advanced ? ControlAppearance.Danger : ControlAppearance.Primary;

    public bool IsNotApplicable => Status == TweakStatus.NotApplicable;

    public bool CanSelect => Status != TweakStatus.NotApplicable;

    public bool CanApply => !IsBusy && Status is not (TweakStatus.Applied or TweakStatus.NotApplicable);

    public bool CanRevert => !IsBusy && IsReversible && (Status is TweakStatus.Applied or TweakStatus.Partial);

    public string StatusText => Status switch
    {
        TweakStatus.Applied => IsAppRemoval ? "Removed" : "Applied",
        TweakStatus.NotApplied => IsAppRemoval ? "Installed" : "Not applied",
        TweakStatus.Partial => "Partly applied",
        TweakStatus.NotApplicable => "Not available",
        _ => "Unknown",
    };

    public Brush StateBrush => Status switch
    {
        TweakStatus.Applied => HealthStatusToBrushConverter.Good,
        TweakStatus.Partial => HealthStatusToBrushConverter.Warning,
        TweakStatus.NotApplied => SeverityToBrushConverter.Info,
        _ => HealthStatusToBrushConverter.Unknown,
    };

    public string ToolTipText => string.Join(Environment.NewLine, Tweak.Operations.Select(o => "• " + o.Describe()).DefaultIfEmpty(
        "• Uses the Optimize page action \"" + Tweak.DelegatesToOptimizationId + "\""));

    public void Apply(TweakState state)
    {
        Status = state.Status;
        StateDetail = state.Detail ?? StatusText;
        if (!CanSelect)
        {
            IsSelected = false;
        }
    }

    /// <summary>Case-insensitive match on title, description, id and category.</summary>
    public bool Matches(string? search) =>
        string.IsNullOrWhiteSpace(search) ||
        Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        Tweak.SourceTools.Any(s => s.Contains(search, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One category expander (Privacy & telemetry, Ads & suggestions, …).</summary>
public sealed partial class DebloatCategoryGroup : ObservableObject
{
    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _summary = string.Empty;

    public DebloatCategoryGroup(string title, string description, SymbolRegular icon)
    {
        Title = title;
        Description = description;
        Icon = icon;
    }

    public string Title { get; }
    public string Description { get; }
    public SymbolRegular Icon { get; }
    public bool IsApps => Title == DebloatCatalog.AppsCategory;
    public bool IsUpdates => Title == DebloatCatalog.UpdateCategory;
    public ObservableCollection<DebloatTweakRow> Rows { get; } = [];

    public void UpdateSummary()
    {
        var visible = Rows.Where(r => r.IsVisible).ToList();
        int applied = visible.Count(r => r.Status == TweakStatus.Applied);
        int available = visible.Count(r => r.Status != TweakStatus.NotApplicable);
        int selected = visible.Count(r => r.IsSelected);
        Summary = $"{applied} of {available} applied" + (selected > 0 ? $" · {selected} selected" : string.Empty);
    }
}

/// <summary>A preset card at the top of the page.</summary>
public sealed class DebloatPresetCard
{
    public DebloatPresetCard(DebloatPreset preset, int count)
    {
        Preset = preset;
        CountText = preset.IsCustom ? "Pick your own" : $"{count} tweak{(count == 1 ? string.Empty : "s")}";
    }

    public DebloatPreset Preset { get; }
    public string Name => Preset.Name;
    public string Description => Preset.Description;
    public bool IsRecommended => Preset.IsRecommended;
    public string CountText { get; }
    public string ButtonText => Preset.IsCustom ? "Clear ticks" : "Select";
    public ControlAppearance ButtonAppearance => IsRecommended ? ControlAppearance.Primary : ControlAppearance.Secondary;
}

/// <summary>One line of the results list after Apply or Revert.</summary>
public sealed class DebloatResultRow
{
    public DebloatResultRow(TweakRunResult result)
    {
        Result = result;
    }

    public TweakRunResult Result { get; }
    public string Title => Result.Title;
    public string Message => Result.Message;
    public string OutcomeText => Result.Outcome.ToString();

    public Brush OutcomeBrush => Result.Outcome switch
    {
        TweakOutcome.Applied or TweakOutcome.Reverted => HealthStatusToBrushConverter.Good,
        TweakOutcome.Failed => HealthStatusToBrushConverter.Critical,
        _ => HealthStatusToBrushConverter.Unknown,
    };
}
