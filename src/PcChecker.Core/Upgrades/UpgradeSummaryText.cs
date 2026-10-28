using System.Globalization;
using System.Text;
using PcChecker.Core.Localization;

namespace PcChecker.Core.Upgrades;

/// <summary>
/// Words for the Upgrade advice page: the gaming balance verdict, the plain-text summary for the clipboard and the
/// question handed to the Ask Claude page.
/// </summary>
public static class UpgradeSummaryText
{
    /// <summary>The verdict's headline.</summary>
    public static string VerdictTitle(BoundAnalysis balance, Translator tr)
    {
        ArgumentNullException.ThrowIfNull(balance);
        ArgumentNullException.ThrowIfNull(tr);
        return balance.Verdict switch
        {
            BoundVerdict.GpuLimited => tr.T("Your graphics card sets the pace"),
            BoundVerdict.CpuLimited => tr.T("Your processor holds your games back"),
            BoundVerdict.Balanced => tr.T("Neither part clearly holds the other back"),
            _ => tr.T("Not enough gaming recorded yet"),
        };
    }

    /// <summary>What the verdict means and what to do about it, in one or two sentences.</summary>
    public static string VerdictDetail(BoundAnalysis balance, bool historyRecording, Translator tr)
    {
        ArgumentNullException.ThrowIfNull(balance);
        ArgumentNullException.ThrowIfNull(tr);
        string time = Duration(balance.GamingMinutes, tr);
        string Pct(double share) => Math.Round(share * 100).ToString("0", CultureInfo.InvariantCulture);
        return balance.Verdict switch
        {
            BoundVerdict.GpuLimited => tr.F("In {0} of recorded gaming the graphics card was at full load {1} % of the time while the processor had room to spare. That is normal; a faster graphics card is what raises frame rates.",
                time, Pct(balance.GpuLimitedShare)),
            BoundVerdict.CpuLimited => tr.F("In {0} of recorded gaming the graphics card often waited below 80 % load while the processor was busy ({1} % of the time). Lower graphics settings will not help much; a faster processor would.",
                time, Pct(balance.CpuLimitedShare)),
            BoundVerdict.Balanced => tr.F("In {0} of recorded gaming the graphics card was rarely maxed out and the processor was not overloaded. A frame-rate cap or V-Sync may be setting the pace, which is fine.", time),
            _ => historyRecording
                ? tr.T("Play a game for about 15 minutes while PC Checker is open (History records in the background), then check again. PC Checker then sees whether the graphics card or the processor holds your games back.")
                : tr.T("History recording is off. Turn it on in Monitor, History, play a game for about 15 minutes, then check again."),
        };
    }

    /// <summary>"Confidence: high." plus a caveat when only the CPU total was recorded.</summary>
    public static string ConfidenceText(BoundAnalysis balance, Translator tr)
    {
        ArgumentNullException.ThrowIfNull(balance);
        ArgumentNullException.ThrowIfNull(tr);
        if (balance.Verdict == BoundVerdict.NoData)
        {
            return string.Empty;
        }

        string level = balance.Confidence switch
        {
            AdviceConfidence.High => tr.T("Confidence: high."),
            AdviceConfidence.Medium => tr.T("Confidence: medium."),
            _ => tr.T("Confidence: low. More gaming in the History makes this surer."),
        };
        return balance.HasPerCoreData || balance.Verdict == BoundVerdict.GpuLimited
            ? level
            : level + " " + tr.T("History records the total processor load, not each core, so one maxed-out core can hide; treat a processor verdict as a strong hint.");
    }

    /// <summary>"GPU-limited", "CPU-limited", "Balanced" or "Not enough data".</summary>
    public static string VerdictShort(BoundVerdict verdict, Translator tr)
    {
        ArgumentNullException.ThrowIfNull(tr);
        return verdict switch
        {
            BoundVerdict.GpuLimited => tr.T("Graphics card limited"),
            BoundVerdict.CpuLimited => tr.T("Processor limited"),
            BoundVerdict.Balanced => tr.T("Balanced"),
            _ => tr.T("Not enough data"),
        };
    }

    /// <summary>"45 min" or "2 h 20 min".</summary>
    public static string Duration(double minutes, Translator tr)
    {
        ArgumentNullException.ThrowIfNull(tr);
        int total = (int)Math.Round(Math.Max(0, minutes));
        return total < 60 ? tr.F("{0} min", total) : tr.F("{0} h {1} min", total / 60, total % 60);
    }

    /// <summary>The plain-text summary for the clipboard.</summary>
    public static string BuildSummary(UpgradeReport report, bool historyRecording, Translator tr)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(tr);
        var sb = new StringBuilder();
        sb.AppendLine(tr.T("PC Checker upgrade advice"));
        sb.AppendLine();
        sb.AppendLine(tr.T("Your PC at a glance"));
        foreach (var item in report.Glance)
        {
            sb.AppendLine("- " + item.Label + ": " + item.Value + (string.IsNullOrWhiteSpace(item.Detail) ? string.Empty : " (" + item.Detail + ")"));
        }

        sb.AppendLine();
        sb.AppendLine(tr.T("Gaming balance") + ": " + VerdictTitle(report.Balance, tr));
        sb.AppendLine(VerdictDetail(report.Balance, historyRecording, tr));
        string confidence = ConfidenceText(report.Balance, tr);
        if (confidence.Length > 0)
        {
            sb.AppendLine(confidence);
        }

        sb.AppendLine();
        sb.AppendLine(tr.T("Fix first (free)"));
        if (report.FixFirst.Count == 0)
        {
            sb.AppendLine("- " + tr.T("Nothing found: the setup is in order."));
        }

        foreach (var fix in report.FixFirst)
        {
            sb.AppendLine("- " + fix.Title + ". " + fix.Detail);
        }

        sb.AppendLine();
        sb.AppendLine(tr.T("Best next upgrades"));
        if (report.Upgrades.Count == 0)
        {
            sb.AppendLine("- " + tr.T("No upgrade stands out: this PC is well balanced for how it is used."));
        }

        int rank = 1;
        foreach (var up in report.Upgrades)
        {
            sb.AppendLine($"{rank++}. {up.Title} ({up.ImpactText}). {up.Why}");
            if (up.Options.Count > 0)
            {
                sb.AppendLine("   " + tr.T("Options") + ": " + string.Join("; ", up.Options));
            }

            if (!string.IsNullOrWhiteSpace(up.Note))
            {
                sb.AppendLine("   " + up.Note);
            }
        }

        if (report.Windows11.Status != Win11Status.AlreadyWindows11 && report.Windows11.Summary.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(tr.T("Windows 11") + ": " + report.Windows11.Summary);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The question for the Ask Claude page: the full spec and usage summary plus what a good answer covers.</summary>
    public static string AskPrompt(UpgradeReport report, bool historyRecording, Translator tr)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(tr);
        var sb = new StringBuilder();
        sb.AppendLine(tr.T("Please make me a personalised upgrade plan for my PC. Here is what PC Checker found, including how my games used the hardware:"));
        sb.AppendLine();
        sb.AppendLine(BuildSummary(report, historyRecording, tr));
        sb.AppendLine();
        sb.Append(tr.T("Ask me first what I mainly use the PC for and my rough budget if it matters. Then list the upgrades in order of value, say in plain words what each changes, and what to check before buying (BIOS update, power supply, case space, free slots). Do not guess prices."));
        return sb.ToString();
    }
}
