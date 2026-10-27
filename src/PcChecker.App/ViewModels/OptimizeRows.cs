using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Optimization;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>One optimization action row: catalog data plus the detected state and button availability.</summary>
public sealed partial class OptimizationActionRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply), nameof(CanRevert), nameof(StateBadge))]
    private bool? _isApplied;

    [ObservableProperty]
    private string _stateText = "Checking…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply), nameof(CanRevert))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isVisible = true;

    public OptimizationActionRow(OptimizationAction action, bool selectable)
    {
        Action = action;
        IsSelectable = selectable;
        _isSelected = selectable;
    }

    public OptimizationAction Action { get; }
    public string Id => Action.Id;
    public string Title => Action.Title;
    public string Description => Action.Description;
    public string Category => Action.Category;
    public RiskLevel Risk => Action.Risk;
    public string RiskText => Action.Risk switch
    {
        RiskLevel.Safe => "Safe",
        RiskLevel.Moderate => "Moderate",
        _ => "Advanced",
    };

    public bool IsReversible => Action.Reversible;
    public bool RequiresRestart => Action.RequiresRestart;
    public bool AdvancedOnly => Action.AdvancedOnly;
    public bool IsSelectable { get; }

    /// <summary>One-off tools (repairs, drive optimization) have no on/off state.</summary>
    public bool IsOnDemand => !Action.Reversible;
    public string ApplyText => IsOnDemand ? "Run" : "Apply";

    /// <summary>Danger for advanced or non-reversible actions, matching the Danger confirmation they get.</summary>
    public ControlAppearance ApplyAppearance => OptimizationRisk.IsDanger(Action) ? ControlAppearance.Danger : ControlAppearance.Primary;
    public bool CanApply => !IsBusy && IsApplied != true;
    public bool CanRevert => !IsBusy && IsReversible && IsApplied == true;
    public string StateBadge => IsApplied switch
    {
        true => "Applied",
        false => "Not applied",
        _ => IsOnDemand ? "On demand" : "Unknown",
    };

    public string ToolTipText => Description
        + "\n\nRisk: " + RiskText
        + (IsReversible ? " · Reversible" : " · Not reversible")
        + (RequiresRestart ? " · Restart needed" : string.Empty);
}

/// <summary>A group of actions on the Optimize page (Safe tweaks, Gaming, Storage, module groups, Advanced).</summary>
public sealed partial class OptimizationGroup : ObservableObject
{
    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private string _summary = string.Empty;

    public OptimizationGroup(string title, string description, SymbolRegular icon, bool isAdvanced)
    {
        Title = title;
        Description = description;
        Icon = icon;
        IsAdvanced = isAdvanced;
    }

    public string Title { get; }
    public string Description { get; }
    public SymbolRegular Icon { get; }
    public bool IsAdvanced { get; }
    public ObservableCollection<OptimizationActionRow> Actions { get; } = [];

    public void UpdateSummary()
    {
        int visible = Actions.Count(a => a.IsVisible);
        int applied = Actions.Count(a => a.IsVisible && a.IsApplied == true);
        int stateful = Actions.Count(a => a.IsVisible && !a.IsOnDemand);
        Summary = stateful == 0 ? $"{visible} tool{(visible == 1 ? string.Empty : "s")}" : $"{applied} of {stateful} applied";
    }
}
