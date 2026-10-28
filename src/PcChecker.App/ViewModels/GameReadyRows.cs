using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Converters;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Accessibility;
using PcChecker.Core.Common;
using PcChecker.Core.GameReady;

namespace PcChecker.App.ViewModels;

/// <summary>One item of the Game ready list: what was found, why it matters, the fix and, after a fix, its undo.</summary>
public sealed partial class GameReadyRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFixButton))]
    [NotifyPropertyChangedFor(nameof(ShowUndoButton))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private bool _isFixed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _resultText;

    [ObservableProperty]
    private bool _resultFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUndoButton))]
    private Func<Task<bool>>? _undo;

    public GameReadyRow(GameReadyItem item)
    {
        Item = item;
    }

    public GameReadyItem Item { get; }

    public string Id => Item.Id;

    public string Title => Item.Title;

    public string Why => Item.Why;

    public string Detail => Item.Detail;

    public GameReadyFix? Fix => Item.Fix;

    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);

    /// <summary>The fix button shows until the fix has been applied (page links and tests always show).</summary>
    public bool ShowFixButton => Fix is not null && (!IsFixed || !Fix.ChangesSystem);

    public bool ShowUndoButton => IsFixed && Undo is not null;

    public string FixLabel => Fix?.Label ?? string.Empty;

    /// <summary>A change that may be included in "Fix all safe items".</summary>
    public bool IsSafeFix => Item.IsSafeFix;

    public bool RequiresRestart => Fix?.RequiresRestart == true;

    public string FixTooltip => Fix switch
    {
        null => string.Empty,
        { ChangesSystem: true, RequiresRestart: true } => Loc.T("Asks first, can be undone, and needs a restart to take effect."),
        { ChangesSystem: true } => Loc.T("Asks first and can be undone."),
        { Kind: GameReadyFixKind.MousePollingTest } => Loc.T("Measures how often your mouse reports its position. Nothing is changed."),
        _ => Loc.T("Opens the place where you can change this. Nothing is changed by the button itself."),
    };

    public string StatusText => IsFixed && Fix?.ChangesSystem == true
        ? Loc.T("Fixed")
        : Item.Status switch
        {
            GameReadyStatus.Problem => Loc.T("Fix this"),
            GameReadyStatus.Suggestion => Loc.T("Worth doing"),
            GameReadyStatus.NotChecked => Loc.T("Not checked"),
            GameReadyStatus.Info => Loc.T("Good to know"),
            _ => Loc.T("OK"),
        };

    public Brush StatusBrush => IsFixed && Fix?.ChangesSystem == true
        ? HealthStatusToBrushConverter.Good
        : Item.Status switch
        {
            GameReadyStatus.Problem => HealthStatusToBrushConverter.Critical,
            GameReadyStatus.Suggestion => HealthStatusToBrushConverter.Warning,
            GameReadyStatus.Info => ThemeColors.Accent(AccentRole.Blue),
            GameReadyStatus.Good => HealthStatusToBrushConverter.Good,
            _ => HealthStatusToBrushConverter.Unknown,
        };

    public bool ShowImpact => Item.NeedsAttention && Item.Impact > GameReadyImpact.None;

    public string ImpactText => Item.Impact switch
    {
        GameReadyImpact.High => Loc.T("Big win"),
        GameReadyImpact.Medium => Loc.T("Medium win"),
        _ => Loc.T("Small win"),
    };

    public string ImpactTooltip => Loc.T("How much this is likely to help in games.");

    public string AreaText => Item.Area switch
    {
        GameReadyArea.Display => Loc.T("Monitor"),
        GameReadyArea.Memory => Loc.T("Memory"),
        GameReadyArea.GraphicsCard => Loc.T("Graphics card"),
        GameReadyArea.Power => Loc.T("Power"),
        GameReadyArea.Windows => Loc.T("Windows"),
        GameReadyArea.BackgroundApps => Loc.T("Background apps"),
        GameReadyArea.Mouse => Loc.T("Mouse"),
        GameReadyArea.Storage => Loc.T("Storage"),
        GameReadyArea.Updates => Loc.T("Updates"),
        _ => Loc.T("Security"),
    };

    /// <summary>One line for screen readers: status, title and area.</summary>
    public string AccessibleName => $"{StatusText}: {Title} ({AreaText})";
}
