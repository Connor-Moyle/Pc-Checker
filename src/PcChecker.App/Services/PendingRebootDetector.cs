using Microsoft.Win32;
using PcChecker.Core.Common;

namespace PcChecker.App.Services;

/// <summary>
/// Checks the registry flags Windows sets when a restart is needed (Component Based Servicing, Windows Update and
/// pending file renames). Read-only; returns null when it cannot tell.
/// </summary>
public static class PendingRebootDetector
{
    /// <summary>Returns the reason a restart is pending, or null when none is pending (or it cannot be determined).</summary>
    public static string? Detect()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            using var base64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (KeyExists(base64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            {
                return "Windows finished installing a component update and needs a restart.";
            }

            if (KeyExists(base64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            {
                return "Windows Update has installed updates that need a restart.";
            }

            using var session = base64.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (session?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 })
            {
                return "An installer or uninstaller left files to replace at the next restart.";
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool KeyExists(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key is not null;
    }
}
