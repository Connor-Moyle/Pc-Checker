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

    /// <summary>
    /// SHQUERYRBINFO is { DWORD cbSize; __int64 i64Size; __int64 i64NumItems; }. shellapi.h applies 1-byte packing only
    /// on 32-bit builds (20 bytes, i64Size at offset 4); on x64 it is naturally aligned (24 bytes, i64Size at offset 8,
    /// i64NumItems at offset 16). The layout is therefore chosen at runtime from the pointer size instead of a fixed
    /// StructLayout, so cbSize always matches what the shell on the running architecture expects.
    /// </summary>
    private static int ShQueryRbInfoSize => IntPtr.Size == 8 ? 24 : 20;
    private static int ShQueryRbInfoSizeOffset => IntPtr.Size == 8 ? 8 : 4;
    private static int ShQueryRbInfoNumItemsOffset => IntPtr.Size == 8 ? 16 : 12;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, IntPtr pSHQueryRBInfo);

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
            int size = ShQueryRbInfoSize;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++)
                {
                    Marshal.WriteByte(buffer, i, 0);
                }

                Marshal.WriteInt32(buffer, 0, size);
                int hr = SHQueryRecycleBinW(null, buffer);
                if (hr != 0)
                {
                    return null;
                }

                return (Marshal.ReadInt64(buffer, ShQueryRbInfoSizeOffset), Marshal.ReadInt64(buffer, ShQueryRbInfoNumItemsOffset));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
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
