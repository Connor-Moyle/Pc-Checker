using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcChecker.Core.Storage;

/// <summary>
/// Thin wrappers over CreateFile/ReadFile/WriteFile for unbuffered, sector-aligned I/O on physical drives and test
/// files. Every call is synchronous; callers run them on a background thread. Windows only.
/// </summary>
internal static class RawDiskIo
{
    public const int SectorAlignment = 4096;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint CreateAlways = 2;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagDeleteOnClose = 0x04000000;
    private const uint FileBegin = 0;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;

    /// <summary>Win32 errors that mean "this sector is unreadable" rather than "the device is gone".</summary>
    public static readonly int[] SectorErrors = [23 /* CRC */, 27 /* sector not found */, 1117 /* I/O device error */, 1392 /* corrupt */];

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long distanceToMove, out long newFilePointer, uint moveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    /// <summary>Opens <c>\\.\PhysicalDrive{n}</c> read-only, unbuffered. Throws <see cref="Win32Exception"/> when it cannot be opened.</summary>
    public static SafeFileHandle OpenPhysicalDrive(int diskNumber)
    {
        var handle = CreateFile($@"\\.\PhysicalDrive{diskNumber}", GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            FileFlagNoBuffering | FileFlagSequentialScan, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Could not open physical drive {diskNumber} (error {error}). Administrator rights are required.");
        }

        return handle;
    }

    /// <summary>
    /// Creates (or truncates) a file for unbuffered, write-through I/O. The file is marked delete-on-close, so the
    /// kernel removes it when the handle closes, including when the process is killed mid-benchmark.
    /// </summary>
    public static SafeFileHandle CreateUnbufferedFile(string path)
    {
        var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead, IntPtr.Zero, CreateAlways,
            FileAttributeNormal | FileFlagNoBuffering | FileFlagWriteThrough | FileFlagDeleteOnClose, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Could not create the test file {path} (error {error}).");
        }

        return handle;
    }

    /// <summary>Byte length of a physical drive via IOCTL_DISK_GET_LENGTH_INFO; null when the call fails.</summary>
    public static long? GetDriveLength(SafeFileHandle device)
    {
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            if (DeviceIoControl(device, IoctlDiskGetLengthInfo, IntPtr.Zero, 0, buffer, 8, out _, IntPtr.Zero))
            {
                return Marshal.ReadInt64(buffer);
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads up to <paramref name="length"/> bytes at <paramref name="offset"/>. Returns bytes read and the Win32 error (0 on success).</summary>
    public static (int BytesRead, int Error) ReadAt(SafeFileHandle file, long offset, AlignedBuffer buffer, int length)
    {
        if (!SetFilePointerEx(file, offset, out _, FileBegin))
        {
            return (0, Marshal.GetLastWin32Error());
        }

        if (!ReadFile(file, buffer.Pointer, (uint)Math.Min(length, buffer.Length), out uint read, IntPtr.Zero))
        {
            return ((int)read, Marshal.GetLastWin32Error());
        }

        return ((int)read, 0);
    }

    /// <summary>Writes <paramref name="length"/> bytes at <paramref name="offset"/>. Returns bytes written and the Win32 error (0 on success).</summary>
    public static (int BytesWritten, int Error) WriteAt(SafeFileHandle file, long offset, AlignedBuffer buffer, int length)
    {
        if (!SetFilePointerEx(file, offset, out _, FileBegin))
        {
            return (0, Marshal.GetLastWin32Error());
        }

        if (!WriteFile(file, buffer.Pointer, (uint)Math.Min(length, buffer.Length), out uint written, IntPtr.Zero))
        {
            return ((int)written, Marshal.GetLastWin32Error());
        }

        return ((int)written, 0);
    }

    public static void Flush(SafeFileHandle file) => FlushFileBuffers(file);

    /// <summary>Rounds down to the sector alignment unbuffered I/O requires.</summary>
    public static int AlignDown(long bytes) => (int)(bytes - bytes % SectorAlignment);

    public static string DescribeError(int error)
    {
        try
        {
            return new Win32Exception(error).Message;
        }
        catch
        {
            return $"error {error}";
        }
    }
}

/// <summary>Native memory aligned to the sector size, as unbuffered I/O demands.</summary>
internal sealed class AlignedBuffer : IDisposable
{
    private IntPtr _raw;

    public AlignedBuffer(int length, int alignment = RawDiskIo.SectorAlignment)
    {
        Length = length;
        _raw = Marshal.AllocHGlobal(length + alignment);
        long address = _raw.ToInt64();
        long aligned = (address + alignment - 1) & ~((long)alignment - 1);
        Pointer = new IntPtr(aligned);
    }

    public IntPtr Pointer { get; }

    public int Length { get; }

    /// <summary>Fills the buffer with pseudo-random bytes so compressing drives cannot cheat the benchmark.</summary>
    public void FillRandom(int seed)
    {
        var data = new byte[Length];
        new Random(seed).NextBytes(data);
        Marshal.Copy(data, 0, Pointer, Length);
    }

    public void Dispose()
    {
        if (_raw != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_raw);
            _raw = IntPtr.Zero;
        }
    }
}
