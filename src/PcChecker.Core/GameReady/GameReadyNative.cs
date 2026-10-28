using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PcChecker.Core.GameReady;

/// <summary>
/// Read-only Windows calls for the Game ready check: the PCI Express link of a device (Configuration Manager device
/// properties), the memory ranges Windows assigned to it (its allocated resource descriptors, as on Device Manager's
/// Resources tab), the power status and the Windows 11 power mode. Every call returns null on any failure.
/// </summary>
internal static class GameReadyNative
{
    private const int CrSuccess = 0;
    private const uint AllocLogConf = 0x2;
    private const uint BootLogConf = 0x3;
    private const uint ResTypeAll = 0x0;
    private const uint ResTypeMem = 0x1;
    private const uint ResTypeMemLarge = 0x7;
    private const uint DevPropTypeUInt32 = 0x7;

    /// <summary>The device's current and maximum link width and speed, or null when it is not a PCI Express device.</summary>
    public static PcieLink? ReadLink(string pnpDeviceId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(pnpDeviceId))
        {
            return null;
        }

        try
        {
            if (CM_Locate_DevNodeW(out uint node, pnpDeviceId, 0) != CrSuccess)
            {
                return null;
            }

            var link = new PcieLink(ReadUInt32(node, PcieLinkRules.CurrentLinkWidthId), ReadUInt32(node, PcieLinkRules.MaxLinkWidthId),
                ReadUInt32(node, PcieLinkRules.CurrentLinkSpeedId), ReadUInt32(node, PcieLinkRules.MaxLinkSpeedId));
            return link.CurrentWidth is null && link.MaxWidth is null && link.CurrentSpeed is null ? null : link;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return null;
        }
    }

    /// <summary>The sizes in bytes of the memory ranges Windows assigned to the device, or null when they cannot be read.</summary>
    public static IReadOnlyList<ulong>? ReadMemoryRanges(string pnpDeviceId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(pnpDeviceId))
        {
            return null;
        }

        try
        {
            if (CM_Locate_DevNodeW(out uint node, pnpDeviceId, 0) != CrSuccess)
            {
                return null;
            }

            return ReadRanges(node, AllocLogConf) ?? ReadRanges(node, BootLogConf);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return null;
        }
    }

    /// <summary>(has battery, on battery, energy saver on) from GetSystemPowerStatus; nulls when unknown.</summary>
    public static (bool HasBattery, bool? OnBattery, bool? SaverOn) ReadPowerStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, null, null);
        }

        try
        {
            if (!GetSystemPowerStatus(out var status))
            {
                return (false, null, null);
            }

            var (onBattery, _) = Overclocking.GpuLoad.PowerStatusProbe.Interpret(status.ACLineStatus, status.BatteryFlag, status.BatteryLifePercent);
            bool hasBattery = status.BatteryFlag != 128 && status.BatteryFlag != 255;
            return (hasBattery, hasBattery ? onBattery : false, status.SystemStatusFlag == 1);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (false, null, null);
        }
    }

    /// <summary>The active power mode overlay GUID (Windows 10 1709 and later), or null.</summary>
    public static Guid? ReadPowerOverlay()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return PowerGetEffectiveOverlayScheme(out var overlay) == 0 ? overlay : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static List<ulong>? ReadRanges(uint node, uint which)
    {
        if (CM_Get_First_Log_Conf(out nint logConf, node, which) != CrSuccess)
        {
            return null;
        }

        var sizes = new List<ulong>();
        try
        {
            nint current = logConf;
            bool currentIsDescriptor = false;
            while (CM_Get_Next_Res_Des(out nint next, current, ResTypeAll, out uint resourceType, 0) == CrSuccess)
            {
                if (currentIsDescriptor)
                {
                    CM_Free_Res_Des_Handle(current);
                }

                current = next;
                currentIsDescriptor = true;
                if (resourceType is not (ResTypeMem or ResTypeMemLarge) || CM_Get_Res_Des_Data_Size(out uint size, current, 0) != CrSuccess || size < 24)
                {
                    continue;
                }

                var buffer = new byte[size];
                if (CM_Get_Res_Des_Data(current, buffer, size, 0) != CrSuccess)
                {
                    continue;
                }

                // MEM_DES / MEM_LARGE_DES: Count, Type (4 bytes each), then the allocated base and end (8 bytes each).
                ulong start = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8));
                ulong end = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(16));
                if (end > start)
                {
                    sizes.Add(end - start + 1);
                }
            }

            if (currentIsDescriptor)
            {
                CM_Free_Res_Des_Handle(current);
            }
        }
        finally
        {
            CM_Free_Log_Conf_Handle(logConf);
        }

        return sizes;
    }

    private static int? ReadUInt32(uint node, uint propertyId)
    {
        var key = new DevPropKey { FormatId = PcieLinkRules.PciDevicePropertySet, PropertyId = propertyId };
        var buffer = new byte[4];
        uint size = 4;
        if (CM_Get_DevNode_PropertyW(node, ref key, out uint type, buffer, ref size, 0) != CrSuccess || size < 4 || type != DevPropTypeUInt32)
        {
            return null;
        }

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return value is > 0 and < 1024 ? (int)value : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint propertyType, byte[] buffer, ref uint bufferSize, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_First_Log_Conf(out nint logConf, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Next_Res_Des(out nint resDes, nint current, uint forResource, out uint resourceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Res_Des_Data_Size(out uint size, nint resDes, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Res_Des_Data(nint resDes, byte[] buffer, uint bufferLength, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Free_Res_Des_Handle(nint resDes);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Free_Log_Conf_Handle(nint logConf);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
}
