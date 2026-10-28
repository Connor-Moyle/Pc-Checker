using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.RamTuning;

namespace PcChecker.App.ViewModels;

public sealed partial class RamTuningViewModel
{
    private CancellationTokenSource? _quickCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCheck))]
    private string? _checkSummary;

    [ObservableProperty] private Severity _checkSeverity = Severity.Info;
    [ObservableProperty] private string _checkBadge = string.Empty;
    [ObservableProperty] private string? _checkTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistory))]
    private int _historyCount;

    [ObservableProperty] private int _quickPercent = QuickMemoryCheck.DefaultPercent;
    [ObservableProperty] private int _quickDurationIndex = 1;
    [ObservableProperty] private string _quickPlanText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartQuickCheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopQuickCheckCommand))]
    [NotifyPropertyChangedFor(nameof(IsQuickIdle))]
    private bool _isQuickRunning;

    [ObservableProperty] private double _quickProgress;
    [ObservableProperty] private string? _quickStatus;
    [ObservableProperty] private string _quickErrorsText = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuickResult))]
    private string? _quickSummary;

    [ObservableProperty] private bool _quickHasErrors;
    [ObservableProperty] private Wpf.Ui.Controls.InfoBarSeverity _quickSeverity = Wpf.Ui.Controls.InfoBarSeverity.Informational;
    [ObservableProperty] private string _quickTitle = string.Empty;

    public bool HasCheck => !string.IsNullOrWhiteSpace(CheckSummary);
    public bool HasHistory => HistoryCount > 0;
    public bool IsQuickIdle => !IsQuickRunning;
    public bool HasQuickResult => !string.IsNullOrWhiteSpace(QuickSummary);

    public ObservableCollection<CheckItemRow> CheckItems { get; } = [];
    public ObservableCollection<CheckHistoryRow> History { get; } = [];
    public ObservableCollection<string> QuickDurations { get; } = [];

    /// <summary>
    /// Re-reads the memory after a BIOS change and compares it with the plan: speed, XMP/EXPO, channels, and memory blue
    /// screens and WHEA errors since the plan started (from the Crashes page's scan). Records the check.
    /// </summary>
    [RelayCommand]
    private async Task CheckMyChangeAsync()
    {
        await RunBusyAsync(async ct =>
        {
            await DetectAsync(ct);
            if (Facts is not { } facts || Plan is not { } plan)
            {
                return;
            }

            var since = _journal.Baseline?.Time;
            var summary = MemoryCrashSummary.None;
            try
            {
                var report = await _crashes.ScanAsync(null, ct);
                summary = MemoryCrashRules.Since(report, since ?? DateTimeOffset.Now.AddDays(-7));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Crash scan for the RAM check failed");
            }

            var result = RamVerifier.Compare(plan, facts, summary, since, DateTimeOffset.Now, Loc.Current);
            ShowCheck(result);
            _journal.Add(RamVerifier.ToRecord(result));
            await _ram.SaveJournalAsync(_journal, ct);
            ShowJournal();
        }, Loc.T("Checking your memory and looking for crashes…"));
    }

    /// <summary>Starts the plan over: the next checks look for crashes from now on.</summary>
    [RelayCommand]
    private async Task StartOverAsync()
    {
        if (Facts is not { } facts)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync(Loc.T("Start the plan over?"),
            Loc.T("PC Checker forgets the earlier checks and counts crashes from now on. Use this after you went back to safe settings or changed the memory. Nothing on the PC is changed."),
            Loc.T("Start over"));
        if (!ok)
        {
            return;
        }

        _journal = new RamTuningJournal { Baseline = RamVerifier.Baseline(facts, DateTimeOffset.Now) };
        await _ram.SaveJournalAsync(_journal, CancellationToken.None);
        CheckSummary = null;
        CheckItems.Clear();
        ShowJournal();
    }

    private void ShowCheck(RamCheckResult result)
    {
        CheckSummary = result.Summary;
        CheckTime = Loc.F("Checked {0}", result.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        (CheckSeverity, CheckBadge) = result.Overall switch
        {
            CheckOutcome.Ok => (Severity.Low, Loc.T("Worked")),
            CheckOutcome.Warning => (Severity.Medium, Loc.T("Mostly fine")),
            _ => (Severity.High, Loc.T("Not there yet")),
        };
        CheckItems.Clear();
        foreach (var item in result.Items)
        {
            CheckItems.Add(CheckItemRow.From(item));
        }
    }

    private void ShowJournal()
    {
        BaselineText = _journal.Baseline is { } b
            ? Loc.F("Plan started {0} at {1}. Crashes are counted from then.", b.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                b.ConfiguredMts is { } m ? $"{m} MT/s" : Loc.T("an unknown speed"))
            : null;
        History.Clear();
        foreach (var record in _journal.Checks)
        {
            History.Add(CheckHistoryRow.From(record));
        }

        HistoryCount = History.Count;
    }

    private void UpdateQuickPlan()
    {
        long free = _ram.GetFreeMemoryBytes() ?? 0;
        long bytes = QuickMemoryCheck.PlanBytes(free, QuickPercent);
        QuickPlanText = bytes == 0
            ? Loc.F("Only {0} is free; the check needs more than 2 GB free. Close some programs first.", ByteSize.Format(free))
            : Loc.F("Tests {0} of the {1} free right now and leaves at least 2 GB for Windows.", ByteSize.Format(bytes), ByteSize.Format(free));
    }

    private TimeSpan QuickDuration =>
        QuickMemoryCheck.Durations[Math.Clamp(QuickDurationIndex, 0, QuickMemoryCheck.Durations.Count - 1)];

    private bool CanStartQuickCheck() => !IsQuickRunning;

    [RelayCommand(CanExecute = nameof(CanStartQuickCheck))]
    private async Task StartQuickCheckAsync()
    {
        long free = _ram.GetFreeMemoryBytes() ?? 0;
        long bytes = QuickMemoryCheck.PlanBytes(free, QuickPercent);
        if (bytes == 0)
        {
            UpdateQuickPlan();
            await _dialogs.ShowMessageAsync(Loc.T("Not enough free memory"), QuickPlanText);
            return;
        }

        var duration = QuickDuration;
        bool ok = await _dialogs.ConfirmAsync(Loc.T("Run the quick memory check?"),
            Loc.F("PC Checker fills {0} of free memory with test patterns on every core for {1} minutes and checks that they read back correctly. Other programs may feel slower meanwhile; it stops by itself if Windows runs low on memory. Nothing on the PC is changed.",
                ByteSize.Format(bytes), (int)duration.TotalMinutes),
            Loc.T("Start"));
        if (!ok)
        {
            return;
        }

        _quickCts?.Dispose();
        _quickCts = new CancellationTokenSource();
        IsQuickRunning = true;
        QuickSummary = null;
        QuickHasErrors = false;
        QuickProgress = 0;
        QuickErrorsText = "0";
        QuickStatus = Loc.T("Starting…");
        var progress = new Progress<QuickCheckProgress>(p =>
        {
            QuickProgress = p.Percent;
            QuickErrorsText = p.Errors.ToString("N0", CultureInfo.CurrentCulture);
            QuickStatus = Loc.F("{0} · {1} of {2} · {3} round(s) done", p.Stage,
                p.Elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture), p.Duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture), p.Rounds);
        });

        try
        {
            var result = await _ram.RunQuickCheckAsync(QuickPercent, duration, progress, Loc.Current, _quickCts.Token);
            (QuickSeverity, QuickTitle) = QuickResultBanner(result.Errors, result.Stop);
            QuickSummary = result.Summary;
            QuickHasErrors = result.Errors > 0;
            QuickErrorsText = result.Errors.ToString("N0", CultureInfo.CurrentCulture);
            if (result.Errors > 0)
            {
                _notifications.Warning(Loc.T("Memory errors found"), Loc.T("The quick check found errors. Undo the last BIOS change or lower the memory speed one step."));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quick memory check failed");
            (QuickSeverity, QuickTitle) = (Wpf.Ui.Controls.InfoBarSeverity.Error, Loc.T("The check could not run"));
            QuickSummary = Loc.F("The check could not run: {0}", ex.Message);
        }
        finally
        {
            IsQuickRunning = false;
            QuickStatus = null;
            UpdateQuickPlan();
        }
    }

    /// <summary>The result banner: green only for a run that finished without errors.</summary>
    private static (Wpf.Ui.Controls.InfoBarSeverity Severity, string Title) QuickResultBanner(long errors, QuickCheckStop stop) => (errors, stop) switch
    {
        ( > 0, _) => (Wpf.Ui.Controls.InfoBarSeverity.Error, Loc.T("Errors found")),
        (_, QuickCheckStop.Finished) => (Wpf.Ui.Controls.InfoBarSeverity.Success, Loc.T("No errors")),
        (_, QuickCheckStop.Cancelled) => (Wpf.Ui.Controls.InfoBarSeverity.Informational, Loc.T("Stopped early: no errors so far")),
        (_, QuickCheckStop.LowMemory) => (Wpf.Ui.Controls.InfoBarSeverity.Warning, Loc.T("Stopped: Windows ran low on memory")),
        _ => (Wpf.Ui.Controls.InfoBarSeverity.Warning, Loc.T("The check could not start")),
    };

    private bool CanStopQuickCheck() => IsQuickRunning;

    [RelayCommand(CanExecute = nameof(CanStopQuickCheck))]
    private void StopQuickCheck()
    {
        try
        {
            _quickCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
