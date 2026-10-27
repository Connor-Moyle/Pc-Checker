using PcChecker.Core.Common;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Checkup;

/// <summary>
/// Orders the check-up to-do list: the most serious problems first; within the same severity, the ones the app can fix
/// by itself, the safer fixes and the bigger gains. Pure.
/// </summary>
public static class CheckupPrioritizer
{
    /// <summary>Points per severity. The other factors together move an item by less than one step.</summary>
    public static int SeverityWeight(Severity severity) => severity switch
    {
        Severity.Critical => 100,
        Severity.High => 75,
        Severity.Medium => 50,
        Severity.Low => 25,
        _ => 0,
    };

    /// <summary>
    /// The priority of one item: severity, plus 6 for an automatic fix, minus 4 (Moderate) or 8 (Advanced) for risk,
    /// plus up to 10 for the size of the gain. Stays inside its severity band (at most +16, at least -8).
    /// </summary>
    public static double Score(CheckupItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        double score = SeverityWeight(item.Severity);
        if (item.HasFix)
        {
            score += 6;
        }

        score -= item.Risk switch
        {
            RiskLevel.Moderate => 4,
            RiskLevel.Advanced => 8,
            _ => 0,
        };

        score += double.IsFinite(item.Magnitude) ? Math.Clamp(item.Magnitude, 0, 10) : 0;
        return score;
    }

    /// <summary>
    /// Removes repeats (same id, or the same <see cref="CheckupItem.Topic"/>, keeping the higher-ranked item and, on a
    /// tie, the one with a fix) and sorts by <see cref="Score"/>, then page, then title.
    /// </summary>
    public static IReadOnlyList<CheckupItem> Rank(IEnumerable<CheckupItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var ordered = items
            .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(Score)
            .ThenByDescending(i => i.HasFix)
            .ThenBy(i => i.Source)
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var seenTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CheckupItem>(ordered.Count);
        foreach (var item in ordered)
        {
            if (item.Topic is { } topic && !seenTopics.Add(topic))
            {
                continue;
            }

            result.Add(item);
        }

        return result;
    }
}
