using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.Checkup;
using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.App.ViewModels;

/// <summary>One entry of the check-up to-do list, with its tick box and the result of the last fix.</summary>
public sealed partial class CheckupItemRow : ObservableObject
{
    private readonly Action _selectionChanged;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckTooltip))]
    [NotifyPropertyChangedFor(nameof(ShowFixButton))]
    private bool _canSelect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _resultText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFixButton))]
    [NotifyPropertyChangedFor(nameof(ShowOpenButton))]
    private bool _isDone;

    [ObservableProperty]
    private bool _resultFailed;

    public CheckupItemRow(CheckupItem item, Type? page, string pageTitle, bool advancedMode, Action selectionChanged)
    {
        Item = item;
        Page = page;
        PageTitle = pageTitle;
        _canSelect = CheckupSelection.CanSelect(item, advancedMode);
        _isSelected = _canSelect && CheckupSelection.IsSelectedByDefault(item);
        _selectionChanged = selectionChanged;
    }

    public CheckupItem Item { get; }

    /// <summary>The page the item comes from; "Open" goes there.</summary>
    public Type? Page { get; }

    public string PageTitle { get; }

    public string Id => Item.Id;

    public string Title => Item.Title;

    public string Why => Item.Why;

    public string Benefit => Item.Benefit;

    public Severity Severity => Item.Severity;

    public RiskLevel Risk => Item.Risk;

    public bool HasFix => Item.HasFix;

    public string RiskText => Item.Risk switch
    {
        RiskLevel.Safe => Loc.T("Safe"),
        RiskLevel.Moderate => Loc.T("Moderate"),
        _ => Loc.T("Advanced"),
    };

    public string RiskTooltip => Item.Risk switch
    {
        RiskLevel.Safe => Loc.T("Safe: a standard change that is easy to undo or harmless."),
        RiskLevel.Moderate => Loc.T("Moderate: changes how something behaves. Read what it does first; it is never ticked for you."),
        _ => Loc.T("Advanced: only for people who know what it does. Needs Advanced mode."),
    };

    /// <summary>"From Cleanup": where the item comes from (the Open button goes there).</summary>
    public string SourceText => Loc.F("From {0}", PageTitle);

    public string OpenText => Loc.F("Open {0}", PageTitle);

    /// <summary>The check box tooltip; the reasons match <see cref="CheckupSelection.WhyNotSelectable"/>, written here so they can be translated.</summary>
    public string CheckTooltip => CanSelect
        ? Loc.T("Include this in Fix selected")
        : !Item.HasFix
            ? Loc.T("This one needs you: open its page to deal with it.")
            : Item.Risk == RiskLevel.Advanced
                ? Loc.T("An advanced change. Turn on Advanced mode to fix it from here, or open its page.")
                : Loc.T("Cannot be fixed from here.");

    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);

    /// <summary>Fixable items show "Fix"; the rest show "Open". A finished item shows neither.</summary>
    public bool ShowFixButton => HasFix && CanSelect && !IsDone;

    public bool ShowOpenButton => !ShowFixButton && !IsDone && Page is not null;

    /// <summary>Records the outcome of a fix; a successful one is unticked and can no longer be selected.</summary>
    public void SetResult(CheckupFixResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ResultText = result.Message;
        ResultFailed = !result.Success;
        if (result.Success)
        {
            IsDone = true;
            IsSelected = false;
            CanSelect = false;
        }
    }

    public void UpdateMode(bool advancedMode)
    {
        if (!IsDone)
        {
            CanSelect = CheckupSelection.CanSelect(Item, advancedMode);
        }
    }

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();

    partial void OnCanSelectChanged(bool value)
    {
        if (!value)
        {
            IsSelected = false;
        }
    }
}
