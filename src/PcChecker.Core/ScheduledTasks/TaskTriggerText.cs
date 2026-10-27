using System.Globalization;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>Task triggers in plain words: "At log on", "Daily at 03:00", "When idle". Pure; times use the 24-hour clock.</summary>
public static class TaskTriggerText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>All triggers in one line (the first three, then "+N more").</summary>
    public static string Summary(IReadOnlyList<TaskTrigger> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        if (triggers.Count == 0)
        {
            return "Only when started by hand or by a program";
        }

        var ordered = triggers.OrderByDescending(t => t.Enabled).ToList();
        var parts = ordered.Take(3).Select(Describe).ToList();
        if (ordered.Count > 3)
        {
            parts.Add($"+{ordered.Count - 3} more");
        }

        return string.Join("; ", parts);
    }

    /// <summary>One trigger, with its delay, repetition and "(off)" when the trigger itself is turned off.</summary>
    public static string Describe(TaskTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var text = Base(trigger);
        if (trigger.Delay is { } delay && delay > TimeSpan.Zero)
        {
            text += $", {Words(delay)} later";
        }

        if (trigger.RepeatEvery is { } every && every > TimeSpan.Zero)
        {
            text += ", then " + Every(every);
            if (trigger.RepeatFor is { } span && span > TimeSpan.Zero)
            {
                text += " for " + Words(span);
            }
        }

        return trigger.Enabled ? text : text + " (off)";
    }

    private static string Base(TaskTrigger t) => t.Kind switch
    {
        TaskTriggerKind.Once => t.Start is { } once ? $"Once on {once.ToString("d MMM yyyy", Invariant)} at {Clock(once)}" : "Once",
        TaskTriggerKind.Daily => (t.Interval <= 1 ? "Daily" : $"Every {t.Interval} days") + At(t),
        TaskTriggerKind.Weekly => Weekly(t),
        TaskTriggerKind.Monthly => Monthly(t),
        TaskTriggerKind.MonthlyDayOfWeek => MonthlyDayOfWeek(t),
        TaskTriggerKind.Boot => "At startup",
        TaskTriggerKind.Logon => string.IsNullOrWhiteSpace(t.UserId) ? "At log on" : $"At log on of {ShortUser(t.UserId)}",
        TaskTriggerKind.Idle => "When idle",
        TaskTriggerKind.Event => "When Windows logs a certain event",
        TaskTriggerKind.Registration => "When the task is created or changed",
        TaskTriggerKind.SessionStateChange => SessionChange(t.StateChange),
        _ => "When Windows signals a change",
    };

    private static string Weekly(TaskTrigger t)
    {
        if (t.DaysOfWeek.Count == 7 && t.Interval <= 1)
        {
            return "Daily" + At(t);
        }

        var days = t.DaysOfWeek.Count == 0 ? string.Empty : " on " + string.Join(", ", t.DaysOfWeek.Select(d => Invariant.DateTimeFormat.GetAbbreviatedDayName(d)));
        return (t.Interval <= 1 ? "Weekly" : $"Every {t.Interval} weeks") + days + At(t);
    }

    private static string Monthly(TaskTrigger t)
    {
        var days = t.DaysOfMonth.Select(d => d.ToString(Invariant)).ToList();
        string on = days.Count switch
        {
            0 when t.LastDayOfMonth => " on the last day",
            0 => string.Empty,
            1 => " on day " + days[0],
            _ => " on days " + string.Join(", ", days),
        };
        if (days.Count > 0 && t.LastDayOfMonth)
        {
            on += " and the last day";
        }

        return "Monthly" + on + InMonths(t) + At(t);
    }

    private static string MonthlyDayOfWeek(TaskTrigger t)
    {
        var weeks = t.Weeks.Select(w => w switch
        {
            "1" => "first",
            "2" => "second",
            "3" => "third",
            "4" => "fourth",
            _ when string.Equals(w, "Last", StringComparison.OrdinalIgnoreCase) => "last",
            _ => w,
        }).ToList();
        var days = string.Join(", ", t.DaysOfWeek.Select(d => Invariant.DateTimeFormat.GetDayName(d)));
        var which = weeks.Count == 0 ? string.Empty : string.Join(" and ", weeks) + " ";
        return "Monthly" + (days.Length > 0 ? $" on the {which}{days}" : string.Empty) + InMonths(t) + At(t);
    }

    private static string InMonths(TaskTrigger t) =>
        t.Months.Count is 0 or 12 ? string.Empty : " in " + string.Join(", ", t.Months.Select(m => Invariant.DateTimeFormat.GetAbbreviatedMonthName(m)));

    private static string SessionChange(string? change) => change switch
    {
        "ConsoleConnect" => "When someone connects to the PC",
        "ConsoleDisconnect" => "When someone disconnects from the PC",
        "RemoteConnect" => "When a remote session connects",
        "RemoteDisconnect" => "When a remote session disconnects",
        "SessionLock" => "When the PC is locked",
        "SessionUnlock" => "When the PC is unlocked",
        _ => "When a session changes",
    };

    private static string At(TaskTrigger t) => t.Start is { } start ? " at " + Clock(start) : string.Empty;

    private static string Clock(DateTime time) => time.ToString("HH:mm", Invariant);

    /// <summary>"DOMAIN\alex" → "alex"; SIDs and plain names stay as they are.</summary>
    public static string ShortUser(string? user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            return string.Empty;
        }

        int slash = user.LastIndexOf('\\');
        return slash >= 0 && slash < user.Length - 1 ? user[(slash + 1)..] : user.Trim();
    }

    /// <summary>"every hour", "every 15 minutes", "every 2 days".</summary>
    public static string Every(TimeSpan span)
    {
        var words = Words(span);
        return words.StartsWith("1 ", StringComparison.Ordinal) && words.IndexOf(' ', 2) < 0 ? "every " + words[2..] : "every " + words;
    }

    /// <summary>"1 hour", "30 minutes", "1 day 12 hours"; at most two units.</summary>
    public static string Words(TimeSpan span)
    {
        var parts = new List<string>();
        void Add(int value, string unit)
        {
            if (value > 0 && parts.Count < 2)
            {
                parts.Add(value == 1 ? $"1 {unit}" : $"{value.ToString(Invariant)} {unit}s");
            }
        }

        Add(span.Days, "day");
        Add(span.Hours, "hour");
        Add(span.Minutes, "minute");
        Add(span.Seconds, "second");
        return parts.Count == 0 ? "0 seconds" : string.Join(" ", parts);
    }
}
