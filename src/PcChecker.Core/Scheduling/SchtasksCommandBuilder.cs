using System.Globalization;
using System.Text;

namespace PcChecker.Core.Scheduling;

/// <summary>Arguments for one <c>schtasks.exe</c> call, plus the same call as a single quoted command line for logs.</summary>
public sealed record SchtasksCommand(IReadOnlyList<string> Arguments)
{
    /// <summary>The arguments quoted the way Windows (and <c>ProcessStartInfo.ArgumentList</c>) passes them.</summary>
    public string CommandLine => SchtasksCommandBuilder.JoinArguments(Arguments);

    public override string ToString() => "schtasks " + CommandLine;
}

/// <summary>
/// Builds the <c>schtasks.exe</c> argument lists PC Checker uses: one task per job in the "PC Checker" folder running
/// <c>"&lt;exe&gt;" --scheduled &lt;job&gt;</c> daily or weekly with highest privileges, the logon task for "Start with
/// Windows", and the matching query and delete calls. Pure, so the quoting rules are unit-tested.
/// </summary>
public static class SchtasksCommandBuilder
{
    /// <summary>Task Scheduler folder that holds every PC Checker task.</summary>
    public const string Folder = "PC Checker";

    /// <summary>The logon task used for "Start with Windows".</summary>
    public const string LogonTaskName = Folder + "\\Start";

    /// <summary>schtasks rejects a /TR value longer than this.</summary>
    public const int MaxTaskRunLength = 261;

    /// <summary>Seconds Windows waits after sign-in before starting PC Checker (lets the desktop settle).</summary>
    public const string LogonDelay = "0000:30";

    /// <summary><c>/Create</c> for a job: <c>/SC DAILY|WEEKLY [/D MON] /ST HH:mm /RL HIGHEST /F</c>.</summary>
    public static SchtasksCommand Create(JobSchedule schedule, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ThrowIfInvalid(ValidateExecutable(executablePath));
        if (!ScheduleRules.TryParseTime(schedule.Time, out var time))
        {
            throw new ArgumentException($"\"{schedule.Time}\" is not a valid time. Use 24-hour HH:mm, for example 09:30.", nameof(schedule));
        }

        var run = TaskRun(executablePath, "--scheduled " + ScheduledJobIds.Id(schedule.Job));
        ThrowIfInvalid(ValidateTaskRun(run));

        var args = new List<string> { "/Create", "/TN", ScheduledJobIds.TaskName(schedule.Job), "/TR", run };
        if (schedule.Frequency == ScheduleFrequency.Weekly)
        {
            args.AddRange(["/SC", "WEEKLY", "/D", DayCode(schedule.Day)]);
        }
        else
        {
            args.AddRange(["/SC", "DAILY"]);
        }

        args.AddRange(["/ST", ScheduleRules.FormatTime(time), "/RL", "HIGHEST", "/F"]);
        return new SchtasksCommand(args);
    }

    /// <summary>
    /// <c>/Create</c> for "Start with Windows": an ONLOGON task with highest privileges, so the elevated app starts
    /// without a UAC prompt. <paramref name="startInTray"/> adds <c>--tray</c>.
    /// </summary>
    public static SchtasksCommand CreateLogon(string executablePath, bool startInTray)
    {
        ThrowIfInvalid(ValidateExecutable(executablePath));
        var run = TaskRun(executablePath, startInTray ? "--tray" : null);
        ThrowIfInvalid(ValidateTaskRun(run));
        return new SchtasksCommand(["/Create", "/TN", LogonTaskName, "/TR", run, "/SC", "ONLOGON", "/DELAY", LogonDelay, "/RL", "HIGHEST", "/F"]);
    }

    /// <summary><c>/Delete /TN name /F</c> (no confirmation prompt).</summary>
    public static SchtasksCommand Delete(string taskName) => new(["/Delete", "/TN", RequireName(taskName), "/F"]);

    /// <summary><c>/Query /TN name /FO CSV /V /NH</c>: verbose CSV without a header, parsed by column position.</summary>
    public static SchtasksCommand Query(string taskName) => new(["/Query", "/TN", RequireName(taskName), "/FO", "CSV", "/V", "/NH"]);

    /// <summary>The /TR value: the quoted executable followed by its arguments.</summary>
    public static string TaskRun(string executablePath, string? arguments)
    {
        var run = "\"" + executablePath.Trim() + "\"";
        return string.IsNullOrWhiteSpace(arguments) ? run : run + " " + arguments.Trim();
    }

    /// <summary>Three-letter day code schtasks expects after <c>/D</c>.</summary>
    public static string DayCode(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "MON",
        DayOfWeek.Tuesday => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday => "THU",
        DayOfWeek.Friday => "FRI",
        DayOfWeek.Saturday => "SAT",
        _ => "SUN",
    };

    /// <summary>Returns why the path cannot be scheduled, or null when it is fine.</summary>
    public static string? ValidateExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return "The location of PcChecker.exe is unknown, so no task can be created.";
        }

        var path = executablePath.Trim();
        if (path.Contains('"'))
        {
            return "The program path contains a quote character, which Task Scheduler cannot run.";
        }

        bool rooted = (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
                      || path.StartsWith(@"\\", StringComparison.Ordinal)
                      || path.StartsWith('/');
        if (!rooted)
        {
            return $"\"{path}\" is not a full path, so Task Scheduler could not find it.";
        }

        return null;
    }

    /// <summary>Returns why a /TR value is rejected (too long), or null.</summary>
    public static string? ValidateTaskRun(string taskRun) =>
        taskRun.Length > MaxTaskRunLength
            ? string.Format(CultureInfo.InvariantCulture,
                "The program path is too long for Task Scheduler ({0} characters; the limit is {1}). Move PcChecker.exe to a shorter folder such as C:\\Tools.",
                taskRun.Length, MaxTaskRunLength)
            : null;

    /// <summary>Joins arguments with the standard Windows quoting rules (the inverse of CommandLineToArgvW).</summary>
    public static string JoinArguments(IEnumerable<string> arguments)
    {
        var sb = new StringBuilder();
        foreach (var arg in arguments)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            AppendArgument(sb, arg ?? string.Empty);
        }

        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                sb.Append('\\', backslashes);
            }

            backslashes = 0;
            sb.Append(c);
        }

        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    private static string RequireName(string taskName) =>
        string.IsNullOrWhiteSpace(taskName) ? throw new ArgumentException("A task name is required.", nameof(taskName)) : taskName.Trim();

    private static void ThrowIfInvalid(string? error)
    {
        if (error is not null)
        {
            throw new ArgumentException(error);
        }
    }
}
