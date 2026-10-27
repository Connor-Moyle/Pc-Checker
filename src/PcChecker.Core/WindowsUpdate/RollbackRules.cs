using System.Globalization;

namespace PcChecker.Core.WindowsUpdate;

/// <summary>
/// Decisions for "go back to the previous version of Windows": how many days are left from the DISM uninstall window
/// and the upgrade date, which longer windows can be chosen, and the DISM arguments. Pure.
/// </summary>
public static class RollbackRules
{
    /// <summary>The shortest window DISM accepts.</summary>
    public const int MinWindowDays = 2;

    /// <summary>The longest window DISM accepts.</summary>
    public const int MaxWindowDays = 60;

    /// <summary>What Windows sets after a feature update.</summary>
    public const int DefaultWindowDays = 10;

    /// <summary>Choices offered for a longer window.</summary>
    public static IReadOnlyList<int> ExtensionChoices { get; } = [14, 21, 30, 45, 60];

    /// <summary>
    /// Decides the state from what Windows reported. <paramref name="windowDays"/> is DISM's window (null when DISM
    /// said there is nothing to go back to or could not be read: <paramref name="dismSaysNotAvailable"/> tells which),
    /// <paramref name="upgradedOn"/> is when the current version was installed.
    /// </summary>
    public static RollbackWindow Evaluate(int? windowDays, bool dismSaysNotAvailable, DateTimeOffset? upgradedOn, bool windowsOldExists,
        DateTimeOffset now, IReadOnlyList<string>? warnings = null)
    {
        var notes = warnings ?? [];
        int? since = upgradedOn is { } on ? Math.Max(0, (int)Math.Floor((now - on).TotalDays)) : null;
        if (!windowsOldExists)
        {
            return new RollbackWindow(RollbackState.NotAvailable, windowDays, 0, upgradedOn, false,
                "There is no previous version of Windows on this PC to go back to (the Windows.old folder is not there).", notes)
            { DaysSinceUpgrade = since };
        }

        if (dismSaysNotAvailable)
        {
            return new RollbackWindow(RollbackState.NotAvailable, null, 0, upgradedOn, true,
                "Windows says going back is no longer possible. The Windows.old folder is left over and can be removed with Disk Cleanup.", notes)
            { DaysSinceUpgrade = since };
        }

        if (windowDays is not { } days)
        {
            return new RollbackWindow(RollbackState.Unknown, null, null, upgradedOn, true,
                "Windows did not say how long going back is possible. Settings → System → Recovery shows whether it still is.", notes)
            { DaysSinceUpgrade = since };
        }

        if (since is not { } elapsed)
        {
            return new RollbackWindow(RollbackState.Available, days, null, null, true,
                $"Going back is possible within {days} days of the upgrade.", notes);
        }

        int left = DaysLeft(days, elapsed);
        if (left <= 0)
        {
            return new RollbackWindow(RollbackState.Expired, days, 0, upgradedOn, true,
                "The time to go back has run out. Windows removes the previous version soon.", notes) { DaysSinceUpgrade = elapsed };
        }

        var text = left == 1 ? "You can go back for 1 more day." : $"You can go back for {left} more days.";
        return new RollbackWindow(RollbackState.Available, days, left, upgradedOn, true, text, notes) { DaysSinceUpgrade = elapsed };
    }

    /// <summary>Days left in a window of <paramref name="windowDays"/> after <paramref name="daysSinceUpgrade"/> whole days, never below 0.</summary>
    public static int DaysLeft(int windowDays, int daysSinceUpgrade) => Math.Max(0, windowDays - Math.Max(0, daysSinceUpgrade));

    /// <summary>
    /// Why a new window of <paramref name="requestedDays"/> cannot be set, or null when it can. It must be 2–60 days and
    /// longer than the time since the upgrade, or it would end the option at once.
    /// </summary>
    public static string? ValidateExtension(int requestedDays, int? daysSinceUpgrade)
    {
        if (requestedDays is < MinWindowDays or > MaxWindowDays)
        {
            return $"Choose between {MinWindowDays} and {MaxWindowDays} days.";
        }

        if (daysSinceUpgrade is { } since && requestedDays <= since)
        {
            return $"The upgrade was {since} days ago, so a {requestedDays}-day window would end the option to go back now.";
        }

        return null;
    }

    /// <summary>The longer windows worth offering: more than the current window and more than the days already gone.</summary>
    public static IReadOnlyList<int> ExtensionOptions(int? currentWindowDays, int? daysSinceUpgrade) =>
        ExtensionChoices.Where(d => d > (currentWindowDays ?? 0) && ValidateExtension(d, daysSinceUpgrade) is null).ToList();

    /// <summary>DISM arguments that read the window.</summary>
    public static IReadOnlyList<string> GetWindowArguments() => ["/Online", "/Get-OSUninstallWindow", "/English"];

    /// <summary>DISM arguments that set the window to <paramref name="days"/>.</summary>
    public static IReadOnlyList<string> SetWindowArguments(int days)
    {
        if (days is < MinWindowDays or > MaxWindowDays)
        {
            throw new ArgumentOutOfRangeException(nameof(days), days, $"The window must be {MinWindowDays}–{MaxWindowDays} days.");
        }

        return ["/Online", "/Set-OSUninstallWindow", "/Value:" + days.ToString(CultureInfo.InvariantCulture), "/English"];
    }

    /// <summary>
    /// DISM arguments that start going back. <c>/Quiet</c> stops DISM from asking at a prompt nobody sees, and
    /// <c>/NoRestart</c> leaves the restart to PC Checker's own countdown.
    /// </summary>
    public static IReadOnlyList<string> GoBackArguments() => ["/Online", "/Initiate-OSUninstall", "/Quiet", "/NoRestart", "/English"];

    /// <summary>The upgrade time from <c>InstallDate</c> (seconds since 1970, which Windows resets at every feature update).</summary>
    public static DateTimeOffset? FromInstallDate(long? unixSeconds) =>
        unixSeconds is { } s && s > 946_684_800 && s < 32_503_680_000 ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    /// <summary>
    /// Whether DISM's exit code from <c>/Get-OSUninstallWindow</c> means "nothing to go back to" (rather than an error
    /// reading it).
    /// </summary>
    public static bool MeansNotAvailable(int exitCode) => UpdateToolExitCodes.Map(exitCode).Outcome == UpdateToolOutcome.NotInstalled;
}
