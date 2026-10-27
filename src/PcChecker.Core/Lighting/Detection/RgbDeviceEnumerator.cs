using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcChecker.Core.Lighting;

/// <summary>
/// Read-only Windows device enumeration for the RGB detection pass: every present USB, HID and PCI device (SetupAPI)
/// and every HID interface's vendor/product ID and top-level usage (HidD_GetAttributes / HidP_GetCaps). HID devices
/// are opened with no read or write access, which only allows these queries; nothing is sent to any device.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RgbDeviceEnumerator
{
    private const uint DigcfPresent = 0x02;
    private const uint DigcfAllClasses = 0x04;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint SpdrpDeviceDesc = 0x00;
    private const uint SpdrpHardwareId = 0x01;
    private const uint SpdrpFriendlyName = 0x0C;
    private const uint FileShareReadWrite = 0x03;
    private const uint OpenExisting = 3;
    private const int HidpStatusSuccess = 0x00110000;
    private const ushort LightingUsagePage = 0x59;
    private const ushort LampArrayUsage = 0x01;

    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>Present devices whose instance ID starts with USB\, HID\ or PCI\.</summary>
    public static List<RgbPnpDevice> PresentDevices(CancellationToken cancellationToken)
    {
        var list = new List<RgbPnpDevice>();
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DigcfAllClasses | DigcfPresent);
        if (set == InvalidHandle)
        {
            return list;
        }

        try
        {
            var data = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? id = InstanceId(set, ref data);
                if (id is null || !(id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var hardwareIds = MultiString(set, ref data, SpdrpHardwareId);
                string? name = SingleString(set, ref data, SpdrpFriendlyName) ?? SingleString(set, ref data, SpdrpDeviceDesc);
                list.Add(new RgbPnpDevice(id, hardwareIds, name));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return list;
    }

    /// <summary>
    /// Every HID interface: IDs from HidD_GetAttributes for paths that do not carry them (some Bluetooth and virtual
    /// devices), and the paths of Windows Dynamic Lighting (LampArray) devices.
    /// </summary>
    public static (List<RgbPnpDevice> ExtraIds, List<string> LampArrays) HidInterfaces(CancellationToken cancellationToken)
    {
        var extra = new List<RgbPnpDevice>();
        var lamps = new List<string>();
        HidD_GetHidGuid(out var hidGuid);
        IntPtr set = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandle)
        {
            return (extra, lamps);
        }

        try
        {
            var iface = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? path = InterfacePath(set, ref iface);
                if (path is null)
                {
                    continue;
                }

                using var handle = CreateFileW(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    continue;
                }

                if (!RgbDetectionRules.TryParseUsbIds(path, out _, out _))
                {
                    var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
                    if (HidD_GetAttributes(handle, ref attributes))
                    {
                        string synthetic = string.Create(CultureInfo.InvariantCulture, $@"HID\VID_{attributes.VendorId:X4}&PID_{attributes.ProductId:X4}");
                        extra.Add(new RgbPnpDevice(synthetic, [], ProductString(handle)));
                    }
                }

                if (IsLampArray(handle))
                {
                    string label = RgbDetectionRules.TryParseUsbIds(path, out _, out _) ? path : ProductString(handle) ?? path;
                    lamps.Add(label);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return (extra, lamps);
    }

    private static bool IsLampArray(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var preparsed))
        {
            return false;
        }

        try
        {
            var caps = new byte[64]; // HIDP_CAPS: Usage, UsagePage, then 30 more USHORTs
            return HidP_GetCaps(preparsed, caps) == HidpStatusSuccess
                && BitConverter.ToUInt16(caps, 2) == LightingUsagePage
                && BitConverter.ToUInt16(caps, 0) == LampArrayUsage;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    private static string? ProductString(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        if (!HidD_GetProductString(handle, buffer, buffer.Length))
        {
            return null;
        }

        string text = Encoding.Unicode.GetString(buffer);
        int end = text.IndexOf('\0');
        text = (end >= 0 ? text[..end] : text).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? InstanceId(IntPtr set, ref SpDevinfoData data)
    {
        var buffer = new char[512];
        return SetupDiGetDeviceInstanceIdW(set, ref data, buffer, buffer.Length, out int required)
            ? new string(buffer, 0, Math.Max(0, Math.Min(required - 1, buffer.Length))).TrimEnd('\0')
            : null;
    }

    private static byte[]? Property(IntPtr set, ref SpDevinfoData data, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out int required);
        if (required <= 0 || required > 64 * 1024)
        {
            return null;
        }

        var buffer = new byte[required];
        return SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, buffer.Length, out _) ? buffer : null;
    }

    private static string? SingleString(IntPtr set, ref SpDevinfoData data, uint property)
    {
        var bytes = Property(set, ref data, property);
        if (bytes is null)
        {
            return null;
        }

        string text = Encoding.Unicode.GetString(bytes);
        int end = text.IndexOf('\0');
        text = (end >= 0 ? text[..end] : text).Trim();
        return text.Length == 0 ? null : text;
    }

    private static List<string> MultiString(IntPtr set, ref SpDevinfoData data, uint property)
    {
        var bytes = Property(set, ref data, property);
        return bytes is null
            ? []
            : Encoding.Unicode.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string? InterfacePath(IntPtr set, ref SpDeviceInterfaceData iface)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out int required, IntPtr.Zero);
        if (required <= 0 || required > 64 * 1024)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize is the size of the fixed part (8 on 64-bit, 6 on 32-bit).
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref iface, buffer, required, out _, IntPtr.Zero))
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, char[] buffer, int bufferSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, uint property,
        out uint propertyRegDataType, byte[]? propertyBuffer, int propertyBufferSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr detail,
        int detailSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, byte[] capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
