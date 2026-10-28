using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.Crashes;
using PcChecker.Core.Games;
using PcChecker.Core.History;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>One row of the Crashes timeline. The insight is filled in after the context has loaded.</summary>
public sealed partial class CrashRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SolvedText))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isSolved;

    public CrashRow(CrashEvent crash, CrashAdviceText advice, CrashInsight insight)
    {
        Crash = crash;
        Advice = advice;
        Insight = insight;
        var local = crash.Time.ToLocalTime();
        DayText = DayLabel(DateOnly.FromDateTime(local.DateTime), DateOnly.FromDateTime(DateTime.Now));
        TimeText = local.ToString("t", CultureInfo.CurrentCulture);
    }

    public CrashEvent Crash { get; }

    public CrashAdviceText Advice { get; }

    /// <summary>Everything the details pane shows; replaced when the context, driver details or game match arrive.</summary>
    public CrashInsight Insight { get; set; }

    /// <summary>The game this program belongs to, when the Games page found one.</summary>
    public InstalledGame? Game { get; set; }

    public CrashKind Kind => Crash.Kind;

    public string Title => Advice.Title;

    public string Summary => Advice.Summary;

    public string DayText { get; }

    public string TimeText { get; }

    public string SolvedText => IsSolved ? Loc.T("Solved") : string.Empty;

    public SymbolRegular Icon => Kind switch
    {
        CrashKind.BlueScreen => SymbolRegular.ErrorCircle24,
        CrashKind.SuddenShutdown => SymbolRegular.Power24,
        CrashKind.DriverReset => SymbolRegular.DesktopPulse24,
        CrashKind.AppHang => SymbolRegular.AppGeneric24,
        _ => SymbolRegular.WindowApps24,
    };

    public string KindText => KindName(Kind);

    public string AccessibleName => IsSolved ? $"{TimeText}, {Title}, {Summary}, {SolvedText}" : $"{TimeText}, {Title}, {Summary}";

    public static string KindName(CrashKind kind) => kind switch
    {
        CrashKind.BlueScreen => Loc.T("Blue screen"),
        CrashKind.SuddenShutdown => Loc.T("Sudden shutdown"),
        CrashKind.DriverReset => Loc.T("Driver reset"),
        CrashKind.AppHang => Loc.T("App stopped responding"),
        _ => Loc.T("App crash"),
    };

    /// <summary>"Today", "Yesterday" or the long date in the user's format.</summary>
    public static string DayLabel(DateOnly day, DateOnly today) =>
        day == today ? Loc.T("Today")
        : day == today.AddDays(-1) ? Loc.T("Yesterday")
        : day.ToDateTime(TimeOnly.MinValue).ToString("D", CultureInfo.CurrentCulture);
}

/// <summary>One line of the "Most crashing apps" card.</summary>
public sealed class AppCrashRow
{
    public AppCrashRow(AppCrashGroup group)
    {
        Group = group;
        var parts = new List<string>();
        if (group.Crashes > 0)
        {
            parts.Add(group.Crashes == 1 ? Loc.T("1 crash") : Loc.F("{0} crashes", group.Crashes));
        }

        if (group.Hangs > 0)
        {
            parts.Add(group.Hangs == 1 ? Loc.T("1 hang") : Loc.F("{0} hangs", group.Hangs));
        }

        CountText = string.Join(", ", parts);
        var detail = new List<string>();
        if (group.TopModule is { Length: > 0 } module && !string.Equals(module, group.AppName, StringComparison.OrdinalIgnoreCase))
        {
            detail.Add(FaultingModuleCatalog.Find(module) is { } info
                ? Loc.F("Mostly in {0} ({1})", module, Loc.T(info.Owner))
                : Loc.F("Mostly in {0}", module));
        }

        if (group.TopExceptionCode is { } code)
        {
            detail.Add(ExceptionCodeCatalog.Describe(code, Loc.Current));
        }

        detail.Add(Loc.F("last {0}", group.Latest.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        DetailText = string.Join(" · ", detail);
    }

    public AppCrashGroup Group { get; }

    public string AppName => Group.AppName;

    public string CountText { get; }

    public string DetailText { get; }

    public string AccessibleName => $"{AppName}, {CountText}";

    public string FilterButtonName => Loc.F("Show only {0}", AppName);
}

/// <summary>A choice in a filter drop-down.</summary>
public sealed record CrashFilterOption(string Text, int Days, CrashKind[] Kinds)
{
    public override string ToString() => Text;
}

/// <summary>One "Try these fixes" button: what it does, why it is suggested and which crash it is for.</summary>
public sealed class CrashFixRow
{
    public CrashFixRow(CrashFix fix, CrashRow crash, int rank)
    {
        Fix = fix;
        Crash = crash;
        Rank = rank;
        Title = Loc.T(fix.Title);
        Why = fix.Why.Text(Loc.Current);
    }

    public CrashFix Fix { get; }

    public CrashRow Crash { get; }

    public int Rank { get; }

    public string Title { get; }

    public string Why { get; }

    public string RankText => Rank.ToString(CultureInfo.CurrentCulture);

    public bool IsFirst => Rank == 1;

    public SymbolRegular Icon => Fix.Kind switch
    {
        CrashFixKind.UpdateDriver => SymbolRegular.ArrowSync24,
        CrashFixKind.RollBackDriver => SymbolRegular.ArrowUndo24,
        CrashFixKind.ResetOverclock => SymbolRegular.Gauge24,
        CrashFixKind.MemoryDiagnostic => SymbolRegular.Ram20,
        CrashFixKind.RepairWindowsFiles => SymbolRegular.Wrench24,
        CrashFixKind.CheckDisk => SymbolRegular.Storage24,
        CrashFixKind.CheckTemperatures => SymbolRegular.Temperature24,
        CrashFixKind.CheckFans => SymbolRegular.WeatherDuststorm24,
        CrashFixKind.ReinstallRuntimes or CrashFixKind.RepairDotNet or CrashFixKind.ScanGameFolder => SymbolRegular.PuzzlePiece24,
        CrashFixKind.VerifyGameFiles => SymbolRegular.Games24,
        CrashFixKind.DisableOverlay => SymbolRegular.Layer24,
        CrashFixKind.UninstallRecentProgram or CrashFixKind.UpdateApp => SymbolRegular.Apps24,
        CrashFixKind.UninstallRecentUpdate or CrashFixKind.UpdateWindows => SymbolRegular.ArrowDownload24,
        CrashFixKind.TurnOffFastStartup => SymbolRegular.Power24,
        _ => SymbolRegular.Info24,
    };

    public string AccessibleName => $"{Title}: {Why}";
}

/// <summary>One "Probably caused by" line with its confidence.</summary>
public sealed record CrashCauseRow(string Confidence, string Text, CauseConfidence Level)
{
    public bool IsStrong => Level >= CauseConfidence.Likely;

    public string AccessibleName => $"{Confidence}: {Text}";
}

/// <summary>One mini chart in "Before the crash": a metric's points in the ten minutes before.</summary>
public sealed record CrashChartRow(string Label, string RangeText, IReadOnlyList<double> Points, double? Minimum, double? Maximum)
{
    public string AccessibleName => $"{Label}: {RangeText}";
}

/// <summary>What the details pane shows for one crash, in the page's language.</summary>
public sealed class CrashDetails
{
    public CrashDetails(CrashRow row, IReadOnlyList<CrashFixRow> fixes)
    {
        var crash = row.Crash;
        var insight = row.Insight;
        var tr = Loc.Current;
        Row = row;
        Title = insight.Advice.Title;
        WhenText = crash.Time.ToLocalTime().ToString("F", CultureInfo.CurrentCulture);
        Source = Loc.T(crash.Source);
        Explanation = insight.Advice.Explanation;
        DetailLines = insight.Details.Select(l => l.Text(tr)).ToList();
        Causes = insight.Causes.Select(c => new CrashCauseRow(Loc.T(c.ConfidenceText), c.Text.Text(tr), c.Confidence)).ToList();
        LikelyCause = insight.Advice.LikelyCause;
        DriverLines = insight.Driver is { } d ? CrashDriverRules.Describe(d).Select(l => l.Text(tr)).ToList() : [];
        HasDriverDevice = insight.Driver?.DeviceId is { Length: > 0 };
        CanCheckDriverUpdates = insight.Driver is { IsMicrosoft: false, NewerVersion: null };
        AppLines = insight.App is { } app ? AppText(app) : [];
        GameText = row.Game is { } game ? Loc.F("Part of {0} ({1}).", game.Name, game.Launcher.ToString()) : null;
        Patterns = insight.Patterns.Select(p => p.Text.Text(tr)).ToList();
        ContextLines = insight.Context?.Lines.Select(l => l.Text(tr)).ToList() ?? [];
        Charts = insight.Context is { } context ? BuildCharts(context) : [];
        Fixes = fixes;
        Steps = insight.Advice.Steps;
        Pages = insight.Advice.Pages;
        DocsUrl = insight.Advice.DocsUrl;
        HasDump = crash.DumpPath is not null;
        IsSolved = row.IsSolved;
        Facts = BuildFacts(crash);
    }

    public CrashRow Row { get; }

    public string Title { get; }

    public string WhenText { get; }

    public string Source { get; }

    public string Explanation { get; }

    public IReadOnlyList<string> DetailLines { get; }

    public bool HasDetailLines => DetailLines.Count > 0;

    public IReadOnlyList<CrashCauseRow> Causes { get; }

    public bool HasCauses => Causes.Count > 0;

    /// <summary>The older one-paragraph cause (no dump found, hardware error…), shown when there is nothing better.</summary>
    public string? LikelyCause { get; }

    public bool ShowLikelyCause => !HasCauses && !string.IsNullOrWhiteSpace(LikelyCause);

    public IReadOnlyList<string> DriverLines { get; }

    public bool HasDriver => DriverLines.Count > 0;

    public bool HasDriverDevice { get; }

    public bool CanCheckDriverUpdates { get; }

    public IReadOnlyList<string> AppLines { get; }

    public bool HasApp => AppLines.Count > 0;

    public string? GameText { get; }

    public bool HasGame => GameText is not null;

    public IReadOnlyList<string> Patterns { get; }

    public bool HasPatterns => Patterns.Count > 0;

    public IReadOnlyList<string> ContextLines { get; }

    public bool HasContext => ContextLines.Count > 0;

    public IReadOnlyList<CrashChartRow> Charts { get; }

    public bool HasCharts => Charts.Count > 0;

    public IReadOnlyList<CrashFixRow> Fixes { get; }

    public bool HasFixes => Fixes.Count > 0;

    public IReadOnlyList<string> Steps { get; }

    public IReadOnlyList<CrashLink> Pages { get; }

    public bool HasPages => Pages.Count > 0;

    public string? DocsUrl { get; }

    public bool HasDocs => DocsUrl is not null;

    public bool HasDump { get; }

    public bool IsSolved { get; }

    public string SolvedButtonText => IsSolved ? Loc.T("Show as not solved") : Loc.T("Mark as solved");

    public IReadOnlyList<string> Facts { get; }

    private static IReadOnlyList<string> AppText(AppCrashStats app)
    {
        var lines = new List<string>
        {
            Loc.F("{0} crashes and {1} hangs between {2} and {3}.", app.Crashes, app.Hangs,
                app.First.ToLocalTime().ToString("d", CultureInfo.CurrentCulture), app.Last.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)),
        };
        if (app.Versions.Count > 0)
        {
            lines.Add(Loc.F("Versions that crashed: {0}.", string.Join(", ", app.Versions)));
        }

        if (app.TopModules.Count > 0)
        {
            lines.Add(Loc.F("Faulting files: {0}.", string.Join(", ", app.TopModules.Select(m => $"{m.Module} ({m.Count.ToString(CultureInfo.CurrentCulture)})"))));
        }

        return lines;
    }

    private static IReadOnlyList<CrashChartRow> BuildCharts(CrashContext context)
    {
        var rows = new List<CrashChartRow>();
        foreach (var metric in context.Metrics.Where(m => HistoryMetrics.IsTemperature(m.Metric) || m.Metric is HistoryMetrics.CpuLoad or HistoryMetrics.GpuLoad))
        {
            bool temp = HistoryMetrics.IsTemperature(metric.Metric);
            string unit = HistoryMetrics.UnitOf(metric.Metric);
            rows.Add(new CrashChartRow(
                Loc.T(HistoryMetrics.LabelOf(metric.Metric)),
                Loc.F("{0} to {1} {2}", Math.Round(metric.Min).ToString("0", CultureInfo.CurrentCulture), Math.Round(metric.Max).ToString("0", CultureInfo.CurrentCulture), unit),
                metric.Points.Select(p => p.Value).ToList(),
                temp ? 20 : 0,
                temp ? 105 : 100));
        }

        return rows;
    }

    private static IReadOnlyList<string> BuildFacts(CrashEvent crash)
    {
        var facts = new List<string>();
        if (crash.BugCheckCode is { } code)
        {
            facts.Add(Loc.F("Stop code: {0}", BugCheckFormat.WithParameters(code, crash.Parameters)));
        }

        if (crash.Blame is { } blame)
        {
            facts.Add(Loc.F("Probably caused by: {0}", blame.Module));
        }

        if (crash.Driver is { Length: > 0 } driver)
        {
            facts.Add(Loc.F("Driver: {0}", driver));
        }

        if (crash.AppName is { Length: > 0 })
        {
            facts.Add(Loc.F("Program: {0} {1}", crash.AppName, crash.AppVersion ?? string.Empty).TrimEnd());
        }

        if (crash.AppPath is { Length: > 0 } path)
        {
            facts.Add(Loc.F("Path: {0}", path));
        }

        if (crash.ModuleName is { Length: > 0 } module)
        {
            facts.Add(Loc.F("Faulting file: {0} {1}", module, crash.ModuleVersion ?? string.Empty).TrimEnd());
        }

        if (crash.ModulePath is { Length: > 0 } modulePath)
        {
            facts.Add(Loc.F("Faulting file path: {0}", modulePath));
        }

        if (crash.ExceptionCode is { } ex)
        {
            facts.Add(Loc.F("Exception: {0}", ExceptionCodeCatalog.Describe(ex, Loc.Current)));
        }

        if (crash.OsBuild is { } build)
        {
            facts.Add(Loc.F("Windows build: {0}", build));
        }

        if (crash.DumpPath is { Length: > 0 } dump)
        {
            facts.Add(Loc.F("Dump file: {0}", dump));
        }

        if (crash.Modules.Count > 0)
        {
            facts.Add(Loc.F("Drivers loaded at the time: {0}", crash.Modules.Count));
        }

        if (crash.ReportId is { Length: > 0 } report)
        {
            facts.Add(Loc.F("Report id: {0}", report));
        }

        return facts;
    }
}
