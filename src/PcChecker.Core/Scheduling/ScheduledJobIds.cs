namespace PcChecker.Core.Scheduling;

/// <summary>Command-line ids, Task Scheduler names and friendly text for each <see cref="ScheduledJobKind"/>.</summary>
public static class ScheduledJobIds
{
    /// <summary>Every job in display order.</summary>
    public static IReadOnlyList<ScheduledJobKind> All { get; } = Enum.GetValues<ScheduledJobKind>();

    /// <summary>The id used after <c>--scheduled</c>: "health", "cleanup", "drivers" or "report".</summary>
    public static string Id(ScheduledJobKind job) => job switch
    {
        ScheduledJobKind.Health => "health",
        ScheduledJobKind.Cleanup => "cleanup",
        ScheduledJobKind.Drivers => "drivers",
        ScheduledJobKind.Report => "report",
        _ => job.ToString().ToLowerInvariant(),
    };

    /// <summary>Parses a job id case-insensitively. Accepts a few friendly aliases ("clean", "driver", "reports").</summary>
    public static bool TryParse(string? text, out ScheduledJobKind job)
    {
        switch (text?.Trim().Trim('"').ToLowerInvariant())
        {
            case "health":
            case "healthcheck":
            case "health-check":
                job = ScheduledJobKind.Health;
                return true;
            case "cleanup":
            case "clean":
            case "clean-up":
                job = ScheduledJobKind.Cleanup;
                return true;
            case "drivers":
            case "driver":
                job = ScheduledJobKind.Drivers;
                return true;
            case "report":
            case "reports":
                job = ScheduledJobKind.Report;
                return true;
            default:
                job = default;
                return false;
        }
    }

    /// <summary>The task's name inside the "PC Checker" Task Scheduler folder, e.g. <c>PC Checker\Health</c>.</summary>
    public static string TaskName(ScheduledJobKind job) => SchtasksCommandBuilder.Folder + "\\" + job switch
    {
        ScheduledJobKind.Health => "Health",
        ScheduledJobKind.Cleanup => "Cleanup",
        ScheduledJobKind.Drivers => "Drivers",
        ScheduledJobKind.Report => "Report",
        _ => job.ToString(),
    };

    public static string DisplayName(ScheduledJobKind job) => job switch
    {
        ScheduledJobKind.Health => "Health check",
        ScheduledJobKind.Cleanup => "Junk file cleanup",
        ScheduledJobKind.Drivers => "Driver update check",
        ScheduledJobKind.Report => "PC report",
        _ => job.ToString(),
    };

    public static string Description(ScheduledJobKind job) => job switch
    {
        ScheduledJobKind.Health => "Checks drives, devices, crashes and event logs, and tells you when the health score drops or a new critical problem appears.",
        ScheduledJobKind.Cleanup => "Removes temporary files from the Safe categories only (temp folders, caches, update leftovers). Files changed in the last hour and the Recycle Bin are never touched.",
        ScheduledJobKind.Drivers => "Asks Windows Update whether newer drivers exist and tells you. Nothing is downloaded or installed.",
        ScheduledJobKind.Report => "Saves a full HTML report (hardware, health, performance, drivers) to Documents\\PC Checker Reports and keeps the newest ones.",
        _ => string.Empty,
    };
}
