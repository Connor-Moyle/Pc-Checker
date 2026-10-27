using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PcChecker.Core.Common;

namespace PcChecker.Core.DiskSpace;

/// <summary>
/// Shell calls for the Disk space page: move one item to the Recycle Bin, show a file selected in File Explorer, and
/// read a file's volume and id (to spot hard links). Every call is guarded and never throws.
/// </summary>
internal static class DiskSpaceNativeMethods
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofNoConnectedElements = 0x2000;
    private const ushort FofWantNukeWarning = 0x4000;

    /// <summary>SHFILEOPSTRUCTW. The app only ships 64-bit builds, where the structure is naturally aligned.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        // FILETIME is two DWORDs (4-byte aligned), so it is spelled out rather than declared as a long.
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folderPidl, uint count, IntPtr[]? items, uint flags);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    /// <summary>
    /// Moves one file or folder to the Recycle Bin without prompts. If Windows cannot recycle it (too big for the bin),
    /// the shell asks before deleting it for good (FOF_WANTNUKEWARNING), so nothing is ever destroyed silently.
    /// </summary>
    public static OperationResult MoveToRecycleBin(string path)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("The Recycle Bin is only available on Windows.");
        }

        try
        {
            var operation = new ShFileOpStruct
            {
                Func = FoDelete,
                From = path + "\0", // the marshaller adds the second terminating null
                Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofNoConnectedElements | FofWantNukeWarning),
            };
            int code = SHFileOperation(ref operation);
            if (operation.AnyOperationsAborted)
            {
                return OperationResult.Fail("Cancelled; it was kept.");
            }

            return code == 0 ? OperationResult.Ok("Moved to the Recycle Bin.") : OperationResult.Fail(DescribeError(code));
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Windows could not move it to the Recycle Bin: " + ex.Message, ex);
        }
    }

    /// <summary>Opens the folder that holds <paramref name="filePath"/> with the file selected.</summary>
    public static OperationResult ShowFileInExplorer(string filePath)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("File Explorer is only available on Windows.");
        }

        IntPtr pidl = IntPtr.Zero;
        try
        {
            int hr = SHParseDisplayName(filePath, IntPtr.Zero, out pidl, 0, out _);
            if (hr != 0 || pidl == IntPtr.Zero)
            {
                return OperationResult.Fail($"Windows could not find {filePath}.");
            }

            hr = SHOpenFolderAndSelectItems(pidl, 0, null, 0);
            return hr == 0 ? OperationResult.Ok("Opened File Explorer.") : OperationResult.Fail($"File Explorer could not be opened (error 0x{hr:X8}).");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("File Explorer could not be opened: " + ex.Message, ex);
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                ILFree(pidl);
            }
        }
    }

    /// <summary>"volume:fileid" for an open file; hard links to the same data give the same key. Null when unknown.</summary>
    public static string? FileIdentity(SafeFileHandle handle)
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        try
        {
            if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks <= 1)
            {
                // A file with a single link cannot share its data with another path.
                return null;
            }

            ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return info.VolumeSerialNumber.ToString("X8", CultureInfo.InvariantCulture) + ":" + index.ToString("X16", CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeError(int code) => code switch
    {
        0x02 or 0x7C or 0x402 => "It no longer exists.",
        0x05 or 0x78 => "Access was denied. It may be in use, or need administrator rights.",
        0x20 or 0x21 => "It is in use by another program. Close the program and try again.",
        0x4C7 => "Cancelled; it was kept.",
        _ => $"Windows could not move it to the Recycle Bin (code 0x{code:X}).",
    };
}
