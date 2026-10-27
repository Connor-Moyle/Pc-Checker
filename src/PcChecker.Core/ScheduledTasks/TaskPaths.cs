using PcChecker.Core.Scheduling;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>Task Scheduler paths ("\Vendor\Task"): name, folder and which folders belong to Microsoft or PC Checker. Pure.</summary>
public static class TaskPaths
{
    /// <summary>Normalises to a leading backslash, backslashes only and no trailing backslash.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "\\";
        }

        var p = path.Trim().Replace('/', '\\');
        if (!p.StartsWith('\\'))
        {
            p = "\\" + p;
        }

        return p.Length > 1 ? p.TrimEnd('\\') : p;
    }

    public static string NameOf(string? path)
    {
        var p = Normalize(path);
        int slash = p.LastIndexOf('\\');
        return slash >= 0 && slash < p.Length - 1 ? p[(slash + 1)..] : p;
    }

    public static string FolderOf(string? path)
    {
        var p = Normalize(path);
        int slash = p.LastIndexOf('\\');
        return slash <= 0 ? "\\" : p[..slash];
    }

    /// <summary>Under "\Microsoft\" (Windows, Office, Edge, OneDrive and other Microsoft products).</summary>
    public static bool IsMicrosoftFolder(string? path) => Normalize(path).StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Under "\Microsoft\Windows\": Windows' own upkeep, read-only on the page outside Advanced mode.</summary>
    public static bool IsWindowsTask(string? path) => Normalize(path).StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase);

    /// <summary>PC Checker's own schedules and "Start with Windows", which Settings manages.</summary>
    public static bool IsPcCheckerTask(string? path) =>
        Normalize(path).StartsWith("\\" + SchtasksCommandBuilder.Folder + "\\", StringComparison.OrdinalIgnoreCase);
}
