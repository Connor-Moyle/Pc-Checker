using PcChecker.Core.ScheduledTasks;

namespace PcChecker.Core.Software;

/// <summary>
/// Decides when an uninstaller must be started without administrator rights. PC Checker runs as administrator, and a
/// program started from it directly inherits those rights. A per-user install's uninstall command comes from HKCU and
/// its uninstaller usually lives in AppData: any program the user runs can change either, so starting it from here
/// would hand that program administrator rights. Pure.
/// </summary>
public static class UninstallElevationRules
{
    /// <summary>
    /// Why the uninstaller is started with the user's normal rights, or null when it may run with PC Checker's.
    /// <paramref name="fileName"/> is the program <see cref="UninstallCommandBuilder.Build"/> returned.
    /// </summary>
    public static string? UnelevatedReason(InstalledProgram program, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(program);

        // Even msiexec is not started with administrator rights for these: its arguments come from HKCU as well.
        if (program.Source == SoftwareSource.User)
        {
            return "It was installed for your account only, and its uninstall command is stored where any program can change it.";
        }

        return TaskSuspicionRules.WritableFolder(fileName) is { } folder
            ? $"Its uninstaller is in {folder}, which any program can change."
            : null;
    }
}
