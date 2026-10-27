using System.Text;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Debloat;

/// <summary>Builds the plain-English confirmation shown before a batch is applied or reverted. Pure; tested.</summary>
public static class DebloatConfirmationText
{
    /// <summary>
    /// Lists the exact changes grouped by risk (Advanced first), then explains the restore point, one-way changes, and
    /// whether File Explorer or Windows has to restart.
    /// </summary>
    public static string Build(IReadOnlyList<DebloatTweak> tweaks, bool revert, bool removeAppsForAllUsers = false)
    {
        ArgumentNullException.ThrowIfNull(tweaks);
        var sb = new StringBuilder();
        foreach (var group in tweaks.GroupBy(t => t.Risk).OrderByDescending(g => g.Key))
        {
            sb.AppendLine($"{RiskHeading(group.Key)} ({group.Count()})");
            foreach (var tweak in group)
            {
                sb.Append("  • ").Append(tweak.Title);
                if (!revert && tweak.Warning is not null)
                {
                    sb.Append(" — ").Append(tweak.Warning);
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        if (revert)
        {
            sb.AppendLine("Each setting goes back to the value it had before PC Checker changed it, or to the Windows default when no earlier value was saved.");
            var oneWay = tweaks.Where(t => !t.Reversible).ToList();
            if (oneWay.Count > 0)
            {
                sb.AppendLine($"{oneWay.Count} of these cannot be undone here (removed apps or OneDrive); reinstall them from the Microsoft Store or microsoft.com.");
            }
        }
        else
        {
            sb.AppendLine("A restore point is created first, and the current values are saved so you can revert any of these on this page.");
            var oneWay = tweaks.Where(t => !t.Reversible).ToList();
            if (oneWay.Count > 0)
            {
                sb.AppendLine($"{oneWay.Count} change{(oneWay.Count == 1 ? " is" : "s are")} one-way ({string.Join(", ", oneWay.Take(4).Select(t => t.Title.Replace("Remove ", string.Empty)))}{(oneWay.Count > 4 ? ", …" : string.Empty)}): reinstall from the Microsoft Store to undo.");
                if (oneWay.Any(t => t.Operations.Any(o => o is AppxRemovalOp)))
                {
                    sb.AppendLine(removeAppsForAllUsers
                        ? "Apps are removed for every user account on this PC and for new accounts."
                        : "Apps are removed for your account only.");
                }
            }
        }

        if (tweaks.Any(t => t.RestartsExplorer))
        {
            sb.AppendLine("Taskbar, Start and File Explorer changes show after File Explorer restarts (offered when done).");
        }

        if (tweaks.Any(t => t.RequiresRestart))
        {
            sb.AppendLine("Some changes finish after you restart Windows or sign out.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>True when the confirmation should use the Danger style (advanced or one-way changes).</summary>
    public static bool IsDanger(IEnumerable<DebloatTweak> tweaks, bool revert) =>
        tweaks.Any(t => t.Risk == RiskLevel.Advanced || (!revert && !t.Reversible));

    private static string RiskHeading(RiskLevel risk) => risk switch
    {
        RiskLevel.Advanced => "Advanced",
        RiskLevel.Moderate => "Moderate",
        _ => "Safe",
    };
}
