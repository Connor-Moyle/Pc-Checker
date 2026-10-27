namespace PcChecker.Core.ScheduledTasks;

/// <summary>
/// Which actions the Scheduled tasks page offers for a task, with the plain reason when one is refused. Windows' own
/// tasks (\Microsoft\Windows\) are read-only outside Advanced mode, Microsoft's tasks are never deleted, and PC
/// Checker's own schedules are managed in Settings. Pure.
/// </summary>
public static class TaskSafetyRules
{
    public const string OwnTaskReason = "This is one of PC Checker's own schedules. Turn it on or off in Settings → Schedules.";
    public const string WindowsReadOnlyReason = "Windows uses this task for its own upkeep, so it is read-only here. Turn on Advanced mode to turn it off or on.";

    /// <summary>Why "Run now" is refused, or null.</summary>
    public static string? RunRefusal(ScheduledTaskInfo task, bool advancedMode)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsWindowsTask && !advancedMode)
        {
            return WindowsReadOnlyReason;
        }

        if (task.State == TaskRunState.Running)
        {
            return "It is running already.";
        }

        if (task.State == TaskRunState.Disabled || !task.Definition.Enabled)
        {
            return "The task is turned off. Turn it on first.";
        }

        if (TaskActionText.RunsElevated(task.Definition) && TaskSuspicionRules.WritableFolder(task.ProgramPath) is { } folder)
        {
            return $"It would start a program from {folder} with administrator rights. Any program can change files there, so PC Checker will not start it.";
        }

        return null;
    }

    /// <summary>Why "End" is refused, or null.</summary>
    public static string? EndRefusal(ScheduledTaskInfo task, bool advancedMode)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsWindowsTask && !advancedMode)
        {
            return WindowsReadOnlyReason;
        }

        return task.State == TaskRunState.Running ? null : "It is not running.";
    }

    /// <summary>Why turning the task off or on is refused, or null.</summary>
    public static string? ToggleRefusal(ScheduledTaskInfo task, bool advancedMode)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsPcCheckerTask)
        {
            return OwnTaskReason;
        }

        return task.IsWindowsTask && !advancedMode ? WindowsReadOnlyReason : null;
    }

    /// <summary>Why "Delete" is refused, or null. Only other companies' tasks can be deleted.</summary>
    public static string? DeleteRefusal(ScheduledTaskInfo task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsPcCheckerTask)
        {
            return OwnTaskReason;
        }

        if (task.IsMicrosoft || TaskPaths.IsMicrosoftFolder(task.Path))
        {
            return "Microsoft's tasks are not deleted here. Turn it off instead if you do not want it to run.";
        }

        return null;
    }

    /// <summary>The extra warning shown before turning off one of Windows' own tasks, or null.</summary>
    public static string? TurnOffWarning(ScheduledTaskInfo task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsWindowsTask)
        {
            return "Windows uses this task for its own upkeep. Turning it off can stop updates, maintenance, backups or security checks from working. Only do this if you know what the task does.";
        }

        return task.IsMicrosoft
            ? "This task belongs to a Microsoft product. Turning it off may stop that product from updating itself."
            : null;
    }

    /// <summary>True when the task can be found by the list's filter.</summary>
    public static bool Matches(ScheduledTaskInfo task, TaskListFilter filter) => filter switch
    {
        TaskListFilter.All => true,
        TaskListFilter.Microsoft => task.IsMicrosoft,
        TaskListFilter.Flagged => task.IsFlagged,
        TaskListFilter.TurnedOff => task.State == TaskRunState.Disabled || !task.Definition.Enabled,
        _ => !task.IsMicrosoft || task.IsFlagged,
    };

    /// <summary>The tasks for a filter and search text (name, folder, program, author or publisher), sorted by name.</summary>
    public static IReadOnlyList<ScheduledTaskInfo> Filter(IEnumerable<ScheduledTaskInfo> tasks, TaskListFilter filter, string? search)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var words = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tasks
            .Where(t => Matches(t, filter))
            .Where(t => words.All(w => Haystack(t).Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => t.IsFlagged)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string Haystack(ScheduledTaskInfo t) =>
        string.Join('\n', t.Path, TaskActionText.Describe(t.Definition), t.Definition.Author, t.Definition.Description, t.Signature.Publisher);
}
