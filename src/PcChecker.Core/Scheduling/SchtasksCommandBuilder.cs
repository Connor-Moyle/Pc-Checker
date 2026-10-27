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
/// Builds the <c>schtasks.exe</c> argument lists PC Checker uses: one task per job in the "PC Checker" folder and the
/// logon task for "Start with Windows", both registered from XML (<see cref="JobTaskXml"/>, <see cref="LogonTaskXml"/>)
/// because the plain switches cannot turn off the battery rules or set time limits, plus the matching query and
/// delete calls. Pure, so the quoting rules are unit-tested.
/// </summary>
public static class SchtasksCommandBuilder
{
    /// <summary>Task Scheduler folder that holds every PC Checker task.</summary>
    public const string Folder = "PC Checker";

    /// <summary>The logon task used for "Start with Windows".</summary>
    public const string LogonTaskName = Folder + "\\Start";

    /// <summary><c>/Create /TN name /XML file /F</c>: registers (or replaces) a task from a definition file.</summary>
    public static SchtasksCommand CreateFromXml(string taskName, string xmlPath)
    {
        if (string.IsNullOrWhiteSpace(xmlPath))
        {
            throw new ArgumentException("The task definition file is required.", nameof(xmlPath));
        }

        return new SchtasksCommand(["/Create", "/TN", RequireName(taskName), "/XML", xmlPath.Trim(), "/F"]);
    }

    /// <summary><c>/Create /TN "PC Checker\Start" /XML file /F</c> for "Start with Windows".</summary>
    public static SchtasksCommand CreateLogon(string xmlPath) => CreateFromXml(LogonTaskName, xmlPath);

    /// <summary><c>/Delete /TN name /F</c> (no confirmation prompt).</summary>
    public static SchtasksCommand Delete(string taskName) => new(["/Delete", "/TN", RequireName(taskName), "/F"]);

    /// <summary><c>/Query /TN name /FO CSV /V /NH</c>: verbose CSV without a header, parsed by column position.</summary>
    public static SchtasksCommand Query(string taskName) => new(["/Query", "/TN", RequireName(taskName), "/FO", "CSV", "/V", "/NH"]);

    /// <summary>The "Task To Run" text Task Scheduler shows for a task: the quoted executable followed by its arguments.</summary>
    public static string TaskRun(string executablePath, string? arguments)
    {
        var run = "\"" + executablePath.Trim() + "\"";
        return string.IsNullOrWhiteSpace(arguments) ? run : run + " " + arguments.Trim();
    }

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
}
