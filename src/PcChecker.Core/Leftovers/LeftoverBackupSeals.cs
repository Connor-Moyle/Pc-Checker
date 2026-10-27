using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace PcChecker.Core.Leftovers;

/// <summary>
/// Keeps the seal of each scheduled task backup (see <see cref="LeftoverBackupNaming.Seal"/>) under
/// <c>HKLM\SOFTWARE\PcChecker\LeftoverBackups\&lt;user SID&gt;</c>, where only administrators can write. The backup
/// files sit in the user's profile; without the seal a program running unelevated could change a backed-up task so
/// that the elevated restore registers its own command.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LeftoverBackupSeals
{
    private const string RootPath = @"SOFTWARE\PcChecker\LeftoverBackups";

    /// <summary>Records the seal of a new backup.</summary>
    /// <exception cref="IOException">The seal could not be stored.</exception>
    public static void Save(string backupId, string seal)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(UserPath(), writable: true)
                ?? throw new IOException("The key for leftover backup seals could not be created.");
            key.SetValue(backupId, seal, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            throw new IOException("The backup seal could not be stored: " + ex.Message, ex);
        }
    }

    /// <summary>The recorded seal of a backup, or null when there is none or it cannot be read.</summary>
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

    /// <summary>Backups are per user (they live in the user's profile), so their seals are kept per user too.</summary>
    private static string UserPath()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new IOException("The current user could not be identified.");
        return RootPath + "\\" + sid;
    }
}
