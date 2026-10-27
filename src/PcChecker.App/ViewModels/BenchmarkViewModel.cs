using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Benchmark;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Benchmark page: runs the processor, memory, drive and graphics tests one after another (or one on its own), scores
/// them against a mid-range 2024 desktop (1000 per test), warns when the PC is on battery, hot or busy, keeps every
/// run in a local history and compares any two runs. Nothing here changes the system; the drive test's temporary file
/// is deleted afterwards.
/// </summary>
public sealed partial class BenchmarkViewModel : ViewModelBase
{
    private const string DefaultSubtitle = "Score the processor, memory, drive and graphics card against a typical 2024 desktop.";

    private readonly IBenchmarkService _benchmark;
    private readonly IBenchmarkHistoryStore _history;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<BenchmarkViewModel> _logger;

    [ObservableProperty] private string _subtitle = DefaultSubtitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunWarnings))]
    private string? _runWarnings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStoppedEarly))]
    private string? _stoppedEarlyMessage;

    [ObservableProperty] private string _overallText = "—";
    [ObservableProperty] private string _overallSubtitle = string.Empty;
    [ObservableProperty] private string _changeText = "—";
    [ObservableProperty] private string _changeSubtitle = string.Empty;
    [ObservableProperty] private string _testsText = "—";
    [ObservableProperty] private string _testsSubtitle = string.Empty;
    [ObservableProperty] private string _runDateText = "—";
    [ObservableProperty] private string _runDateSubtitle = string.Empty;

    [ObservableProperty] private double _secondsPerTest = BenchmarkOptions.DefaultSeconds;
    [ObservableProperty] private string? _runNote;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))]
    private int _selectedTestCount = BenchmarkCatalog.All.Count;

    public BenchmarkViewModel(ISettingsService settings, IBenchmarkService benchmark, IBenchmarkHistoryStore history, IDialogService dialogs,
        INotificationService notifications, ILogger<BenchmarkViewModel> logger)
        : base(settings)
    {
        _benchmark = benchmark;
        _history = history;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        foreach (var kind in BenchmarkCatalog.All)
        {
            Cards.Add(new BenchmarkTestCard(kind));
            TestChoices.Add(new BenchmarkTestChoice(kind, () => SelectedTestCount = TestChoices.Count(c => c.IsSelected)));
        }

        // A setup import (or its undo) changed the history behind the page's back.
        _history.Changed += (_, runs) => DispatcherHelper.PostToUi(() =>
        {
            if (IsInitialized && !IsBusy)
            {
                ApplyHistory(runs, ShownRun?.Run.Id);
            }
        });
        Runs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(HasNoHistory));
            OnPropertyChanged(nameof(CanCompare));
        };
    }

    /// <summary>One results card per test, in run order.</summary>
    public ObservableCollection<BenchmarkTestCard> Cards { get; } = [];

    /// <summary>The Options checkboxes that choose what "Run benchmark" includes.</summary>
    public ObservableCollection<BenchmarkTestChoice> TestChoices { get; } = [];

    public bool HasRunWarnings => !string.IsNullOrWhiteSpace(RunWarnings);

    public bool HasStoppedEarly => !string.IsNullOrWhiteSpace(StoppedEarlyMessage);

    public string HistoryLocationText => Loc.F("History: {0}", _history.Location);

    /// <summary>Loads the history and shows the newest run. Demo mode has sample runs, so the page is filled at once.</summary>
    public override Task InitializeAsync(CancellationToken cancellationToken) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, cancellationToken);
        var runs = await _history.LoadAsync(linked.Token);
        ApplyHistory(runs, runs.FirstOrDefault()?.Id);
    }, Loc.T("Loading benchmark history\u2026"));

    public override Task RefreshAsync(CancellationToken cancellationToken) => InitializeAsync(cancellationToken);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Benchmark page operation failed.");

    private bool CanRunSelected() => SelectedTestCount > 0;

    [RelayCommand(CanExecute = nameof(CanRunSelected))]
    private Task RunSelectedAsync() =>
        StartAsync(TestChoices.Where(c => c.IsSelected).Select(c => c.Kind).ToList());

    [RelayCommand]
    private Task RunTestAsync(BenchmarkTestCard? card) =>
        card is null ? Task.CompletedTask : StartAsync([card.Kind]);

    /// <summary>Checks the conditions, asks to confirm (listing any warnings), runs the tests and saves the run.</summary>
    private async Task StartAsync(IReadOnlyList<BenchmarkTestKind> tests)
    {
        if (tests.Count == 0 || IsBusy)
        {
            return;
        }

        BenchmarkConditions? conditions = null;
        bool checkedOk = await RunBusyAsync(async ct => conditions = await _benchmark.CheckConditionsAsync(ct),
            Loc.T("Checking that the PC is idle\u2026"));
        if (!checkedOk || conditions is null)
        {
            return;
        }

        bool hasWarnings = !conditions.IsIdeal;
        if (!await _dialogs.ConfirmAsync(tests.Count == 1 ? Loc.F("Run the test \u201C{0}\u201D?", BenchmarkText.Name(tests[0])) : Loc.T("Run the benchmark?"),
                ConfirmationText(tests, conditions), hasWarnings ? Loc.T("Run anyway") : Loc.T("Run benchmark")))
        {
            return;
        }

        var options = new BenchmarkOptions((int)Math.Round(SecondsPerTest));
        string? note = RunNote;
        BenchmarkSuiteOutcome? outcome = null;
        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p =>
                BusyText = p.Stage + (p.Percent is { } pct ? $" · {pct:0} %" : string.Empty) + (p.Detail is null ? string.Empty : "\n" + p.Detail));
            outcome = await BenchmarkSuite.RunAsync(_benchmark, tests, options, note, conditions.Warnings, progress, ct);
            if (outcome.Run is { } run)
            {
                await SaveRunAsync(run);
            }
        }, Loc.T("Starting the benchmark\u2026"));

        if (outcome is null)
        {
            return;
        }

        if (outcome.Run is not { } finished)
        {
            _notifications.Info(Loc.T("Benchmark stopped"), Loc.T("No test finished, so nothing was saved."));
            return;
        }

        RunNote = null;
        if (outcome.Cancelled)
        {
            _notifications.Info(Loc.T("Benchmark stopped"), Loc.T("The tests that finished were saved."));
        }
        else
        {
            _notifications.Success(Loc.T("Benchmark finished"), finished.OverallScore is { } score
                ? Loc.F("Overall score {0} (reference PC = 1,000).", BenchmarkFormatter.Score(score, CultureInfo.CurrentCulture))
                : Loc.T("No test produced a score. The cards say why."));
        }
    }

    private string ConfirmationText(IReadOnlyList<BenchmarkTestKind> tests, BenchmarkConditions conditions)
    {
        int seconds = new BenchmarkOptions((int)Math.Round(SecondsPerTest)).ClampedSeconds;
        int timed = tests.Count(BenchmarkCatalog.IsTimed);
        var text = new StringBuilder();
        text.Append(Loc.F("PC Checker will run {0} and use the PC fully for about {1}. Fans may get loud; that is normal.",
            tests.Count == 1 ? Loc.F("the test \u201C{0}\u201D", BenchmarkText.Name(tests[0])) : Loc.F("{0} tests", tests.Count),
            BenchmarkFormatter.Duration(timed * (seconds + 2) + (tests.Contains(BenchmarkTestKind.Disk) ? 15 : 0))));
        if (tests.Contains(BenchmarkTestKind.Disk))
        {
            text.Append("\n\n").Append(Loc.F("The drive test writes a temporary {0} MB file to {1} and deletes it afterwards.", _benchmark.DiskTestFileMb, _benchmark.SystemDrive ?? Loc.T("the Windows drive")));
        }

        text.Append("\n\n").Append(Loc.T("Close games, browsers and downloads first, and leave the PC alone until it finishes."));
        if (!conditions.IsIdeal)
        {
            text.Append("\n\n").Append(Loc.T("Before you start:"));
            foreach (var warning in conditions.Warnings)
            {
                text.Append("\n• ").Append(warning);
            }
        }

        return text.ToString();
    }
}
