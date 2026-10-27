using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.Benchmark;

namespace PcChecker.App.ViewModels;

/// <summary>History part of the Benchmark page: the shown run, the saved runs, the comparison and text export.</summary>
public sealed partial class BenchmarkViewModel
{
    private IReadOnlyList<BenchmarkRun> _allRuns = [];
    private BenchmarkComparison? _comparison;
    private bool _applyingHistory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShownRun))]
    [NotifyPropertyChangedFor(nameof(HasNoShownRun))]
    [NotifyCanExecuteChangedFor(nameof(CopyResultsCommand), nameof(SaveResultsCommand), nameof(DeleteShownRunCommand))]
    private BenchmarkRunItem? _shownRun;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyComparisonCommand), nameof(SaveComparisonCommand), nameof(SwapComparisonCommand))]
    private BenchmarkRunItem? _compareBefore;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyComparisonCommand), nameof(SaveComparisonCommand), nameof(SwapComparisonCommand))]
    private BenchmarkRunItem? _compareAfter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparison))]
    private string? _comparisonSummary;

    /// <summary>Saved runs, newest first.</summary>
    public ObservableCollection<BenchmarkRunItem> Runs { get; } = [];

    public ObservableCollection<BenchmarkComparisonRowView> ComparisonRows { get; } = [];

    public ObservableCollection<string> ComparisonNotes { get; } = [];

    public bool HasShownRun => ShownRun is not null;

    public bool HasNoShownRun => ShownRun is null;

    public bool HasHistory => Runs.Count > 0;

    public bool HasNoHistory => Runs.Count == 0;

    public bool CanCompare => Runs.Count >= 2;

    public bool HasComparison => !string.IsNullOrWhiteSpace(ComparisonSummary);

    partial void OnCompareBeforeChanged(BenchmarkRunItem? value) => UpdateComparison();

    partial void OnCompareAfterChanged(BenchmarkRunItem? value) => UpdateComparison();

    /// <summary>Saves a finished run; when saving fails the run is still shown, with the reason.</summary>
    private async Task SaveRunAsync(BenchmarkRun run)
    {
        IReadOnlyList<BenchmarkRun> runs;
        try
        {
            runs = await _history.AddAsync(run, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The benchmark run could not be saved.");
            ErrorMessage = Loc.F("The run could not be saved to the history: {0}", ex.Message);
            runs = BenchmarkHistoryRules.Normalize(_allRuns.Append(run));
        }

        ApplyHistory(runs, run.Id);
    }

    /// <summary>Refreshes the history list, shows <paramref name="showId"/> and picks the default comparison for it.</summary>
    private void ApplyHistory(IReadOnlyList<BenchmarkRun> runs, Guid? showId)
    {
        _applyingHistory = true;
        try
        {
            _allRuns = runs;
            Runs.Clear();
            foreach (var run in runs)
            {
                Runs.Add(new BenchmarkRunItem(run));
            }

            var shown = Runs.FirstOrDefault(r => r.Run.Id == showId) ?? Runs.FirstOrDefault();
            if (BenchmarkHistoryRules.DefaultComparison(runs) is { } pair)
            {
                CompareBefore = Runs.FirstOrDefault(r => r.Run.Id == pair.Before.Id);
                CompareAfter = Runs.FirstOrDefault(r => r.Run.Id == pair.After.Id);
            }
            else
            {
                CompareBefore = CompareAfter = null;
            }

            Show(shown);
        }
        finally
        {
            _applyingHistory = false;
        }

        UpdateComparison();
    }

    [RelayCommand]
    private void ShowRun(BenchmarkRunItem? item)
    {
        if (item is not null)
        {
            Show(item);
        }
    }

    private void Show(BenchmarkRunItem? item)
    {
        foreach (var row in Runs)
        {
            row.IsShown = ReferenceEquals(row, item);
        }

        ShownRun = item;
        if (item is null)
        {
            foreach (var card in Cards)
            {
                card.Show(null);
            }

            Subtitle = DefaultSubtitle;
            OverallText = TestsText = RunDateText = ChangeText = "—";
            OverallSubtitle = TestsSubtitle = RunDateSubtitle = ChangeSubtitle = string.Empty;
            RunWarnings = null;
            StoppedEarlyMessage = null;
            return;
        }

        var run = item.Run;
        foreach (var card in Cards)
        {
            card.Show(run.Find(card.Kind));
        }

        var culture = CultureInfo.CurrentCulture;
        var when = run.Started.LocalDateTime;
        Subtitle = Loc.F("Showing the run from {0}. Reference PC = 1,000 per test.", when.ToString("g", culture));
        OverallText = BenchmarkFormatter.Score(run.OverallScore, culture);
        OverallSubtitle = BenchmarkText.Rating(run.OverallScore);
        int scored = run.ScoredTests.Count;
        TestsText = Loc.F("{0} of {1}", scored, BenchmarkCatalog.All.Count);
        TestsSubtitle = Loc.F("Took {0}", BenchmarkFormatter.Duration(run.Seconds));
        RunDateText = when.ToString("d MMM", culture);
        RunDateSubtitle = string.IsNullOrWhiteSpace(run.Note) ? when.ToString("t", culture) : $"{when.ToString("t", culture)} · {run.Note}";
        RunWarnings = run.Warnings.Count == 0 ? null : string.Join("\n", run.Warnings);
        StoppedEarlyMessage = run.StoppedEarly ? Loc.T("This run was stopped before every chosen test finished. Only the finished tests have scores.") : null;

        // Change against the newest older run that completed the same tests.
        var previous = _allRuns.Where(r => r.Started < run.Started).FirstOrDefault(r => BenchmarkComparer.SameTests(r, run));
        if (previous is null)
        {
            ChangeText = "—";
            ChangeSubtitle = Loc.T("Run again later to see the change");
        }
        else
        {
            var change = BenchmarkComparer.ChangePercent(previous.OverallScore, run.OverallScore);
            ChangeText = BenchmarkFormatter.Change(change, culture);
            ChangeSubtitle = Loc.F("Since {0} ({1})", previous.Started.LocalDateTime.ToString("d MMM", culture), BenchmarkText.ChangeWord(BenchmarkComparer.Classify(change)));
        }
    }

    private void UpdateComparison()
    {
        if (_applyingHistory)
        {
            return;
        }

        ComparisonRows.Clear();
        ComparisonNotes.Clear();
        if (CompareBefore is null || CompareAfter is null || ReferenceEquals(CompareBefore, CompareAfter))
        {
            _comparison = null;
            ComparisonSummary = CanCompare ? Loc.T("Pick two different runs to compare.") : null;
            return;
        }

        _comparison = BenchmarkComparer.Compare(CompareBefore.Run, CompareAfter.Run);
        ComparisonRows.Add(BenchmarkComparisonRowView.From(_comparison.Overall));
        foreach (var row in _comparison.Tests)
        {
            ComparisonRows.Add(BenchmarkComparisonRowView.From(row));
            foreach (var detail in _comparison.Details.Where(d => d.Test == row.Test))
            {
                ComparisonRows.Add(BenchmarkComparisonRowView.From(detail));
            }
        }

        foreach (var note in _comparison.Notes)
        {
            ComparisonNotes.Add(note);
        }

        var overall = _comparison.Overall;
        ComparisonSummary = overall.Change == BenchmarkChange.NotComparable
            ? Loc.T("Compare the tests both runs completed below.")
            : Loc.F("Overall {0} ({1}). Changes under {2} % are normal run-to-run variation.",
                BenchmarkFormatter.Change(overall.ChangePercent, CultureInfo.CurrentCulture), BenchmarkText.ChangeWord(overall.Change), BenchmarkComparer.NoiseThresholdPercent);
        CopyComparisonCommand.NotifyCanExecuteChanged();
        SaveComparisonCommand.NotifyCanExecuteChanged();
    }

    private bool CanExportRun() => ShownRun is not null;

    private bool CanExportComparison() => _comparison is not null;

    private bool CanSwap() => CompareBefore is not null && CompareAfter is not null;

    [RelayCommand(CanExecute = nameof(CanSwap))]
    private void SwapComparison() => (CompareBefore, CompareAfter) = (CompareAfter, CompareBefore);

    [RelayCommand(CanExecute = nameof(CanExportRun))]
    private void CopyResults()
    {
        if (ShownRun is { } item)
        {
            CopyText(BenchmarkFormatter.ExportRun(item.Run), Loc.T("The results were copied as text. Paste them into a message or forum post."));
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportRun))]
    private async Task SaveResultsAsync()
    {
        if (ShownRun is { } item)
        {
            await SaveTextAsync(Loc.T("Save benchmark results"), $"benchmark-{item.Run.Started.LocalDateTime:yyyyMMdd-HHmm}.txt", BenchmarkFormatter.ExportRun(item.Run));
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportComparison))]
    private void CopyComparison()
    {
        if (_comparison is { } comparison)
        {
            CopyText(BenchmarkFormatter.ExportComparison(comparison), Loc.T("The comparison was copied as text."));
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportComparison))]
    private async Task SaveComparisonAsync()
    {
        if (_comparison is { } comparison)
        {
            await SaveTextAsync(Loc.T("Save benchmark comparison"),
                $"benchmark-comparison-{comparison.Before.Started.LocalDateTime:yyyyMMdd}-{comparison.After.Started.LocalDateTime:yyyyMMdd}.txt",
                BenchmarkFormatter.ExportComparison(comparison));
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportRun))]
    private async Task DeleteShownRunAsync()
    {
        if (ShownRun is not { } item)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Loc.T("Remove this run from the history?"),
                Loc.F("The run from {0} is removed from PC Checker's benchmark history. This cannot be undone; copy or save it first if you want to keep it.", item.Title),
                Loc.T("Remove"), isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var runs = await _history.DeleteAsync(item.Run.Id, ct);
            ApplyHistory(runs, runs.FirstOrDefault()?.Id);
        }, Loc.T("Removing the run\u2026"));
    }

    private void CopyText(string text, string message)
    {
        try
        {
            Clipboard.SetText(text);
            _notifications.Success(Loc.T("Copied"), message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
            ErrorMessage = Loc.F("The text could not be copied to the clipboard: {0}", ex.Message);
        }
    }

    private async Task SaveTextAsync(string title, string suggestedName, string text)
    {
        var path = await _dialogs.SaveFileAsync(title, suggestedName, "Text file (*.txt)|*.txt");
        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, text);
            _notifications.Success(Loc.T("Saved"), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Saving the benchmark text failed.");
            _notifications.Error(Loc.T("Could not save"), ex.Message);
        }
    }
}
