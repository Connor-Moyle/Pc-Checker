using System.Text;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Debloat;

/// <summary>Builds the plain-English confirmation shown before a batch is applied or reverted. Pure; tested.</summary>
public static class DebloatConfirmationText
{
    /// <summary>Most exact changes listed under one tweak before the rest are summarised.</summary>
    public const int MaxChangesPerTweak = 4;

    /// <summary>Most exact-change lines in the whole dialog; larger batches list titles only after that.</summary>
    public const int MaxChangeLines = 40;

    /// <summary>
    /// Lists the exact changes grouped by risk (Advanced first), then explains the restore point, one-way changes, and
    /// whether File Explorer or Windows has to restart.
    /// </summary>
    public static string Build(IReadOnlyList<DebloatTweak> tweaks, bool revert, bool removeAppsForAllUsers = false, string? cpuName = null)
    {
        ArgumentNullException.ThrowIfNull(tweaks);
        var sb = new StringBuilder();
        if (!revert && X3dGameBarConflict(tweaks, cpuName))
        {
            sb.AppendLine("Warning: " + Hardware.AmdPlatformRules.GameBarX3dWarning);
            sb.AppendLine();
        }

        int changeLines = 0;
        bool truncated = false;
        foreach (var group in tweaks.GroupBy(t => t.Risk).OrderByDescending(g => g.Key))
        {
            sb.AppendLine($"{RiskHeading(group.Key)} ({group.Count()})");
            foreach (var tweak in group)
            {
                sb.Append("  • ").AppendLine(tweak.Title);
                if (!revert && tweak.Warning is not null)
                {
                    sb.Append("      Note: ").AppendLine(tweak.Warning);
                }

                var changes = Changes(tweak, revert);
                if (changeLines >= MaxChangeLines)
                {
                    truncated |= changes.Count > 0;
                    continue;
                }

                foreach (var change in changes.Take(MaxChangesPerTweak))
                {
                    sb.Append("      - ").AppendLine(change);
                    changeLines++;
                }

                if (changes.Count > MaxChangesPerTweak)
                {
                    sb.AppendLine($"      - and {changes.Count - MaxChangesPerTweak} more");
                    changeLines++;
                }
            }

            sb.AppendLine();
        }

        if (truncated)
        {
            sb.AppendLine("The exact changes of the remaining tweaks are shown when you point at their rows on the page.");
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
            sb.AppendLine("One restore point is created before the first change, covering every tweak above (app removals included), and the current values are saved so you can revert the reversible ones on this page.");
            var oneWay = tweaks.Where(t => !t.Reversible).ToList();
            if (oneWay.Count > 0)
            {
                sb.AppendLine($"{oneWay.Count} change{(oneWay.Count == 1 ? " is" : "s are")} one-way ({string.Join(", ", oneWay.Take(4).Select(t => t.Title.Replace("Remove ", string.Empty)))}{(oneWay.Count > 4 ? ", …" : string.Empty)}): reinstall from the Microsoft Store to undo.");
                sb.AppendLine("If the restore point cannot be created, nothing is changed.");
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

    /// <summary>The exact registry, service, task, app and feature changes of one tweak, in plain words.</summary>
    public static IReadOnlyList<string> Changes(DebloatTweak tweak, bool revert)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        if (tweak.DelegatesToOptimizationId is { } optimizationId)
        {
            return [$"{(revert ? "Revert" : "Apply")} the Optimize page action \"{optimizationId}\""];
        }

        return tweak.Operations.Select(op => revert ? DescribeRevert(op) : op.Describe()).ToList();
    }

    private static string DescribeRevert(DebloatOperation op) => op switch
    {
        RegistryValueOp reg => $"Restore {RegistryValueCodec.HiveName(reg.Hive)}\\{reg.Path}\\{(reg.Name.Length == 0 ? "(Default)" : reg.Name)}",
        RegistryKeyRemovalOp keyOp => $"Recreate the key {RegistryValueCodec.HiveName(keyOp.Hive)}\\{keyOp.Path}",
        ServiceOp svc => $"Restore the start type of service {svc.Name}",
        ScheduledTaskOp task => $"Restore scheduled task {task.Path}",
        OptionalFeatureOp feature => $"Turn the Windows feature {feature.FriendlyName} back {(feature.DisableWhenApplied ? "on" : "off")}",
        AppxRemovalOp app => $"{app.FriendlyName}: reinstall from the Microsoft Store (not done here)",
        _ => "Undo: " + op.Describe(),
    };

    /// <summary>
    /// True when the batch removes or turns off Xbox Game Bar on a dual-CCD Ryzen 9 X3D, which AMD's CCD parking relies
    /// on: the Game Bar app removal itself, or a tweak that runs the Optimize page's Game Bar action.
    /// </summary>
    public static bool X3dGameBarConflict(IEnumerable<DebloatTweak> tweaks, string? cpuName) =>
        Hardware.CpuTraits.IsDualCcdX3D(cpuName) && tweaks.Any(t =>
            Hardware.AmdPlatformRules.ActionWarning(t.Id, cpuName) == Hardware.AmdPlatformRules.GameBarX3dWarning ||
            Hardware.AmdPlatformRules.ActionWarning(t.DelegatesToOptimizationId, cpuName) == Hardware.AmdPlatformRules.GameBarX3dWarning);

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
