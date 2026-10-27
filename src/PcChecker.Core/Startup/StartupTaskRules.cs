using PcChecker.Core.Performance;
using PcChecker.Core.ScheduledTasks;

namespace PcChecker.Core.Startup;

/// <summary>
/// The Scheduled tasks page's safety rules applied to the logon and boot tasks on the Startup page: tasks in
/// Microsoft's folders are never deleted, Windows' own tasks (\Microsoft\Windows\) are read-only here, and PC Checker's
/// own schedules are managed in Settings. Pure.
/// </summary>
public static class StartupTaskRules
{
    public const string WindowsReadOnlyReason =
        "Windows uses this task for its own upkeep, so it is read-only here. Turn on Advanced mode and use the Scheduled tasks page to turn it off or on.";

    public const string MicrosoftDeleteReason = "Microsoft's tasks are not deleted here. Turn it off instead if you do not want it to run.";

    /// <summary>The Task Scheduler path ("\Folder\Name") of a task item, or null for anything else.</summary>
    public static string? TaskPath(StartupItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind != StartupKinds.Task)
        {
            return null;
        }

        return item.Location.StartsWith(StartupEnumerator.TaskLocationPrefix, StringComparison.Ordinal)
            ? item.Location[StartupEnumerator.TaskLocationPrefix.Length..]
            : item.Location;
    }

    /// <summary>True for a task in Microsoft's folders, whatever its publisher text says (COM handler tasks have no program).</summary>
    public static bool IsInMicrosoftFolder(StartupItem item) => TaskPath(item) is { } path && TaskPaths.IsMicrosoftFolder(path);

    /// <summary>Why the item cannot be removed from the Startup page, or null.</summary>
    public static string? RemoveRefusal(StartupItem item)
    {
        if (TaskPath(item) is not { } path)
        {
            return null;
        }

        if (TaskPaths.IsPcCheckerTask(path))
        {
            return TaskSafetyRules.OwnTaskReason;
        }

        return TaskPaths.IsMicrosoftFolder(path) ? MicrosoftDeleteReason : null;
    }

    /// <summary>Why the item cannot be turned off or on from the Startup page, or null.</summary>
    public static string? ToggleRefusal(StartupItem item)
    {
        if (TaskPath(item) is not { } path)
        {
            return null;
        }

        if (TaskPaths.IsPcCheckerTask(path))
        {
            return TaskSafetyRules.OwnTaskReason;
        }

        return TaskPaths.IsWindowsTask(path) ? WindowsReadOnlyReason : null;
    }
}
