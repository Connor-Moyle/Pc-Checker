using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.Benchmark;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>One raw number on a test card ("Compression · 145 MB/s").</summary>
public sealed record BenchmarkMetricRow(string Name, string ValueText, bool IsInformational);

/// <summary>
/// The results card of one test: score, a bar against the reference PC (the middle of the bar), the raw numbers and
/// a note. Shows "Not in this run" when the selected run did not include the test.
/// </summary>
public sealed partial class BenchmarkTestCard : ObservableObject
{
    public BenchmarkTestCard(BenchmarkTestKind kind)
    {
        Kind = kind;
        Name = BenchmarkText.Name(kind);
        Description = BenchmarkText.Description(kind);
        Icon = kind switch
        {
            BenchmarkTestKind.CpuSingleCore => SymbolRegular.DeveloperBoard24,
            BenchmarkTestKind.CpuMultiCore => SymbolRegular.Board24,
            BenchmarkTestKind.MemoryBandwidth => SymbolRegular.Ram20,
            BenchmarkTestKind.MemoryLatency => SymbolRegular.Timer24,
            BenchmarkTestKind.Disk => SymbolRegular.HardDrive20,
            _ => SymbolRegular.Games24,
        };
    }

    public BenchmarkTestKind Kind { get; }
    public string Name { get; }
    public string Description { get; }
    public SymbolRegular Icon { get; }

    public ObservableCollection<BenchmarkMetricRow> Metrics { get; } = [];

    [ObservableProperty] private string _scoreText = "—";
    [ObservableProperty] private double _barValue;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string? _note;
    [ObservableProperty] private bool _hasScore;
    [ObservableProperty] private string? _detailText;

    /// <summary>Shows <paramref name="result"/>, or the "not in this run" state when it is null.</summary>
    public void Show(BenchmarkTestResult? result)
    {
        Metrics.Clear();
        if (result is null)
        {
            ScoreText = "—";
            BarValue = 0;
            HasScore = false;
            StatusText = Loc.T("Not in this run. Run this test on its own, or include it in the next full run.");
            Note = null;
            DetailText = null;
            return;
        }

        HasScore = result.HasScore;
        ScoreText = BenchmarkFormatter.Score(result.Score, CultureInfo.CurrentCulture);
        BarValue = BenchmarkScoring.BarFraction(result.Score);
        StatusText = BenchmarkText.Status(result);
        Note = result.HasScore ? result.Note : null;
        foreach (var metric in result.Metrics)
        {
            Metrics.Add(new BenchmarkMetricRow(metric.Name, BenchmarkFormatter.Metric(metric, CultureInfo.CurrentCulture), metric.Informational));
        }

        DetailText = result.HasScore
            ? Loc.F("{0} repeats in {1}", result.Repeats, BenchmarkFormatter.Duration(result.Seconds))
                + (result.SpreadPercent is { } s ? Loc.F(", \u00B1{0} %", s.ToString("0.#", CultureInfo.CurrentCulture)) : string.Empty)
            : null;
    }
}

/// <summary>A checkbox in Options: whether "Run benchmark" includes this test.</summary>
public sealed partial class BenchmarkTestChoice : ObservableObject
{
    public BenchmarkTestChoice(BenchmarkTestKind kind, Action changed)
    {
        Kind = kind;
        Name = BenchmarkText.Name(kind);
        Description = BenchmarkText.Description(kind);
        _changed = changed;
    }

    private readonly Action _changed;

    public BenchmarkTestKind Kind { get; }
    public string Name { get; }
    public string Description { get; }

    [ObservableProperty] private bool _isSelected = true;

    partial void OnIsSelectedChanged(bool value) => _changed();
}

/// <summary>One saved run in the history list and the compare pickers.</summary>
public sealed partial class BenchmarkRunItem : ObservableObject
{
    public BenchmarkRunItem(BenchmarkRun run)
    {
        Run = run;
        var date = run.Started.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
        Title = string.IsNullOrWhiteSpace(run.Note) ? date : $"{date} · {run.Note}";
        OverallText = BenchmarkFormatter.Score(run.OverallScore, CultureInfo.CurrentCulture);
        int scored = run.ScoredTests.Count;
        var parts = new List<string>
        {
            scored == BenchmarkCatalog.All.Count ? Loc.T("All tests") : Loc.F("{0} of {1} tests", scored, BenchmarkCatalog.All.Count),
        };
        if (run.StoppedEarly)
        {
            parts.Add(Loc.T("stopped early"));
        }

        if (run.Warnings.Count > 0)
        {
            parts.Add(run.Warnings.Count == 1 ? Loc.T("1 warning") : Loc.F("{0} warnings", run.Warnings.Count));
        }

        Detail = string.Join(" · ", parts);
    }

    public BenchmarkRun Run { get; }
    public string Title { get; }
    public string OverallText { get; }
    public string Detail { get; }

    /// <summary>True for the run the result cards are showing.</summary>
    [ObservableProperty] private bool _isShown;

    public override string ToString() => Title;
}

/// <summary>One line of the comparison table.</summary>
public sealed record BenchmarkComparisonRowView(string Name, string BeforeText, string AfterText, string ChangeText, BenchmarkChange Change, bool IsDetail)
{
    public static BenchmarkComparisonRowView From(BenchmarkComparisonRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var culture = CultureInfo.CurrentCulture;
        string Value(double? v) => row.IsDetail ? BenchmarkFormatter.Number(v, culture) : BenchmarkFormatter.Score(v is { } d ? (int)Math.Round(d) : null, culture);
        string name = row.IsDetail ? $"{row.Name} ({row.Unit})" : row.Name;
        return new BenchmarkComparisonRowView(name, Value(row.Before), Value(row.After), BenchmarkFormatter.Change(row.ChangePercent, culture), row.Change, row.IsDetail);
    }
}
