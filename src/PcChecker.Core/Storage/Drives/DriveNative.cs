using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcChecker.Core.Storage;

/// <summary>
/// Windows calls for one physical drive: seek penalty and power state (a handle with no read/write access, which never
/// sends a command to the drive), ATA IDENTIFY and SMART over SMART_RCV_DRIVE_DATA, IOCTL_ATA_PASS_THROUGH or SAT ATA
/// PASS-THROUGH for USB bridges, the NVMe health log, and the PCIe link of an NVMe controller. Every call is guarded
/// and returns null on failure.
/// </summary>
internal static class DriveNative
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint SmartRcvDriveData = 0x0007C088;
    private const uint IoctlAtaPassThrough = 0x0004D02C;
    private const uint IoctlScsiPassThrough = 0x0004D004;

    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int StorageAdapterProtocolSpecificProperty = 49;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int ProtocolTypeNvme = 3;
    private const int NvmeDataTypeLogPage = 2;
    private const int NvmeLogHealth = 2;
    private const int SectorBytes = 512;

    private const int ErrorAccessDenied = 5;

    /// <summary>Why a read failed, so the page can say "needs administrator" rather than "not supported".</summary>
    internal enum OpenResult
    {
        Ok,
        AccessDenied,
        Failed,
    }

    public static SafeFileHandle? OpenQueryOnly(int diskNumber) => Open(diskNumber, 0, out _);

    public static SafeFileHandle? OpenReadWrite(int diskNumber, out OpenResult result) => Open(diskNumber, GenericRead | GenericWrite, out result);

    /// <summary>True when Windows measured a seek penalty (spinning disk), false for solid state, null when unknown.</summary>
    public static bool? SeekPenalty(SafeFileHandle handle)
    {
        var input = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(input, StorageDeviceSeekPenaltyProperty);
        var output = new byte[12];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, input, input.Length, output, output.Length, out int returned, IntPtr.Zero) || returned < 9)
        {
            return null;
        }

        return output[8] != 0;
    }

    /// <summary>Active or asleep (spun down). Asking does not wake the drive.</summary>
    public static DrivePowerState PowerState(SafeFileHandle handle)
    {
        try
        {
            return GetDevicePowerState(handle, out bool on) ? (on ? DrivePowerState.Active : DrivePowerState.Asleep) : DrivePowerState.Unknown;
        }
        catch
        {
            return DrivePowerState.Unknown;
        }
    }

    /// <summary>SMART_RCV_DRIVE_DATA (the classic SMART path of the Windows ATA drivers).</summary>
    public static byte[]? SmartRcv(SafeFileHandle handle, AtaCommands.TaskFile tf)
    {
        var input = new byte[32 + 1];
        BinaryPrimitives.WriteInt32LittleEndian(input, SectorBytes);
        input[4] = tf.Features;
        input[5] = tf.SectorCount;
        input[6] = tf.LbaLow;
        input[7] = tf.LbaMid;
        input[8] = tf.LbaHigh;
        input[9] = tf.Device;
        input[10] = tf.Command;
        input[12] = 0; // drive number
        var output = new byte[16 + SectorBytes];
        if (!DeviceIoControl(handle, SmartRcvDriveData, input, 32, output, output.Length, out int returned, IntPtr.Zero) || returned < 16 + SectorBytes)
        {
            return null;
        }

        if (output[4] != 0)
        {
            return null; // driver error
        }

        return output.AsSpan(16, SectorBytes).ToArray();
    }

    /// <summary>IOCTL_ATA_PASS_THROUGH with a 512-byte PIO data-in transfer.</summary>
    public static byte[]? AtaPassThrough(SafeFileHandle handle, AtaCommands.TaskFile tf)
    {
        bool x64 = IntPtr.Size == 8;
        int structSize = x64 ? 48 : 40;
        int dataOffset = structSize;
        var buffer = new byte[dataOffset + SectorBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)structSize);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), 0x0001 | 0x0002); // DRDY required, data in
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), SectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), 10); // seconds
        int taskFile;
        if (x64)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(24), (ulong)dataOffset);
            taskFile = 40;
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20), (uint)dataOffset);
            taskFile = 32;
        }

        buffer[taskFile] = tf.Features;
        buffer[taskFile + 1] = tf.SectorCount;
        buffer[taskFile + 2] = tf.LbaLow;
        buffer[taskFile + 3] = tf.LbaMid;
        buffer[taskFile + 4] = tf.LbaHigh;
        buffer[taskFile + 5] = tf.Device;
        buffer[taskFile + 6] = tf.Command;
        if (!DeviceIoControl(handle, IoctlAtaPassThrough, buffer, buffer.Length, buffer, buffer.Length, out int returned, IntPtr.Zero) || returned < dataOffset)
        {
            return null;
        }

        // The status register comes back in CurrentTaskFile[6]; bit 0 is ERR.
        if ((buffer[taskFile + 6] & 0x01) != 0)
        {
            return null;
        }

        return buffer.AsSpan(dataOffset, SectorBytes).ToArray();
    }

    /// <summary>IOCTL_SCSI_PASS_THROUGH with a SAT ATA PASS-THROUGH CDB, the path USB bridges understand (when they do).</summary>
    public static byte[]? ScsiPassThrough(SafeFileHandle handle, byte[] cdb)
    {
        bool x64 = IntPtr.Size == 8;
        int structSize = x64 ? 56 : 44;
        int senseOffset = structSize;
        const int senseLength = 32;
        int dataOffset = senseOffset + senseLength;
        var buffer = new byte[dataOffset + SectorBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)structSize);
        buffer[6] = (byte)cdb.Length;
        buffer[7] = senseLength;
        buffer[8] = 1; // SCSI_IOCTL_DATA_IN
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), SectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), 10); // seconds
        int cdbOffset;
        if (x64)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(24), (ulong)dataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(32), (uint)senseOffset);
            cdbOffset = 36;
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20), (uint)dataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24), (uint)senseOffset);
            cdbOffset = 28;
        }

        cdb.CopyTo(buffer, cdbOffset);
        if (!DeviceIoControl(handle, IoctlScsiPassThrough, buffer, buffer.Length, buffer, buffer.Length, out int returned, IntPtr.Zero) || returned < structSize)
        {
            return null;
        }

        if (buffer[2] != 0)
        {
            return null; // SCSI status not GOOD: the bridge rejected the command
        }

        return buffer.AsSpan(dataOffset, SectorBytes).ToArray();
    }

    /// <summary>The NVMe SMART / Health Information log (02h) through the Windows NVMe driver.</summary>
    public static byte[]? NvmeHealthLog(SafeFileHandle handle)
    {
        foreach (int property in new[] { StorageDeviceProtocolSpecificProperty, StorageAdapterProtocolSpecificProperty })
        {
            const int header = 8;
            const int protocolData = 40;
            var buffer = new byte[header + protocolData + SectorBytes];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, property);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), 0); // PropertyStandardQuery
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), ProtocolTypeNvme);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), NvmeDataTypeLogPage);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), NvmeLogHealth);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(20), 0);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), protocolData);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28), SectorBytes);
            try
            {
                if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buffer, buffer.Length, buffer, buffer.Length, out int returned, IntPtr.Zero) || returned < header + protocolData)
                {
                    continue;
                }

                int offset = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(24));
                int length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(28));
                int start = header + offset;
                if (length < 192 || start < 0 || start + Math.Min(length, SectorBytes) > buffer.Length)
                {
                    continue;
                }

                return buffer.AsSpan(start, Math.Min(length, SectorBytes)).ToArray();
            }
            catch
            {
                // try the other property
            }
        }

        return null;
    }

    /// <summary>"PCIe 4.0 x4" for the PCI device above a disk's PnP node (the NVMe controller), or null.</summary>
    public static string? PcieLink(string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId))
        {
            return null;
        }

        try
        {
            if (CM_Locate_DevNodeW(out uint node, pnpDeviceId, 0) != 0)
            {
                return null;
            }

            for (int level = 0; level < 4; level++)
            {
                if (ReadUInt32(node, 9) is { } speed)
                {
                    return DriveText.PcieLink((int)speed, ReadUInt32(node, 10) is { } w ? (int)w : null,
                        ReadUInt32(node, 7) is { } ms ? (int)ms : null, ReadUInt32(node, 8) is { } mw ? (int)mw : null);
                }

                if (CM_Get_Parent(out uint parent, node, 0) != 0)
                {
                    return null;
                }

                node = parent;
            }
        }
        catch
        {
            // cfgmgr32 missing or the node vanished
        }

        return null;
    }

    private static uint? ReadUInt32(uint node, uint pid)
    {
        var key = new DevPropKey { FormatId = new Guid("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62"), PropertyId = pid };
        var buffer = new byte[4];
        uint size = 4;
        if (CM_Get_DevNode_PropertyW(node, ref key, out uint type, buffer, ref size, 0) != 0 || size < 4 || type != 7)
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static SafeFileHandle? Open(int diskNumber, uint access, out OpenResult result)
    {
        result = OpenResult.Failed;
        if (diskNumber < 0 || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var handle = CreateFileW($@"\\.\PhysicalDrive{diskNumber}", access, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                result = Marshal.GetLastWin32Error() == ErrorAccessDenied ? OpenResult.AccessDenied : OpenResult.Failed;
                handle.Dispose();
                return null;
            }

            result = OpenResult.Ok;
            return handle;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize, byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetDevicePowerState(SafeFileHandle device, [MarshalAs(UnmanagedType.Bool)] out bool on);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint propertyType, byte[] buffer, ref uint bufferSize, uint flags);
}

/// <summary>
/// Cheap, cached "is this disk asleep?" answers for other parts of the app (the hardware monitor polls drives every
/// second and would otherwise keep a spun-down disk awake). Asking never wakes the drive.
/// </summary>
public static class DrivePowerProbe
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<int, (DateTime At, DrivePowerState State)> Cache = new();

    /// <summary>The power state of \\.\PhysicalDrive<paramref name="diskNumber"/>; Unknown off Windows or on failure.</summary>
    public static DrivePowerState Query(int diskNumber)
    {
        if (diskNumber < 0 || !OperatingSystem.IsWindows())
        {
            return DrivePowerState.Unknown;
        }

        var now = DateTime.UtcNow;
        if (Cache.TryGetValue(diskNumber, out var hit) && now - hit.At < CacheFor)
        {
            return hit.State;
        }

        var state = DrivePowerState.Unknown;
        try
        {
            using var handle = DriveNative.OpenQueryOnly(diskNumber);
            if (handle is not null)
            {
                state = DriveNative.PowerState(handle);
            }
        }
        catch
        {
            state = DrivePowerState.Unknown;
        }

        Cache[diskNumber] = (now, state);
        return state;
    }

    public static bool IsAsleep(int diskNumber) => Query(diskNumber) == DrivePowerState.Asleep;

    /// <summary>
    /// The disk number in a LibreHardwareMonitor ATA storage identifier ("/hdd/2" → 2). NVMe ("/nvme/…") and other
    /// identifiers return null: only ATA disks spin down and only they are skipped while asleep.
    /// </summary>
    public static int? DiskNumberFromMonitorId(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var parts = identifier.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].Equals("hdd", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) && n >= 0
            ? n
            : null;
    }

    /// <summary>True when a LibreHardwareMonitor storage item should not be updated now because its disk is asleep.</summary>
    public static bool ShouldSkipMonitorUpdate(string? identifier) =>
        DiskNumberFromMonitorId(identifier) is { } n && IsAsleep(n);
}
