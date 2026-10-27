using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>
/// Keeps the seal of each deleted task's saved definition under
/// <c>HKLM\SOFTWARE\PcChecker\TaskBackups\&lt;user SID&gt;</c>, where only administrators can write. The saved files
/// sit in the user's profile; without the seal a program running unelevated could change one so that the elevated
/// restore registers its own command.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TaskBackupSeals
{
    private const string RootPath = @"SOFTWARE\PcChecker\TaskBackups";

    /// <exception cref="IOException">The seal could not be stored.</exception>
    public static void Save(string backupId, string seal)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(UserPath(), writable: true)
                ?? throw new IOException("The key for task backup seals could not be created.");
            key.SetValue(backupId, seal, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            throw new IOException("The backup seal could not be stored: " + ex.Message, ex);
        }
    }

    public static string? Read(string backupId)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(UserPath(), writable: false);
            return key?.GetValue(backupId) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return null;
        }
    }

    public static void Delete(string backupId)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(UserPath(), writable: true);
            key?.DeleteValue(backupId, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // A stale seal only matches its own file, which is gone.
        }
    }

    private static string UserPath()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new IOException("The current user could not be identified.");
        return RootPath + "\\" + sid;
    }
}
