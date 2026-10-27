using System.Globalization;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Optimization;

namespace PcChecker.Core.Scheduling;

/// <summary>
/// Pure rules for schedules: defaults, time parsing, validation, the next run (for display), plain-English
/// descriptions, task path checks, which cleanup categories a scheduled run may touch and report retention.
/// </summary>
public static class ScheduleRules
{
    /// <summary>Category ids an unattended cleanup never touches even though they are rated Safe.</summary>
    public static IReadOnlyList<string> NeverCleanedUnattended { get; } = ["recycle.bin"];

    public const int MinKeepReports = 1;
    public const int MaxKeepReports = 100;

    /// <summary>All jobs off; sensible quiet-hour defaults when the user turns one on.</summary>
    public static List<JobSchedule> DefaultJobs() =>
    [
        new() { Job = ScheduledJobKind.Health, Frequency = ScheduleFrequency.Weekly, Day = DayOfWeek.Sunday, Time = "10:00" },
        new() { Job = ScheduledJobKind.Cleanup, Frequency = ScheduleFrequency.Weekly, Day = DayOfWeek.Sunday, Time = "10:30" },
        new() { Job = ScheduledJobKind.Drivers, Frequency = ScheduleFrequency.Weekly, Day = DayOfWeek.Wednesday, Time = "18:00" },
        new() { Job = ScheduledJobKind.Report, Frequency = ScheduleFrequency.Weekly, Day = DayOfWeek.Sunday, Time = "11:00", KeepReports = 10 },
    ];

    /// <summary>Parses "H:mm" or "HH:mm" (24-hour; "9.30" and "0930" are accepted too).</summary>
    public static bool TryParseTime(string? text, out TimeOnly time)
    {
        time = default;
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return false;
        }

        t = t.Replace('.', ':');
        if (!t.Contains(':') && t.Length == 4 && t.All(char.IsAsciiDigit))
        {
            t = t[..2] + ":" + t[2..];
        }

        var parts = t.Split(':');
        if (parts.Length != 2 || parts[0].Length is < 1 or > 2 || parts[1].Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int h)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int m)
            || h > 23 || m > 59)
        {
            return false;
        }

        time = new TimeOnly(h, m);
        return true;
    }

    /// <summary>"HH:mm" in 24-hour form, the format schtasks /ST expects.</summary>
    public static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Returns a user-facing problem with the schedule, or null when it can be saved.</summary>
    public static string? Validate(JobSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!TryParseTime(schedule.Time, out _))
        {
            return $"\"{schedule.Time}\" is not a valid time. Use 24-hour time such as 09:30 or 18:00.";
        }

        if (!Enum.IsDefined(schedule.Frequency))
        {
            return "Pick daily or weekly.";
        }

        if (!Enum.IsDefined(schedule.Day))
        {
            return "Pick a day of the week.";
        }

        if (schedule.Job == ScheduledJobKind.Report && schedule.KeepReports is < MinKeepReports or > MaxKeepReports)
        {
            return $"Keep between {MinKeepReports} and {MaxKeepReports} reports.";
        }

        return null;
    }

    /// <summary>The next local time the job runs after <paramref name="now"/>, or null when it is off or invalid.</summary>
    public static DateTime? NextRun(JobSchedule schedule, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!schedule.Enabled || !TryParseTime(schedule.Time, out var time))
        {
            return null;
        }

        var candidate = now.Date + time.ToTimeSpan();
        if (schedule.Frequency == ScheduleFrequency.Daily)
        {
            return candidate > now ? candidate : candidate.AddDays(1);
        }

        int days = ((int)schedule.Day - (int)now.DayOfWeek + 7) % 7;
        candidate = candidate.AddDays(days);
        return candidate > now ? candidate : candidate.AddDays(7);
    }

    /// <summary>"Every day at 09:30" or "Every Sunday at 10:00".</summary>
    public static string Describe(JobSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var time = TryParseTime(schedule.Time, out var t) ? FormatTime(t) : schedule.Time;
        return schedule.Frequency == ScheduleFrequency.Daily
            ? $"Every day at {time}"
            : $"Every {schedule.Day} at {time}";
    }

    /// <summary>
    /// Returns the normalized schedules of <paramref name="settings"/>, replacing a missing (null) section loaded from an
    /// older or hand-edited settings file. The tray section is repaired the same way.
    /// </summary>
    public static SchedulesSettings EnsureSettings(Settings.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Tray ??= new TraySettings();
        settings.Schedules ??= new SchedulesSettings();
        return Normalize(settings.Schedules);
    }

    /// <summary>"every day at 09:30" / "every Sunday at 10:00", for use inside a sentence.</summary>
    public static string DescribeInline(JobSchedule schedule)
    {
        var text = Describe(schedule);
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    /// <summary>
    /// Repairs settings loaded from disk: exactly one schedule per job (first one wins), valid times and report counts.
    /// Returns the same instance for chaining.
    /// </summary>
    public static SchedulesSettings Normalize(SchedulesSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var defaults = DefaultJobs();
        var result = new List<JobSchedule>();
        foreach (var job in ScheduledJobIds.All)
        {
            var schedule = settings.Jobs?.FirstOrDefault(j => j is not null && j.Job == job) ?? defaults.First(d => d.Job == job);
            if (!TryParseTime(schedule.Time, out var time))
            {
                schedule.Time = defaults.First(d => d.Job == job).Time;
            }
            else
            {
                schedule.Time = FormatTime(time);
            }

            if (!Enum.IsDefined(schedule.Frequency))
            {
                schedule.Frequency = ScheduleFrequency.Weekly;
            }

            if (!Enum.IsDefined(schedule.Day))
            {
                schedule.Day = DayOfWeek.Sunday;
            }

            schedule.KeepReports = Math.Clamp(schedule.KeepReports, MinKeepReports, MaxKeepReports);
            result.Add(schedule);
        }

        settings.Jobs = result;
        return settings;
    }

    /// <summary>
    /// Extracts the executable from a task's "Task To Run" text: <c>"C:\A B\PcChecker.exe" --scheduled health</c>
    /// → <c>C:\A B\PcChecker.exe</c>. Unquoted paths end at the first space.
    /// </summary>
    public static string? ExtractExecutable(string? taskToRun)
    {
        var t = taskToRun?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }

        if (t[0] == '"')
        {
            int end = t.IndexOf('"', 1);
            return end > 1 ? t[1..end] : null;
        }

        int space = t.IndexOf(' ');
        return space > 0 ? t[..space] : t;
    }

    /// <summary>True when an existing task runs a different PcChecker.exe than <paramref name="currentExecutable"/> (the file was moved).</summary>
    public static bool PointsElsewhere(ScheduledTaskStatus status, string? currentExecutable)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!status.Exists || string.IsNullOrWhiteSpace(currentExecutable))
        {
            return false;
        }

        var exe = ExtractExecutable(status.TaskToRun);
        return exe is not null && !SamePath(exe, currentExecutable);
    }

    /// <summary>Case-insensitive path comparison that ignores slash direction and surrounding quotes.</summary>
    public static bool SamePath(string a, string b)
    {
        static string N(string p) => p.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Categories a scheduled or tray cleanup may clean: Safe risk only, never the Recycle Bin, never report-only
    /// categories, limited to what the user enabled on the Cleanup page (or the catalogue defaults when they never chose).
    /// </summary>
    public static IReadOnlyList<string> UnattendedCleanupCategories(IEnumerable<CleanupCategory> categories, IEnumerable<string>? userEnabledIds)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var enabled = (userEnabledIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return categories
            .Where(c => c.Risk == RiskLevel.Safe)
            .Where(c => !NeverCleanedUnattended.Contains(c.Id, StringComparer.OrdinalIgnoreCase) && !CleanupRules.IsReportOnly(c.Id))
            .Where(c => enabled.Count == 0 ? c.EnabledByDefault : enabled.Contains(c.Id))
            .Select(c => c.Id)
            .ToList();
    }

    /// <summary>The oldest scheduled reports beyond the newest <paramref name="keep"/>.</summary>
    public static IReadOnlyList<string> ReportsToDelete(IEnumerable<(string Path, DateTime LastWrite)> reports, int keep)
    {
        ArgumentNullException.ThrowIfNull(reports);
        keep = Math.Clamp(keep, MinKeepReports, MaxKeepReports);
        return reports
            .OrderByDescending(r => r.LastWrite)
            .ThenByDescending(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Skip(keep)
            .Select(r => r.Path)
            .ToList();
    }

    /// <summary>Explains Task Scheduler's "Last Result" code in plain words.</summary>
    public static string DescribeLastResult(long? code)
    {
        if (code is null)
        {
            return "Unknown";
        }

        // schtasks prints the HRESULT as a signed decimal; compare as unsigned so both spellings match.
        uint value = unchecked((uint)code.Value);
        return value switch
        {
            0 => "Completed",
            1 => "Finished with problems (see the result below)",
            0x41300 => "Ready",
            0x41301 => "Running now",
            0x41302 => "Disabled",
            0x41303 => "Has not run yet",
            0x41306 => "Stopped by the user or Windows",
            0x8004131F => "Skipped: an earlier run was still going",
            0x800710E0 => "Skipped: Windows refused to start it (on battery or the PC was busy)",
            0x80070002 => "PcChecker.exe was not found at the scheduled location",
            _ => string.Format(CultureInfo.InvariantCulture, "Code 0x{0:X8}", value),
        };
    }
}
