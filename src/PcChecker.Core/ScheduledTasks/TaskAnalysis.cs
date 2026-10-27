using PcChecker.Core.Processes;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>
/// Fills in what PC Checker works out about a task once its program and signature are known: Microsoft or not, the
/// warning flags and the notes. Shared by the Windows and demo services so both show the same thing. Pure.
/// </summary>
public static class TaskAnalysis
{
    public const string MissingProgramNote = "The program it runs no longer exists, so the task fails every time. It is probably left over from an uninstalled program.";

    public static ScheduledTaskInfo Complete(ScheduledTaskInfo task, string? programPath, bool? programExists, SignatureInfo signature)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(signature);
        bool microsoft = TaskPublisherRules.IsMicrosoft(task.Path, task.Definition.Author, signature);
        var flags = TaskSuspicionRules.Evaluate(task.Path, task.Definition, programPath, signature, microsoft);
        var notes = new List<string>();
        if (programExists == false)
        {
            notes.Add(MissingProgramNote);
        }

        if (task.Definition.NeedsPassword)
        {
            notes.Add("It runs with a saved password. If you delete it, putting it back needs that password in Task Scheduler.");
        }

        return task with
        {
            ProgramPath = programPath,
            ProgramExists = programExists,
            Signature = signature,
            IsMicrosoft = microsoft,
            Flags = flags,
            Notes = notes,
        };
    }

    /// <summary>
    /// True when the program's signature should be checked: always for other companies' tasks, and for Windows' own
    /// upkeep tasks (\Microsoft\Windows\) when the program lies outside the Windows folder, because a program
    /// planted under a Microsoft folder would otherwise pass as Microsoft's and stay out of the default view.
    /// </summary>
    public static bool NeedsSignatureCheck(ScheduledTaskInfo task, string? programPath, string windowsDirectory)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (string.IsNullOrWhiteSpace(programPath))
        {
            return false;
        }

        if (!task.IsWindowsTask)
        {
            return true;
        }

        var windows = windowsDirectory.Replace('/', '\\').TrimEnd('\\') + "\\";
        return !programPath.Replace('/', '\\').StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A privacy-friendly web search for the task name and its program's file name.</summary>
    public static string SearchUrl(ScheduledTaskInfo task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var program = task.ProgramPath is { } p ? " " + p.Replace('/', '\\').Split('\\')[^1] : string.Empty;
        var query = ("\"" + task.Name + "\"" + program + " scheduled task").Trim();
        return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(query);
    }

    /// <summary>Task Scheduler's "never" dates (1899 or 1999) become null.</summary>
    public static DateTimeOffset? RealDate(DateTime value) =>
        value.Year < 2000 ? null : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local));
}
