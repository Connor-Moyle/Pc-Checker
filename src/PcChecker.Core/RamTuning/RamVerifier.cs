using System.Globalization;
using PcChecker.Core.Localization;
using PcChecker.Core.Overclocking;

namespace PcChecker.Core.RamTuning;

/// <summary>How one item of a check turned out.</summary>
public enum CheckOutcome
{
    Ok,
    Warning,
    Problem,
}

/// <summary>One line of a check: what was compared and what it means.</summary>
public sealed record RamCheckItem(CheckOutcome Outcome, string Title, string Detail);

/// <summary>The result of "Check my change".</summary>
public sealed record RamCheckResult(DateTimeOffset Time, CheckOutcome Overall, string Summary, IReadOnlyList<RamCheckItem> Items, int? ConfiguredMts,
    string XmpState, ChannelMode Channels, MemoryCrashSummary Crashes);

/// <summary>A saved check (the page's history) or the starting point of the plan.</summary>
public sealed record RamCheckRecord(DateTimeOffset Time, int? ConfiguredMts, string XmpState, ChannelMode Channels, CheckOutcome Overall, string Summary,
    int MemoryBlueScreens, int WheaErrors);

/// <summary>What the page remembers: when the plan started (the speed before any change) and every check since.</summary>
public sealed class RamTuningJournal
{
    /// <summary>The newest checks kept.</summary>
    public const int MaxChecks = 50;

    public RamCheckRecord? Baseline { get; set; }

    public List<RamCheckRecord> Checks { get; set; } = [];

    /// <summary>Adds a check, newest first, keeping at most <see cref="MaxChecks"/>.</summary>
    public void Add(RamCheckRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Checks.Insert(0, record);
        if (Checks.Count > MaxChecks)
        {
            Checks.RemoveRange(MaxChecks, Checks.Count - MaxChecks);
        }
    }
}

/// <summary>
/// Compares what Windows reports after a BIOS change with the plan: configured speed, XMP/EXPO state, channel mode and
/// memory-related crashes and hardware errors since the plan started. Pure.
/// </summary>
public static class RamVerifier
{
    /// <summary>Speeds within this fraction of the plan count as reached (same tolerance as the XMP rules).</summary>
    public const double Tolerance = 0.03;

    public static RamCheckResult Compare(RamTuningPlan plan, RamFacts facts, MemoryCrashSummary crashes, DateTimeOffset? since, DateTimeOffset now, Translator? tr = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(crashes);
        tr ??= Translator.English;
        var items = new List<RamCheckItem>();
        int? mts = facts.ConfiguredMts;

        // Speed
        if (mts is null)
        {
            items.Add(new(CheckOutcome.Warning, tr.T("Speed unknown"), tr.T("Windows did not report the memory speed.")));
        }
        else if (plan.ExpectedMts is { } expected && mts < expected * (1 - Tolerance))
        {
            items.Add(new(CheckOutcome.Problem, tr.F("Still at {0} MT/s", mts),
                tr.F("The plan expects about {0} MT/s. The setting did not stick, or the board fell back to safe settings after a failed start.", expected)));
        }
        else if (plan.ExpectedMts is { } expected2 && mts > expected2 * (1 + Tolerance))
        {
            items.Add(new(CheckOutcome.Warning, tr.F("Running at {0} MT/s", mts),
                tr.F("That is faster than the plan's {0} MT/s. Fine if you set it on purpose: test it well.", expected2)));
        }
        else
        {
            items.Add(new(CheckOutcome.Ok, tr.F("Running at {0} MT/s", mts),
                plan.ExpectedMts is null ? tr.T("The speed Windows reports now.") : tr.T("As planned.")));
        }

        // XMP / EXPO
        items.Add(facts.Xmp.State switch
        {
            XmpEvaluation.Enabled => new(CheckOutcome.Ok, tr.F("{0} is on", plan.ProfileName), tr.T("The memory runs at its rated speed.")),
            XmpEvaluation.Disabled => new(CheckOutcome.Problem, tr.F("{0} is off", plan.ProfileName),
                tr.F("The kit is rated {0} MT/s. Turn the profile on in the BIOS (stage 2).", facts.RatedMts ?? 0)),
            _ => new(CheckOutcome.Warning, tr.F("{0} state unknown", plan.ProfileName), tr.T("Windows reports a standard speed, so the profile may be off. Check the BIOS.")),
        });

        // Channels
        items.Add(facts.Layout.Mode switch
        {
            ChannelMode.Dual => new(CheckOutcome.Ok, tr.T("Dual channel"), tr.T("Both memory channels are in use.")),
            ChannelMode.Single => new(CheckOutcome.Problem, tr.T("Single channel"), facts.Layout.SlotAdvice ?? tr.T("Only one memory channel is in use, which halves the bandwidth.")),
            _ => new(CheckOutcome.Warning, tr.T("Channel mode unknown"), tr.T("Windows did not say which slots are used.")),
        });

        // Crashes and hardware errors since the plan started
        string sinceText = since is { } s ? s.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : tr.T("the plan started");
        if (crashes.MemoryBlueScreens > 0)
        {
            items.Add(new(CheckOutcome.Problem, tr.F("{0} memory blue screen(s) since {1}", crashes.MemoryBlueScreens, sinceText),
                tr.F("Stop codes {0} usually mean the memory setting is not stable. Go back one speed step or undo the last timing change.", string.Join(", ", crashes.StopCodes))));
        }

        if (crashes.WheaErrors > 0)
        {
            items.Add(new(CheckOutcome.Warning, tr.F("{0} hardware error(s) (WHEA) since {1}", crashes.WheaErrors, sinceText),
                tr.T("On Ryzen these usually mean FCLK is too high: go back to 2000 MHz (AM5) or 1800 MHz (AM4). On Intel, lower the memory speed one step.")));
        }

        if (!crashes.Any)
        {
            items.Add(new(CheckOutcome.Ok, tr.T("No memory crashes"), tr.F("No memory blue screens or hardware errors since {0}.", sinceText)));
        }

        var overall = items.Any(i => i.Outcome == CheckOutcome.Problem) ? CheckOutcome.Problem
            : items.Any(i => i.Outcome == CheckOutcome.Warning) ? CheckOutcome.Warning
            : CheckOutcome.Ok;
        string summary = overall switch
        {
            CheckOutcome.Ok => tr.T("Your change worked. Now run a stability test before you rely on it."),
            CheckOutcome.Warning => tr.T("Mostly fine; read the yellow items."),
            _ => tr.T("Not there yet; read the red items."),
        };
        return new RamCheckResult(now, overall, summary, items, mts, facts.Xmp.State, facts.Layout.Mode, crashes);
    }

    /// <summary>The record saved for a check.</summary>
    public static RamCheckRecord ToRecord(RamCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new RamCheckRecord(result.Time, result.ConfiguredMts, result.XmpState, result.Channels, result.Overall, result.Summary,
            result.Crashes.MemoryBlueScreens, result.Crashes.WheaErrors);
    }

    /// <summary>The starting point of the plan, taken from the first detection.</summary>
    public static RamCheckRecord Baseline(RamFacts facts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return new RamCheckRecord(now, facts.ConfiguredMts, facts.Xmp.State, facts.Layout.Mode, CheckOutcome.Ok, "Starting point", 0, 0);
    }
}
