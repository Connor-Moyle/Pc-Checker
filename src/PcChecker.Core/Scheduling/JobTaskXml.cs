using System.Globalization;
using System.Xml.Linq;

namespace PcChecker.Core.Scheduling;

/// <summary>
/// Task Scheduler XML for one scheduled job: a daily or weekly calendar trigger at the chosen local time that runs
/// <c>"&lt;exe&gt;" --scheduled &lt;job&gt;</c> with highest privileges. Registered from XML rather than the plain
/// <c>/SC DAILY|WEEKLY</c> switches so the job also runs on battery power (the switch defaults skip it on battery and
/// stop it when the charger is unplugged) and has a time limit. Pure, so it is unit-tested.
/// </summary>
public static class JobTaskXml
{
    /// <summary>Task Scheduler's hard stop. The headless run gives up after 10 minutes on its own; this is the backstop.</summary>
    public const string ExecutionTimeLimit = "PT1H";

    /// <summary>Below-normal priority (Task Scheduler's default) so maintenance stays out of the way of games.</summary>
    public const int Priority = 7;

    /// <summary>
    /// Builds the task definition. <paramref name="startDate"/> is the first day the trigger is valid (today); the
    /// start boundary has no time zone, so the job keeps the same wall-clock time across daylight-saving changes.
    /// </summary>
    public static string Build(JobSchedule schedule, string executablePath, string? userId, DateOnly startDate)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        TaskXml.ThrowIfInvalidExecutable(executablePath);
        if (!ScheduleRules.TryParseTime(schedule.Time, out var time))
        {
            throw new ArgumentException($"\"{schedule.Time}\" is not a valid time. Use 24-hour HH:mm, for example 09:30.", nameof(schedule));
        }

        var start = startDate.ToDateTime(time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        XElement recurrence = schedule.Frequency == ScheduleFrequency.Weekly
            ? new XElement(TaskXml.Ns + "ScheduleByWeek",
                new XElement(TaskXml.Ns + "DaysOfWeek", new XElement(TaskXml.Ns + schedule.Day.ToString())),
                new XElement(TaskXml.Ns + "WeeksInterval", "1"))
            : new XElement(TaskXml.Ns + "ScheduleByDay", new XElement(TaskXml.Ns + "DaysInterval", "1"));
        var trigger = new XElement(TaskXml.Ns + "CalendarTrigger",
            new XElement(TaskXml.Ns + "StartBoundary", start),
            new XElement(TaskXml.Ns + "Enabled", "true"),
            recurrence);

        var description = $"Runs the PC Checker job \"{ScheduledJobIds.DisplayName(schedule.Job)}\" {ScheduleRules.DescribeInline(schedule)}.";
        return TaskXml.Document(description, trigger, TaskXml.Principal(TaskXml.NormalizeUser(userId)),
            TaskXml.Settings(ExecutionTimeLimit, Priority),
            TaskXml.Actions(executablePath, "--scheduled " + ScheduledJobIds.Id(schedule.Job)));
    }
}
