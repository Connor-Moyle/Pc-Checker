using System.Globalization;
using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>The "Crash summary" card: the single most likely cause across recent crashes and the fix to try first.</summary>
public sealed record CrashSummaryCard(CrashLine Headline, CrashLine Detail, CrashCause? Cause, CrashFix? TopFix, string? CrashId);

/// <summary>
/// Weighs the "probably caused by" guesses of the recent serious crashes (blue screens, shutdowns, driver resets) and
/// the patterns between them into one most likely cause, and picks the fix that ranks highest across them. Falls back
/// to the most crashing program when only apps crashed. Solved crashes are left out. Pure.
/// </summary>
public static class CrashSummaryRules
{
    public static CrashSummaryCard? Build(IReadOnlyList<CrashInsight> insights, DateTimeOffset now, IReadOnlyCollection<string>? solved = null, int days = CrashRules.SummaryDays)
    {
        ArgumentNullException.ThrowIfNull(insights);
        var recent = insights.Where(i => i.Crash.Time >= now.AddDays(-days) && (solved is null || !solved.Contains(i.Crash.Id))).ToList();
        var serious = recent.Where(i => CrashPatternRules.IsSerious(i.Crash)).ToList();
        if (serious.Count == 0)
        {
            return AppSummary(recent);
        }

        var weights = new Dictionary<CrashCause, double>();
        foreach (var insight in serious)
        {
            foreach (var guess in insight.Causes.Where(g => g.Cause is not null))
            {
                double w = guess.Confidence switch { CauseConfidence.VeryLikely => 3, CauseConfidence.Likely => 2, _ => 1 };
                weights[guess.Cause!.Value] = weights.GetValueOrDefault(guess.Cause!.Value) + w;
            }
        }

        var fixScores = new Dictionary<CrashFixKind, (double Score, CrashFix Fix, DateTimeOffset Time)>();
        foreach (var insight in serious)
        {
            foreach (var (fix, rank) in insight.Fixes.Take(3).Select((f, i) => (f, i)))
            {
                double s = fix.Score / (double)(rank + 1);
                if (fixScores.TryGetValue(fix.Kind, out var existing))
                {
                    fixScores[fix.Kind] = (existing.Score + s, insight.Crash.Time > existing.Time ? fix : existing.Fix, insight.Crash.Time > existing.Time ? insight.Crash.Time : existing.Time);
                }
                else
                {
                    fixScores[fix.Kind] = (s, fix, insight.Crash.Time);
                }
            }
        }

        var top = fixScores.OrderByDescending(kv => kv.Value.Score).Select(kv => kv.Value.Fix).FirstOrDefault();
        var newest = serious.OrderByDescending(i => i.Crash.Time).First();
        string count = serious.Count.ToString(CultureInfo.InvariantCulture);
        if (weights.Count == 0)
        {
            return new CrashSummaryCard(new CrashLine(T("{0} serious crashes in the last {1} days, without a clear common cause."), count, days.ToString(CultureInfo.InvariantCulture)),
                CrashLine.Of(T("Start with the newest crash below and its first fix.")), null, top, newest.Crash.Id);
        }

        var cause = weights.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
        var strongest = serious.SelectMany(i => i.Patterns).OrderByDescending(p => p.Strength).FirstOrDefault();
        var detail = strongest?.Text ?? new CrashLine(T("Based on what {0} crashes have in common: their stop codes, the drivers named in their memory dumps and what happened just before."), count);
        var headline = serious.Count == 1
            ? new CrashLine(T("Most likely cause of the last crash: {0}."), new LocalizedText(CrashCauseText.Name(cause)))
            : new CrashLine(T("Most likely cause across {0} recent crashes: {1}."), count, new LocalizedText(CrashCauseText.Name(cause)));
        return new CrashSummaryCard(headline, detail, cause, top, newest.Crash.Id);
    }

    private static CrashSummaryCard? AppSummary(IReadOnlyList<CrashInsight> recent)
    {
        var apps = recent.Where(i => i.Crash.Kind is CrashKind.AppCrash or CrashKind.AppHang).ToList();
        if (apps.Count == 0)
        {
            return null;
        }

        var worst = apps.GroupBy(i => i.Crash.AppName ?? "?", StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First();
        var newest = worst.OrderByDescending(i => i.Crash.Time).First();
        var module = worst.Select(i => i.Module).FirstOrDefault(m => m is not null);
        var detail = module is not null
            ? new CrashLine(T("Most of its crashes are in {0}."), new LocalizedText(module.Owner))
            : CrashLine.Of(T("Windows itself is stable: no blue screens or sudden shutdowns recently."));
        return new CrashSummaryCard(new CrashLine(T("No blue screens recently; {0} crashed most ({1} times)."), worst.Key, worst.Count().ToString(CultureInfo.InvariantCulture)),
            detail, null, newest.Fixes.FirstOrDefault(), newest.Crash.Id);
    }
}
