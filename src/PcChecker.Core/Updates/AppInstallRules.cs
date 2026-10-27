using PcChecker.Core.TaskManager;

namespace PcChecker.Core.Updates;

/// <summary>
/// Pure rules for "Install in Program Files": the portable PcChecker.exe copies itself to
/// <c>C:\Program Files\PC Checker\PcChecker.exe</c>, a folder only administrators can change. Replace Task Manager,
/// Start with Windows and the schedules start PC Checker with administrator rights, so they only work from there.
/// </summary>
public static class AppInstallRules
{
    /// <summary>The folder created under Program Files.</summary>
    public const string FolderName = "PC Checker";

    /// <summary>The installed file name, the same for the x64 and Arm builds (Replace Task Manager expects it).</summary>
    public const string ExeName = "PcChecker.exe";

    /// <summary>The Start menu shortcut (all users).</summary>
    public const string ShortcutName = "PC Checker.lnk";

    /// <summary>
    /// Where the app is installed under <paramref name="programFilesRoot"/>, or null when the root is unknown. Built with
    /// backslashes so it is the same on the Linux test runner.
    /// </summary>
    public static string? InstallPath(string? programFilesRoot) =>
        string.IsNullOrWhiteSpace(programFilesRoot)
            ? null
            : programFilesRoot.Trim().TrimEnd('\\', '/') + "\\" + FolderName + "\\" + ExeName;

    /// <summary>
    /// True when <paramref name="processPath"/> is a published PC Checker executable that is not already under one of
    /// <paramref name="programFilesRoots"/>, so offering to install it there makes sense.
    /// </summary>
    public static bool CanInstall(string? processPath, IEnumerable<string> programFilesRoots)
    {
        ArgumentNullException.ThrowIfNull(programFilesRoots);
        return ExeUpdateRules.CanSelfUpdate(processPath)
            && !TaskManagerReplacement.IsInstalledLocation(processPath!, programFilesRoots);
    }
}
