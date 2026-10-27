using System.Globalization;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Health;

namespace PcChecker.Core.Scheduling;

/// <summary>
/// Turns a job's raw output into a <see cref="ScheduledJobResult"/> with a short title and summary, decides whether it
/// needs the user's attention (health score drop, new critical item, driver updates, errors) and shapes the tray
/// notification text. Pure and unit-tested.
/// </summary>
public static class JobResultSummarizer
{
    /// <summary>A drop of this many points (or more) since the previous run is worth a notification.</summary>
    public const int ScoreDropThreshold = 5;

    /// <summary>On the first run, a score below this is worth a notification.</summary>
    public const int LowScore = 70;

    /// <summary>Windows truncates notification titles at 63 characters and messages at 255.</summary>
    public const int MaxTitleLength = 63;
    public const int MaxMessageLength = 255;

    private const int MaxDetails = 8;

    public static ScheduledJobResult Health(HealthReport report, ScheduledJobResult? previous, DateTimeOffset started, DateTimeOffset finished, string trigger)
    {
        ArgumentNullException.ThrowIfNull(report);
        int score = Math.Clamp(report.Score, 0, 100);
        var critical = report.Items.Where(i => i.Status == HealthStatus.Critical).Select(i => i.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var warnings = report.Items.Where(i => i.Status == HealthStatus.Warning).Select(i => i.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int? previousScore = previous is { Success: true } ? previous.HealthScore : null;
        var known = new HashSet<string>(previous?.CriticalItems ?? [], StringComparer.OrdinalIgnoreCase);
        var newCritical = previous is null ? critical : critical.Where(c => !known.Contains(c)).ToList();
        int? delta = previousScore is { } p ? score - p : null;

        bool dropped = delta is { } d && d <= -ScoreDropThreshold;
        bool attention = newCritical.Count > 0 || dropped || (previousScore is null && (score < LowScore || critical.Count > 0));

        string title = "Health score " + score.ToString(CultureInfo.InvariantCulture) + delta switch
        {
            null => string.Empty,
            0 => " (no change)",
            < 0 => $" (down {-delta.Value})",
            _ => $" (up {delta.Value})",
        };

        string summary;
        if (newCritical.Count > 0)
        {
            summary = (previous is null ? "Critical: " : "New critical problem: ") + newCritical[0]
                + (newCritical.Count > 1 ? $" and {newCritical.Count - 1} more" : string.Empty) + ". Open Health for details.";
        }
        else if (dropped)
        {
            summary = $"The score dropped from {previousScore} to {score}. Open Health to see what changed.";
        }
        else if (critical.Count + warnings.Count == 0)
        {
            summary = "Everything checked out. Nothing needs your attention.";
        }
        else
        {
            summary = Plural(critical.Count + warnings.Count, "item needs", "items need") + " a look"
                + (previous is null ? "." : ", none of them new.");
        }

        return new ScheduledJobResult(ScheduledJobKind.Health, started, finished, true, attention, title, summary,
            critical.Concat(warnings).Take(MaxDetails).ToList(), trigger)
        {
            HealthScore = score,
            CriticalItems = critical,
        };
    }

    public static ScheduledJobResult Cleanup(CleanupRunResult run, int categoryCount, DateTimeOffset started, DateTimeOffset finished, string trigger)
    {
        ArgumentNullException.ThrowIfNull(run);
        var freed = ByteSize.Format(run.FreedBytes);
        string title = run.DryRun
            ? $"Cleanup preview: {freed} could be freed"
            : run.FreedBytes > 0 ? $"Cleanup freed {freed}" : "Cleanup: nothing to remove";
        var errors = run.Errors.Where(e => !CleanupRules.IsNote(e)).ToList();
        var details = errors.Take(MaxDetails / 2)
            .Concat(run.Skipped.Select(CleanupRules.NoteText).Take(MaxDetails / 2))
            .ToList();
        string summary = CleanupRules.Summarize(run) + " " + Plural(categoryCount, "Safe category", "Safe categories") + " checked.";
        return new ScheduledJobResult(ScheduledJobKind.Cleanup, started, finished, true, errors.Count > 0, title, summary, details, trigger)
        {
            DryRun = run.DryRun,
            FreedBytes = run.FreedBytes,
        };
    }

    public static ScheduledJobResult Drivers(DriverUpdateCheckResult check, DateTimeOffset started, DateTimeOffset finished, string trigger)
    {
        ArgumentNullException.ThrowIfNull(check);
        var pending = check.Updates.Where(u => !u.IsInstalled).ToList();
        string title = pending.Count == 0 ? "Drivers are up to date" : Plural(pending.Count, "driver update", "driver updates") + " available";
        string summary = pending.Count == 0
            ? (check.Warnings.Count > 0 ? "No updates found, but the check was incomplete: " + check.Warnings[0] : "Windows Update has no newer drivers for this PC.")
            : "Open Drivers to review and install them. Nothing was installed automatically.";
        var details = pending.Select(u => u.Title).Take(MaxDetails).Concat(check.Warnings.Take(2)).ToList();
        return new ScheduledJobResult(ScheduledJobKind.Drivers, started, finished, true, pending.Count > 0, title, summary, details, trigger)
        {
            UpdatesFound = pending.Count,
        };
    }

    public static ScheduledJobResult Report(string path, int removedOldReports, IReadOnlyList<string> missingSections, DateTimeOffset started, DateTimeOffset finished, string trigger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        missingSections ??= [];
        string summary = "Saved to " + path + "."
            + (removedOldReports > 0 ? " Removed " + Plural(removedOldReports, "older report", "older reports") + "." : string.Empty)
            + (missingSections.Count > 0 ? " Left out: " + string.Join(", ", missingSections) + "." : string.Empty);
        return new ScheduledJobResult(ScheduledJobKind.Report, started, finished, true, false, "Report saved", summary, missingSections.ToList(), trigger)
        {
            OutputPath = path,
        };
    }

    public static ScheduledJobResult Failure(ScheduledJobKind job, string message, bool timedOut, DateTimeOffset started, DateTimeOffset finished, string trigger)
    {
        string name = ScheduledJobIds.DisplayName(job);
        string title = timedOut ? name + " took too long" : name + " failed";
        string summary = timedOut
            ? "It was stopped after " + (int)ScheduledJobExecutorLimits.Timeout.TotalMinutes + " minutes. It will try again at the next scheduled time."
            : string.IsNullOrWhiteSpace(message) ? "Something went wrong. Details are in the log." : message.Trim();
        return new ScheduledJobResult(job, started, finished, false, true, title, summary, [], trigger);
    }

    /// <summary>True when a finished run should show a notification.</summary>
    public static bool ShouldNotify(ScheduledJobResult result, bool onlyWhenAttention)
    {
        ArgumentNullException.ThrowIfNull(result);
        return !onlyWhenAttention || !result.Success || result.NeedsAttention;
    }

    /// <summary>Notification title and message, trimmed to what Windows displays.</summary>
    public static (string Title, string Message) NotificationText(ScheduledJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return (Truncate(result.Title, MaxTitleLength), Truncate(result.Summary, MaxMessageLength));
    }

    /// <summary>"1 item" / "3 items".</summary>
    public static string Plural(int count, string singular, string plural) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural);

    public static string Truncate(string? text, int max)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }
}

/// <summary>Limits shared by the job runner and its messages.</summary>
public static class ScheduledJobExecutorLimits
{
    /// <summary>A job that runs longer than this is cancelled.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
}
