namespace PcChecker.Core.Debloat;

/// <summary>
/// Pure rules for the restore point a debloat batch needs: whether Windows will refuse it, and the plain sentence the
/// page shows before asking whether to turn on System Protection, continue without a restore point, or cancel.
/// </summary>
public static class DebloatRestorePointRules
{
    /// <summary>Windows creates at most one restore point per this period unless the limit is lifted.</summary>
    public static readonly TimeSpan FrequencyWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// What will stop the restore point. <paramref name="protectionOn"/> null means unknown (tried anyway).
    /// <paramref name="newest"/> is the newest restore point's time (null when there is none or it is unknown).
    /// </summary>
    public static RestorePointProblem Assess(bool? protectionOn, DateTimeOffset? newest, DateTimeOffset now, bool limitLifted)
    {
        if (protectionOn == false)
        {
            return RestorePointProblem.ProtectionOff;
        }

        if (!limitLifted && newest is { } last && last > DateTimeOffset.MinValue && now - last < FrequencyWindow && now >= last)
        {
            return RestorePointProblem.FrequencyLimit;
        }

        return RestorePointProblem.None;
    }

    /// <summary>The explanation shown in the dialog.</summary>
    public static string Describe(RestorePointProblem problem, string systemDrive, string? reason = null) => problem switch
    {
        RestorePointProblem.ProtectionOff =>
            $"System Protection is off for {systemDrive}, so Windows cannot create the restore point this batch needs.",
        RestorePointProblem.FrequencyLimit =>
            "Windows already made a restore point in the last 24 hours and only allows one per day, so it will not make the restore point this batch needs.",
        RestorePointProblem.Failed =>
            $"Windows could not create the restore point this batch needs{(string.IsNullOrWhiteSpace(reason) ? "." : $": {reason.Trim().TrimEnd('.')}.")}",
        _ => string.Empty,
    };

    /// <summary>True when at least one tweak could run without a restore point (it can be reverted from the page).</summary>
    public static bool CanContinueWithout(int needRestorePoint, int total) => total > 0 && needRestorePoint < total;

    /// <summary>The full dialog text: the problem, what each choice does, and which tweaks would be left out.</summary>
    public static string DialogText(RestorePointProblem problem, string systemDrive, string? reason, int needRestorePoint, int total, bool canTurnOn)
    {
        var lines = new List<string> { Describe(problem, systemDrive, reason) };
        if (canTurnOn)
        {
            lines.Add($"Turn on System Protection: Windows starts keeping restore points for {systemDrive} (a few percent of the drive), then the batch runs with a restore point.");
        }

        int reversible = Math.Max(0, total - needRestorePoint);
        if (CanContinueWithout(needRestorePoint, total))
        {
            lines.Add(needRestorePoint > 0
                ? $"Continue without: the {reversible} tweak{(reversible == 1 ? string.Empty : "s")} that can be reverted from this page run; the {needRestorePoint} that cannot be undone here (app or OneDrive removal, Windows features) are skipped."
                : "Continue without: every selected tweak can be reverted from this page with the values saved before the change.");
        }
        else
        {
            lines.Add("None of the selected tweaks can be undone from this page (app or OneDrive removal, Windows features), so they only run with a restore point.");
        }

        lines.Add("Cancel: nothing changes.");
        return string.Join("\n\n", lines);
    }
}
