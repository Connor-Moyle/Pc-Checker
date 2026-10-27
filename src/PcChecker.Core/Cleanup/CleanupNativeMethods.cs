using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>
/// Shell calls for the Recycle Bin, the elevation check and handle-based file deletion. Every call is guarded:
/// failures return null/false.
/// </summary>
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

    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileWriteAttributes = 0x0100;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeNormal = 0x00000080;
    private const int FileBasicInfoClass = 0;
    private const int FileDispositionInfoClass = 4;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, char[] buffer, uint bufferLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileBasicInfo(SafeFileHandle file, int informationClass, ref FileBasicInfo information, uint size);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileDispositionInfo(SafeFileHandle file, int informationClass, ref byte deleteFile, uint size);

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

    /// <summary>
    /// Opens a file for deletion without following a reparse point at its own name: the handle refers to the link
    /// itself, never to what it points at. Folders in the path are resolved by Windows, so the caller compares
    /// <see cref="FinalPath"/> with the allowed root before deleting. Null when the file cannot be opened;
    /// <paramref name="missing"/> tells a vanished file from one that is in use or protected.
    /// </summary>
    public static SafeFileHandle? OpenForDelete(string path, out bool missing)
    {
        missing = false;
        var handle = CreateFile(path, Delete | FileReadAttributes | FileWriteAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        int error = Marshal.GetLastWin32Error();
        missing = error is ErrorFileNotFound or ErrorPathNotFound;
        handle.Dispose();
        return null;
    }

    /// <summary>Attributes, last write time (UTC) and size of an open file, or null when Windows does not say.</summary>
    public static (FileAttributes Attributes, DateTime LastWriteUtc, long Size)? Describe(SafeFileHandle file)
    {
        if (!GetFileInformationByHandle(file, out var info))
        {
            return null;
        }

        long lastWrite = ((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow;
        long size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        return ((FileAttributes)info.FileAttributes, DateTime.FromFileTimeUtc(lastWrite), size);
    }

    /// <summary>The real location of an open file or folder (all junctions and symlinks resolved), in plain form.</summary>
    public static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                return CleanupSafety.StripExtendedPrefix(new string(buffer, 0, (int)length));
            }

            buffer = new char[length + 1];
        }

        return null;
    }

    /// <summary>The real location of a folder (all junctions and symlinks resolved), or null when it cannot be opened.</summary>
    public static string? FinalDirectoryPath(string directory)
    {
        using var handle = CreateFile(directory, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        return handle.IsInvalid ? null : FinalPath(handle);
    }

    /// <summary>Clears ReadOnly, System and Hidden on an open file so it can be deleted. Timestamps are left alone.</summary>
    public static bool ClearAttributes(SafeFileHandle file)
    {
        var info = new FileBasicInfo { FileAttributes = FileAttributeNormal };
        return SetFileBasicInfo(file, FileBasicInfoClass, ref info, (uint)Marshal.SizeOf<FileBasicInfo>());
    }

    /// <summary>Marks an open file for deletion; Windows removes it when the handle is closed.</summary>
    public static bool MarkForDeletion(SafeFileHandle file)
    {
        byte deleteFile = 1;
        return SetFileDispositionInfo(file, FileDispositionInfoClass, ref deleteFile, 1);
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
