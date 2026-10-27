using PcChecker.Core.Common;
using PcChecker.Core.History;

namespace PcChecker.App.ViewModels;

/// <summary>One choice in the History page's range picker, with its label already translated.</summary>
public sealed record HistoryRangeOption(string Label, HistoryRange Range)
{
    public override string ToString() => Label;
}

/// <summary>
/// One chart on the History page. <see cref="Tone"/> picks the line colour in XAML: temperature, load, power,
/// memory, disk or health.
/// </summary>
public sealed record HistoryChartItem(
    string Metric,
    string Title,
    string Tone,
    string Unit,
    string Headline,
    string StatsText,
    IReadOnlyList<HistoryPoint> Points,
    DateTimeOffset From,
    DateTimeOffset To,
    double Minimum,
    double Maximum,
    double Limit,
    TimeSpan GapThreshold);

/// <summary>One line in the notable events list.</summary>
public sealed record HistoryEventRow(string Title, string Detail, string TimeText, Severity Severity, HistoryEventKind Kind)
{
    public static HistoryEventRow From(HistoryEvent e) => new(e.Title, e.Detail,
        e.Time.ToLocalTime().ToString("ddd d MMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture), e.Severity, e.Kind);
}
