using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PcChecker.Core.Software;

/// <summary>
/// Reads a registry key's last-write time via <c>RegQueryInfoKey</c>. Installers that forget to write
/// <c>InstallDate</c> still touch the key when they create it, so this is a good fallback for the install date.
/// </summary>
public static class RegistryKeyTimestamp
{
    /// <summary>Returns the key's last-write time (local), or null when it cannot be read.</summary>
    public static DateTime? TryGetLastWriteTime(RegistryKey? key)
    {
        if (key is null || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            SafeRegistryHandle handle = key.Handle;
            int result = RegQueryInfoKey(handle, null, IntPtr.Zero, IntPtr.Zero, out _, out _, out _, out _, out _, out _, out _, out long fileTime);
            if (result != 0 || fileTime <= 0)
            {
                return null;
            }

            var utc = DateTime.FromFileTimeUtc(fileTime);
            return utc.Year < 1995 ? null : utc.ToLocalTime();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int RegQueryInfoKey(
        SafeRegistryHandle hKey,
        [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder? lpClass,
        IntPtr lpcchClass,
        IntPtr lpReserved,
        out uint lpcSubKeys,
        out uint lpcbMaxSubKeyLen,
        out uint lpcbMaxClassLen,
        out uint lpcValues,
        out uint lpcbMaxValueNameLen,
        out uint lpcbMaxValueLen,
        out uint lpcbSecurityDescriptor,
        out long lpftLastWriteTime);
}
