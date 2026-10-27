using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace PcChecker.Core.RegistryHealth;

/// <summary>
/// Keeps the seal of each registry backup (see <see cref="RegistryBackupCatalog.Seal"/>) under
/// <c>HKLM\SOFTWARE\PcChecker\RegistryBackups\&lt;user SID&gt;</c>. Keys under HKLM\SOFTWARE can only be changed by
/// administrators, unlike the backup folder in the user's profile, so a program running without elevation cannot
/// make a changed backup pass the check the elevated restore does before importing it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RegistryBackupSeals
{
    private const string RootPath = @"SOFTWARE\PcChecker\RegistryBackups";

    /// <summary>Records the seal of a new backup.</summary>
    /// <exception cref="IOException">The seal could not be stored.</exception>
    public static void Save(string backupId, string seal)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(UserPath(), writable: true)
                ?? throw new IOException("The key for registry backup seals could not be created.");
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

    /// <summary>Forgets the seal of a deleted backup. Failures are ignored: a stale seal matches no file.</summary>
    public static void Remove(string backupId)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(UserPath(), writable: true);
            key?.DeleteValue(backupId, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // Nothing to undo: without its files the seal is never used.
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
