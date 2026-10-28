using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Converters;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Displays;
using PcChecker.Core.GameReady;
using PcChecker.Core.Input;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The Game ready page: one check of everything that commonly costs gamers performance (refresh rate, memory profile,
/// graphics card link, Windows gaming settings, power, background apps, mouse, drives, updates), biggest wins first,
/// each with a one-click fix through the service that owns it. Split across partial files: this one runs the check and
/// shows it, the other applies fixes.
/// </summary>
public sealed partial class GameReadyViewModel : ViewModelBase
{
    private readonly IGameReadyService _service;
    private readonly IDisplayService _displays;
    private readonly OptimizationActionRunner _runner;
    private readonly IChangeBatchRunner _batch;
    private readonly IMousePollingMeter _meter;
    private readonly IDialogService _dialogs;
    private readonly IContentDialogService _contentDialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<GameReadyViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyCanExecuteChangedFor(nameof(FixAllCommand))]
    private bool _hasReport;

    [ObservableProperty]
    private int _score;

    [ObservableProperty]
    private string _grade = string.Empty;

    [ObservableProperty]
    private Brush _scoreBrush = HealthStatusToBrushConverter.Unknown;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _checkedText = string.Empty;

    [ObservableProperty]
    private string _problemsText = "0";

    [ObservableProperty]
    private string _suggestionsText = "0";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FixAllCommand))]
    private int _safeFixCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warnings;

    [ObservableProperty]
    private bool _restartNeeded;

    public GameReadyViewModel(ISettingsService settings, IGameReadyService service, IDisplayService displays, OptimizationActionRunner runner,
        IChangeBatchRunner batch, IMousePollingMeter meter, IDialogService dialogs, IContentDialogService contentDialogs,
        INotificationService notifications, INavigationService navigation, ILogger<GameReadyViewModel> logger)
        : base(settings)
    {
        _service = service;
        _displays = displays;
        _runner = runner;
        _batch = batch;
        _meter = meter;
        _dialogs = dialogs;
        _contentDialogs = contentDialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
        _runner.RebootRequiredChanged += (_, _) => DispatcherHelper.PostToUi(() => RestartNeeded = _runner.IsRebootRequired);
    }

    /// <summary>Problems and suggestions, biggest wins first.</summary>
    public ObservableCollection<GameReadyRow> ToDo { get; } = [];

    /// <summary>Items that could not be checked and background information.</summary>
    public ObservableCollection<GameReadyRow> Notes { get; } = [];

    /// <summary>Everything that is already fine.</summary>
    public ObservableCollection<GameReadyRow> AllGood { get; } = [];

    public bool ShowEmptyState => !HasReport;

    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);

    public bool HasToDo => ToDo.Count > 0;

    public bool HasNoToDo => HasReport && ToDo.Count == 0;

    public bool HasNotes => Notes.Count > 0;

    public bool HasAllGood => AllGood.Count > 0;

    public string AllGoodHeader => Loc.F("Already good ({0})", AllGood.Count);

    /// <summary>
    /// Shows the last check of this session when there is one. Otherwise normal mode waits for the button, and demo mode
    /// runs the check so the page has content.
    /// </summary>
    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_service.LastFacts is { } facts)
        {
            Apply(GameReadyRules.Evaluate(facts, Loc.Current), keepFixes: false);
            return Task.CompletedTask;
        }

        return AppOptions.Current.IsDemo ? CheckNowAsync(cancellationToken) : Task.CompletedTask;
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => CheckNowAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Game ready page operation failed.");

    [RelayCommand]
    private Task CheckAsync() => CheckNowAsync(CancellationToken.None);

    private Task CheckNowAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Percent is { } pct ? Loc.F("Checking… {0:0} %", pct) : Loc.T("Checking…"));
        var facts = await _service.CheckAsync(progress, linked.Token);
        var report = GameReadyRules.Evaluate(facts, Loc.Current);
        Apply(report, keepFixes: false);
        _notifications.Success(Loc.T("Game ready check finished"), report.ProblemCount + report.SuggestionCount == 0
            ? Loc.T("Nothing to improve: this PC is ready for games.")
            : Loc.F("{0} things to improve, {1} can be fixed safely in one go.", report.ProblemCount + report.SuggestionCount, report.SafeFixCount));
    }, Loc.T("Checking monitors, settings, hardware and running apps…"));

    /// <summary>Shows a report. With <paramref name="keepFixes"/>, rows fixed earlier stay marked as fixed (with their undo).</summary>
    private void Apply(GameReadyReport report, bool keepFixes)
    {
        var previous = new Dictionary<string, GameReadyRow>(StringComparer.Ordinal);
        if (keepFixes)
        {
            foreach (var row in ToDo.Concat(Notes).Concat(AllGood).Where(r => r.IsFixed || r.HasResult))
            {
                previous.TryAdd(row.Id, row);
            }
        }

        ToDo.Clear();
        Notes.Clear();
        AllGood.Clear();
        foreach (var item in report.Items)
        {
            var row = new GameReadyRow(item);
            if (previous.TryGetValue(item.Id, out var old))
            {
                row.IsFixed = old.IsFixed;
                row.Undo = old.Undo;
                row.ResultText = old.ResultText;
                row.ResultFailed = old.ResultFailed;
            }

            (item.NeedsAttention ? ToDo : item.Status == GameReadyStatus.Good ? AllGood : Notes).Add(row);
        }

        Warnings = report.Warnings.Count == 0 ? null : string.Join("\n", report.Warnings);
        CheckedText = Loc.F("Checked {0:t}", report.Checked.LocalDateTime);
        HasReport = true;
        UpdateSummary();
        OnPropertyChanged(nameof(HasToDo));
        OnPropertyChanged(nameof(HasNoToDo));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasAllGood));
        OnPropertyChanged(nameof(AllGoodHeader));
    }

    /// <summary>Score and counts from the rows as they stand now: fixed rows no longer count.</summary>
    private void UpdateSummary()
    {
        var open = ToDo.Where(r => !(r.IsFixed && r.Fix?.ChangesSystem == true)).Select(r => r.Item).ToList();
        Score = GameReadyRules.Score(open);
        Grade = GameReadyRules.Grade(Score, Loc.Current);
        ScoreBrush = Score >= 75 ? HealthStatusToBrushConverter.Good : Score >= 50 ? HealthStatusToBrushConverter.Warning : HealthStatusToBrushConverter.Critical;
        int problems = open.Count(i => i.Status == GameReadyStatus.Problem);
        int suggestions = open.Count(i => i.Status == GameReadyStatus.Suggestion);
        ProblemsText = problems.ToString(System.Globalization.CultureInfo.CurrentCulture);
        SuggestionsText = suggestions.ToString(System.Globalization.CultureInfo.CurrentCulture);
        SafeFixCount = ToDo.Count(r => r.IsSafeFix && !r.IsFixed);
        SummaryText = problems + suggestions == 0
            ? Loc.T("Nothing to improve. Monitors, memory, graphics card and Windows settings are ready for games.")
            : Loc.F("{0} things cost you performance. The biggest wins are at the top; safe ones can be fixed in one go.", problems + suggestions);
    }

    [RelayCommand]
    private void OpenPage(string? key)
    {
        var page = PageHelpLocator.PageTypeOf(key)
            ?? (string.Equals(key, GameReadyRules.RamTuningPage, StringComparison.OrdinalIgnoreCase) ? PageHelpLocator.PageTypeOf("memory") : null);
        if (page is not null)
        {
            _navigation.Navigate(page);
        }
    }

    [RelayCommand]
    private void DismissRestart() => RestartNeeded = false;

    private void OpenLink(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var result = ShellLauncher.Open(target);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Could not open"), result.Message);
        }
    }
}
