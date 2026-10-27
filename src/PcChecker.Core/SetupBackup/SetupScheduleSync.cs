using PcChecker.Core.Scheduling;

namespace PcChecker.Core.SetupBackup;

/// <summary>What has to happen in Task Scheduler so it matches imported schedules.</summary>
public enum ScheduleSyncAction
{
    /// <summary>Create the job's task, or update it with the new time and options.</summary>
    CreateOrUpdate,

    /// <summary>Remove the job's task.</summary>
    Delete,

    /// <summary>Create or remove the "Start with Windows" logon task.</summary>
    SetStartWithWindows,
}

/// <summary>One Task Scheduler change. <see cref="Schedule"/> is set for jobs; the tray fields for the logon task.</summary>
public sealed record ScheduleSyncStep(ScheduleSyncAction Action, ScheduledJobKind? Job, JobSchedule? Schedule, bool StartWithWindows = false, bool StartInTray = false);

/// <summary>
/// Works out the Task Scheduler changes that make Windows match imported (or reset) schedules and tray options:
/// a job turned on (or changed while on) gets its task created or updated, a job turned off has its task removed, and
/// "Start with Windows" follows the tray setting. Schedules that are not valid are not created.
/// </summary>
public static class SetupScheduleSync
{
    public static IReadOnlyList<ScheduleSyncStep> Plan(SchedulesSettings before, TraySettings beforeTray, SchedulesSettings after, TraySettings afterTray)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(beforeTray);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(afterTray);
        var steps = new List<ScheduleSyncStep>();
        foreach (var job in ScheduledJobIds.All)
        {
            var old = Find(before, job);
            var now = Find(after, job);
            bool wasOn = old?.Enabled == true;
            if (now is { Enabled: true } && ScheduleRules.Validate(now) is null)
            {
                if (!wasOn || !SameTask(old!, now))
                {
                    steps.Add(new ScheduleSyncStep(ScheduleSyncAction.CreateOrUpdate, job, now));
                }
            }
            else if (wasOn)
            {
                steps.Add(new ScheduleSyncStep(ScheduleSyncAction.Delete, job, now));
            }
        }

        bool logonChanged = beforeTray.StartWithWindows != afterTray.StartWithWindows
            || (afterTray.StartWithWindows && beforeTray.StartInTray != afterTray.StartInTray);
        if (logonChanged)
        {
            steps.Add(new ScheduleSyncStep(ScheduleSyncAction.SetStartWithWindows, null, null, afterTray.StartWithWindows, afterTray.StartInTray));
        }

        return steps;
    }

    /// <summary>The Task Scheduler changes that importing the schedules part of <paramref name="document"/> would make.</summary>
    public static IReadOnlyList<ScheduleSyncStep> PlanImport(Settings.AppSettings current, SetupDocument document, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(document);
        var outcome = SetupApplier.Apply(current, document, [SetupSectionIds.Schedules], now);
        if (outcome.AppliedSections.Count == 0)
        {
            return [];
        }

        var before = SetupJson.Clone(current);
        return Plan(ScheduleRules.EnsureSettings(before), before.Tray, ScheduleRules.EnsureSettings(outcome.Settings), outcome.Settings.Tray);
    }

    /// <summary>One line per kind of change, for the confirmation before an import touches Task Scheduler.</summary>
    public static IReadOnlyList<string> Describe(IReadOnlyList<ScheduleSyncStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var lines = new List<string>();
        var created = steps.Where(s => s.Action == ScheduleSyncAction.CreateOrUpdate && s.Schedule is not null)
            .Select(s => $"{ScheduledJobIds.DisplayName(s.Job!.Value)} ({ScheduleRules.DescribeInline(s.Schedule!)})").ToList();
        var removed = steps.Where(s => s.Action == ScheduleSyncAction.Delete).Select(s => ScheduledJobIds.DisplayName(s.Job!.Value)).ToList();
        if (created.Count > 0)
        {
            lines.Add("Create or update: " + string.Join(", ", created));
        }

        if (removed.Count > 0)
        {
            lines.Add("Remove: " + string.Join(", ", removed));
        }

        if (steps.FirstOrDefault(s => s.Action == ScheduleSyncAction.SetStartWithWindows) is { } logon)
        {
            lines.Add(logon.StartWithWindows
                ? "Start with Windows: on" + (logon.StartInTray ? ", in the tray" : string.Empty)
                : "Start with Windows: off");
        }

        return lines;
    }

    /// <summary>True when both schedules produce the same scheduled task.</summary>
    public static bool SameTask(JobSchedule a, JobSchedule b) =>
        a.Job == b.Job && a.Frequency == b.Frequency && a.Day == b.Day
        && string.Equals(a.Time, b.Time, StringComparison.Ordinal) && a.DryRun == b.DryRun && a.KeepReports == b.KeepReports;

    private static JobSchedule? Find(SchedulesSettings settings, ScheduledJobKind job) =>
        settings.Jobs?.FirstOrDefault(j => j is not null && j.Job == job);
}
