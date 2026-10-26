using System.Runtime.InteropServices;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// P/Invoke surface of the Intel Graphics Control Library (ControlLib.dll) for Arc overclocking. Layouts follow
/// igcl_api.h; C <c>bool</c> fields are one byte.
/// </summary>
internal static class IntelIgclNative
{
    public const string LibraryName = "ControlLib.dll";
    public const uint Success = 0;

    // ctl_result_t values from igcl_api.h (CTL_RESULT_ERROR_*). These three drive the legacy-layout retry in
    // ReadOcProperties, so they must match the header exactly.
    public const uint ErrorUnsupportedVersion = 0x40000009;
    public const uint ErrorInvalidSize = 0x4000000F;
    public const uint ErrorUnsupportedSize = 0x40000010;
    public const int MaxDeviceNameLength = 100;
    public const int MaxReservedSize = 112;
    public const uint DeviceTypeGraphics = 1;
    public const uint AdapterFlagIntegrated = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct InitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        public Guid ApplicationUid;
    }

    /// <summary>ctl_oc_control_info_t.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ControlInfo
    {
        public byte Supported;
        public byte Relative;
        public byte Reference;
        public double Min;
        public double Max;
        public double Step;
        public double Default;
        public double ReferenceValue;

        public IgclControl ToControl() => new(Supported != 0, Relative != 0, Min, Max, Step, Default);
    }

    /// <summary>ctl_oc_properties_t (version 1 layout: six controls, then the three added later).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct OcProperties
    {
        public uint Size;
        public byte Version;
        public byte Supported;
        public ControlInfo GpuFrequencyOffset;
        public ControlInfo GpuVoltageOffset;
        public ControlInfo VramFrequencyOffset;
        public ControlInfo VramVoltageOffset;
        public ControlInfo PowerLimit;
        public ControlInfo TemperatureLimit;
        public ControlInfo VramMemSpeedLimit;
        public ControlInfo GpuVfCurveVoltageLimit;
        public ControlInfo GpuVfCurveFrequencyLimit;
    }

    /// <summary>ctl_device_adapter_properties_t.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceProperties
    {
        public uint Size;
        public byte Version;
        public IntPtr DeviceId;
        public uint DeviceIdSize;
        public uint DeviceType;
        public uint SupportedSubfunctionFlags;
        public ulong DriverVersion;
        public ulong FirmwareMajor;
        public ulong FirmwareMinor;
        public ulong FirmwareBuild;
        public uint PciVendorId;
        public uint PciDeviceId;
        public uint RevId;
        public uint NumEusPerSubSlice;
        public uint NumSubSlicesPerSlice;
        public uint NumSlices;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxDeviceNameLength)] public byte[] Name;
        public uint GraphicsAdapterProperties;
        public uint Frequency;
        public ushort PciSubsysId;
        public ushort PciSubsysVendorId;
        public byte Bus;
        public byte Device;
        public byte Function;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxReservedSize)] public byte[] Reserved;

        public string NameText => Name is null ? string.Empty : System.Text.Encoding.ASCII.GetString(Name).TrimEnd('\0').Trim();

        public bool IsIntegrated => (GraphicsAdapterProperties & AdapterFlagIntegrated) != 0;
    }

    [DllImport(LibraryName, EntryPoint = "ctlInit")]
    public static extern uint Init(ref InitArgs args, out IntPtr apiHandle);

    [DllImport(LibraryName, EntryPoint = "ctlClose")]
    public static extern uint Close(IntPtr apiHandle);

    [DllImport(LibraryName, EntryPoint = "ctlEnumerateDevices")]
    public static extern uint EnumerateDevices(IntPtr apiHandle, ref uint count, [Out] IntPtr[]? devices);

    [DllImport(LibraryName, EntryPoint = "ctlGetDeviceProperties")]
    public static extern uint GetDeviceProperties(IntPtr device, IntPtr properties);

    [DllImport(LibraryName, EntryPoint = "ctlOverclockGetProperties")]
    public static extern uint OverclockGetProperties(IntPtr device, IntPtr properties);

    [DllImport(LibraryName, EntryPoint = "ctlOverclockWaiverSet")]
    public static extern uint OverclockWaiverSet(IntPtr device);

    [DllImport(LibraryName, EntryPoint = "ctlOverclockGpuFrequencyOffsetGet")]
    public static extern uint GpuFrequencyOffsetGet(IntPtr device, out double value);

    [DllImport(LibraryName, EntryPoint = "ctlOverclockGpuFrequencyOffsetSet")]
    public static extern uint GpuFrequencyOffsetSet(IntPtr device, double value);

    [DllImport(LibraryName, EntryPoint = "ctlOverclockVramFrequencyOffsetGet")]
    public static extern uint VramFrequencyOffsetGet(IntPtr device, out double value);

    [DllImport(LibraryName, EntryPoint = "ctlOverclockVramFrequencyOffsetSet")]
    public static extern uint VramFrequencyOffsetSet(IntPtr device, double value);

    [DllImport(LibraryName, EntryPoint = "ctlOverclockPowerLimitGet")]
    public static extern uint PowerLimitGet(IntPtr device, out double value);

    [DllImport(LibraryName, EntryPoint = "ctlOverclockPowerLimitSet")]
    public static extern uint PowerLimitSet(IntPtr device, double value);

    [DllImport(LibraryName, EntryPoint = "ctlOverclockTemperatureLimitGet")]
    public static extern uint TemperatureLimitGet(IntPtr device, out double value);

    [DllImport(LibraryName, EntryPoint = "ctlOverclockTemperatureLimitSet")]
    public static extern uint TemperatureLimitSet(IntPtr device, double value);

    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (NativeLibrary.TryLoad(LibraryName, out var handle))
        {
            NativeLibrary.Free(handle);
            return true;
        }

        return false;
    }

    public static uint Initialize(out IntPtr apiHandle)
    {
        var args = new InitArgs
        {
            Size = (uint)Marshal.SizeOf<InitArgs>(),
            Version = 0,
            AppVersion = (1u << 16) | 0u,
            Flags = 0,
            SupportedVersion = (1u << 16) | 0u,
            ApplicationUid = new Guid("8f5a0b66-2d8e-4a1f-9c3d-5f1a1b2c3d4e"),
        };
        return Init(ref args, out apiHandle);
    }

    public static (uint Code, IntPtr[] Devices) GetDevices(IntPtr apiHandle)
    {
        uint count = 0;
        uint code = EnumerateDevices(apiHandle, ref count, null);
        if (code != Success || count == 0)
        {
            return (code, []);
        }

        var devices = new IntPtr[count];
        code = EnumerateDevices(apiHandle, ref count, devices);
        return (code, code == Success ? devices.Take((int)count).ToArray() : []);
    }

    /// <summary>Reads device properties; returns null when the call fails for every known struct size.</summary>
    public static DeviceProperties? ReadDeviceProperties(IntPtr device)
    {
        int size = Marshal.SizeOf<DeviceProperties>();
        var buffer = Marshal.AllocHGlobal(size);
        var luid = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.Copy(new byte[8], 0, luid, 8);

            var props = new DeviceProperties { Size = (uint)size, Version = 0, DeviceId = luid, DeviceIdSize = 8, Name = new byte[MaxDeviceNameLength], Reserved = new byte[MaxReservedSize] };
            Marshal.StructureToPtr(props, buffer, false);
            uint code = GetDeviceProperties(device, buffer);
            return code == Success ? Marshal.PtrToStructure<DeviceProperties>(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(luid);
        }
    }

    /// <summary>Reads overclock ranges, trying the version-1 layout first and the original six-control layout second.</summary>
    public static (uint Code, OcProperties? Properties) ReadOcProperties(IntPtr device)
    {
        int fullSize = Marshal.SizeOf<OcProperties>();
        int controlSize = Marshal.SizeOf<ControlInfo>();
        int legacySize = fullSize - 3 * controlSize;
        uint lastCode = Success;
        foreach (var (size, version) in new[] { (fullSize, (byte)1), (legacySize, (byte)0) })
        {
            var buffer = Marshal.AllocHGlobal(fullSize);
            try
            {
                Marshal.Copy(new byte[fullSize], 0, buffer, fullSize);

                Marshal.WriteInt32(buffer, 0, size);
                Marshal.WriteByte(buffer, 4, version);
                lastCode = OverclockGetProperties(device, buffer);
                if (lastCode == Success)
                {
                    return (lastCode, Marshal.PtrToStructure<OcProperties>(buffer));
                }

                if (lastCode is not (ErrorInvalidSize or ErrorUnsupportedSize or ErrorUnsupportedVersion))
                {
                    return (lastCode, null);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return (lastCode, null);
    }
}
