using System.Runtime.InteropServices;
using System.Security.Principal;
using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>Shell calls for the Recycle Bin and the elevation check. Every call is guarded: failures return null/false.</summary>
internal static class CleanupNativeMethods
{
    private const uint ShErbNoConfirmation = 0x00000001;
    private const uint ShErbNoProgressUi = 0x00000002;
    private const uint ShErbNoSound = 0x00000004;
    private const int EUnexpected = unchecked((int)0x8000FFFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    /// <summary>Total bytes and item count across every drive's Recycle Bin, or null when the shell call fails.</summary>
    public static (long Bytes, long Items)? QueryRecycleBin()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            var info = new ShQueryRbInfo { cbSize = (uint)Marshal.SizeOf<ShQueryRbInfo>() };
            int hr = SHQueryRecycleBinW(null, ref info);
            return hr == 0 ? (info.i64Size, info.i64NumItems) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Empties every Recycle Bin silently. Returns Ok when done or already empty.</summary>
    public static OperationResult EmptyRecycleBin()
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("The Recycle Bin can only be emptied on Windows.");
        }

        try
        {
            int hr = SHEmptyRecycleBinW(IntPtr.Zero, null, ShErbNoConfirmation | ShErbNoProgressUi | ShErbNoSound);
            if (hr == 0)
            {
                return OperationResult.Ok("Recycle Bin emptied.");
            }

            if (hr == EUnexpected)
            {
                return OperationResult.Ok("The Recycle Bin was already empty.");
            }

            return OperationResult.Fail($"Windows refused to empty the Recycle Bin (error 0x{hr:X8}).");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Could not empty the Recycle Bin: " + ex.Message, ex);
        }
    }

    /// <summary>True when the process runs with administrator rights.</summary>
    public static bool IsElevated()
    {
        if (!PlatformGuard.IsWindows)
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
