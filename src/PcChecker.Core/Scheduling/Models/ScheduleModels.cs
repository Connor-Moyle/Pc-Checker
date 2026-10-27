namespace PcChecker.Core.Scheduling;

/// <summary>The maintenance jobs PC Checker can run on a schedule (or on demand from Settings and the tray).</summary>
public enum ScheduledJobKind
{
    /// <summary>Runs the health check and reports when the score dropped or a critical item appeared.</summary>
    Health,

    /// <summary>Scans and cleans the Safe junk-file categories only.</summary>
    Cleanup,

    /// <summary>Checks Windows Update for newer drivers; never installs anything.</summary>
    Drivers,

    /// <summary>Writes a full HTML report to Documents\PC Checker Reports and keeps the newest few.</summary>
    Report,
}

/// <summary>How often a scheduled job runs.</summary>
public enum ScheduleFrequency
{
    Daily,
    Weekly,
}

/// <summary>One job's schedule as the user set it in Settings → Schedules (persisted in settings.json).</summary>
public sealed class JobSchedule
{
    public ScheduledJobKind Job { get; set; }

    /// <summary>True when the Windows scheduled task should exist.</summary>
    public bool Enabled { get; set; }

    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Weekly;

    /// <summary>Day of the week for <see cref="ScheduleFrequency.Weekly"/> schedules.</summary>
    public DayOfWeek Day { get; set; } = DayOfWeek.Sunday;

    /// <summary>Start time in 24-hour "HH:mm" form (local time).</summary>
    public string Time { get; set; } = "10:00";

    /// <summary>Cleanup only: count what would be removed without deleting anything.</summary>
    public bool DryRun { get; set; }

    /// <summary>Report only: how many scheduled reports to keep in the reports folder.</summary>
    public int KeepReports { get; set; } = 10;

    public JobSchedule Clone() => (JobSchedule)MemberwiseClone();
}

/// <summary>Settings → Schedules (persisted as <c>AppSettings.Schedules</c>).</summary>
public sealed class SchedulesSettings
{
    /// <summary>One entry per <see cref="ScheduledJobKind"/>; <see cref="ScheduleRules.Normalize"/> repairs gaps and duplicates.</summary>
    public List<JobSchedule> Jobs { get; set; } = ScheduleRules.DefaultJobs();

    /// <summary>Only show a notification when a job found something that needs attention (or failed).</summary>
    public bool NotifyOnlyWhenAttention { get; set; } = true;

    /// <summary>Returns the schedule for <paramref name="job"/>, adding the default one when it is missing.</summary>
    public JobSchedule For(ScheduledJobKind job)
    {
        var existing = Jobs.FirstOrDefault(j => j.Job == job);
        if (existing is not null)
        {
            return existing;
        }

        var created = ScheduleRules.DefaultJobs().First(j => j.Job == job);
        Jobs.Add(created);
        return created;
    }
}

/// <summary>Settings → Tray (persisted as <c>AppSettings.Tray</c>). "Minimize to tray" stays in <c>GeneralSettings</c>.</summary>
public sealed class TraySettings
{
    /// <summary>Show the PC Checker icon in the notification area while the app runs.</summary>
    public bool ShowIcon { get; set; } = true;

    /// <summary>The window's close button hides PC Checker to the tray instead of exiting.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Start PC Checker when you sign in to Windows (a logon task with highest privileges, so no UAC prompt).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>When started with Windows, start hidden with only the tray icon.</summary>
    public bool StartInTray { get; set; } = true;

    /// <summary>Draw the hottest temperature as the tray icon instead of the logo.</summary>
    public bool ShowTemperatureInIcon { get; set; }

    /// <summary>Show a tray notification for temperature alerts while the window is hidden or minimized.</summary>
    public bool NotifyTemperatureAlerts { get; set; } = true;
}

/// <summary>The outcome of one job run, stored as <c>%LOCALAPPDATA%\PcChecker\schedules\&lt;job&gt;-last.json</c>.</summary>
public sealed record ScheduledJobResult(
    ScheduledJobKind Job,
    DateTimeOffset Started,
    DateTimeOffset Finished,
    bool Success,
    bool NeedsAttention,
    string Title,
    string Summary,
    IReadOnlyList<string> Details,
    string Trigger)
{
    /// <summary>Health job: the score this run measured.</summary>
    public int? HealthScore { get; init; }

    /// <summary>Health job: titles of items rated Critical (compared with the previous run to spot new problems).</summary>
    public IReadOnlyList<string> CriticalItems { get; init; } = [];

    /// <summary>Report job: the file that was written. Cleanup job: null.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Cleanup job: true when nothing was deleted (dry run).</summary>
    public bool DryRun { get; init; }

    /// <summary>Cleanup job: bytes freed (or that would be freed in a dry run).</summary>
    public long? FreedBytes { get; init; }

    /// <summary>Drivers job: number of driver updates Windows Update offered.</summary>
    public int? UpdatesFound { get; init; }

    public TimeSpan Duration => Finished - Started;
}

/// <summary>What Task Scheduler reports about one of PC Checker's tasks (<c>schtasks /Query /V /FO CSV</c>).</summary>
public sealed record ScheduledTaskStatus(
    string TaskName,
    bool Exists,
    string? Status,
    DateTime? NextRun,
    DateTime? LastRun,
    long? LastResult,
    string? TaskToRun,
    string? NextRunText,
    string? LastRunText)
{
    public static ScheduledTaskStatus Missing(string taskName) => new(taskName, false, null, null, null, null, null, null, null);
}
