namespace PcChecker.Core.Cleanup;

/// <summary>Preconditions, per-folder file rules and small helpers for the page.</summary>
public static partial class CleanupRules
{
    /// <summary>
    /// Why a category cannot run right now (not elevated, a restart pending, an install running), or null when it may.
    /// Checked during the scan and again just before cleaning.
    /// </summary>
    public static string? BlockedReason(CleanupCategory category, CleanupConditions conditions)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(conditions);
        var r = category.Requires;
        if (r.HasFlag(CleanupRequirement.Elevated) && !conditions.IsElevated)
        {
            return "Skipped: run PC Checker as administrator to clean this.";
        }

        if (r.HasFlag(CleanupRequirement.NoPendingReboot) && conditions.PendingReboot)
        {
            return "Skipped: Windows is waiting for a restart after an update or driver install. Restart, then scan again.";
        }

        if (r.HasFlag(CleanupRequirement.NoPendingFileRenames) && conditions.PendingFileRenames)
        {
            return "Skipped: an install finishes at the next restart. Restart, then scan again.";
        }

        if (r.HasFlag(CleanupRequirement.NoInstallerRunning) && conditions.InstallerRunning)
        {
            return "Skipped: Windows Installer is installing something. Wait for it to finish, then scan again.";
        }

        if (r.HasFlag(CleanupRequirement.NoUpdateInstalling) && conditions.UpdateInstalling)
        {
            return "Skipped: Windows Update is installing. Wait for it to finish, then scan again.";
        }

        return null;
    }

    /// <summary>A warning when a feature update is younger than <paramref name="days"/> days (its rollback data still matters).</summary>
    public static string? RecentUpgradeWarning(DateTime? lastFeatureUpdate, DateTime now, int days = 10)
    {
        if (lastFeatureUpdate is not { } installed || installed > now)
        {
            return null;
        }

        var age = now - installed;
        if (age.TotalDays >= days)
        {
            return null;
        }

        int whole = (int)age.TotalDays;
        var ago = whole switch { 0 => "today", 1 => "yesterday", _ => $"{whole} days ago" };
        return $"Windows was upgraded {ago}. For {days} days after an upgrade these files are needed to go back to the previous version.";
    }

    /// <summary>The "skip files newer than" limit for a category: the global setting or its minimum age, whichever is longer.</summary>
    public static int EffectiveSkipMinutes(CleanupCategory category, int settingMinutes)
    {
        ArgumentNullException.ThrowIfNull(category);
        long minAge = (long)Math.Max(0, category.MinAgeDays) * 24 * 60;
        return (int)Math.Min(int.MaxValue, Math.Max(Math.Max(0, settingMinutes), minAge));
    }

    /// <summary>
    /// True when a file is one the category may delete: inside one of its resolved folders, directly in it unless that
    /// folder is scanned recursively, and with a name matching that folder's patterns. Checked again at delete time so a
    /// shared folder such as %LOCALAPPDATA% (for IconCache.db) can never give up other files.
    /// </summary>
    public static bool MatchesEntry(string filePath, IEnumerable<ResolvedRoot> entries)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var path = Normalize(filePath);
        var name = CleanupSafety.FileNameOf(path);
        int cut = path.LastIndexOf('\\');
        var parent = cut > 0 ? path[..cut] : string.Empty;
        foreach (var entry in entries ?? [])
        {
            var root = Normalize(entry.Path);
            if (!CleanupSafety.IsPathAllowed(path, [root]))
            {
                continue;
            }

            if (!entry.Recurse && !parent.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Patterns.Any(p => p is "*" or "*.*") || CleanupSafety.MatchesAny(name, entry.Patterns))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How many skipped files were File Explorer's icon or thumbnail caches held open (restarting Explorer helps).</summary>
    public static int ExplorerHeldFiles(IEnumerable<string> skipped) =>
        (skipped ?? []).Count(s => s is not null
            && s.EndsWith("(in use)", StringComparison.OrdinalIgnoreCase)
            && (s.Contains("thumbcache_", StringComparison.OrdinalIgnoreCase) || s.Contains("iconcache", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Whether a category may run in a scheduled or tray cleanup (before the user's own selection is applied).</summary>
    public static bool IsSchedulable(CleanupCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (category.NeverScheduled || category.Kind == CleanupKind.ReportOnly || IsRecycleBin(category.Id))
        {
            return false;
        }

        // Unattended runs only ever take Safe categories; a command category must itself be Safe.
        return category.Risk == Optimization.RiskLevel.Safe;
    }
}
