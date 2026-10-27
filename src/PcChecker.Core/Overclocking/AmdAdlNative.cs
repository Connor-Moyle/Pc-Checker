using System.Runtime.InteropServices;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// P/Invoke surface of the AMD Display Library (atiadlxx.dll) used for Overdrive8. Layouts follow the public ADL SDK
/// headers (adl_structures.h). Only the entry points PC Checker needs are declared.
/// </summary>
internal static class AmdAdlNative
{
    public const string LibraryName = "atiadlxx.dll";
    public const int AdlOk = 0;
    public const int AmdVendorId = 1002;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr MallocCallback(int size);

    /// <summary>Keeps the delegate alive for the life of the process (the driver stores the pointer).</summary>
    public static readonly MallocCallback Malloc = size => Marshal.AllocHGlobal(Math.Max(1, size));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct AdapterInfo
    {
        public int Size;
        public int AdapterIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Udid;
        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DisplayName;
        public int Present;
        public int Exist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string PnpString;
        public int OsDisplayIndex;
    }

    /// <summary>ADLOD8SingleInitSetting.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Od8SingleInitSetting
    {
        public int FeatureId;
        public int MinValue;
        public int MaxValue;
        public int DefaultValue;
    }

    /// <summary>ADLOD8SingleSetSetting.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Od8SingleSetSetting
    {
        public int Value;
        public int Requested;
        public int Reset;
    }

    [DllImport(LibraryName, EntryPoint = "ADL2_Main_Control_Create")]
    public static extern int MainControlCreate(MallocCallback callback, int enumConnectedAdapters, out IntPtr context);

    [DllImport(LibraryName, EntryPoint = "ADL2_Main_Control_Destroy")]
    public static extern int MainControlDestroy(IntPtr context);

    [DllImport(LibraryName, EntryPoint = "ADL2_Adapter_NumberOfAdapters_Get")]
    public static extern int NumberOfAdaptersGet(IntPtr context, out int numberOfAdapters);

    [DllImport(LibraryName, EntryPoint = "ADL2_Adapter_AdapterInfo_Get")]
    public static extern int AdapterInfoGet(IntPtr context, IntPtr adapterInfo, int inputSize);

    [DllImport(LibraryName, EntryPoint = "ADL2_Adapter_Active_Get")]
    public static extern int AdapterActiveGet(IntPtr context, int adapterIndex, out int status);

    [DllImport(LibraryName, EntryPoint = "ADL2_Overdrive_Caps")]
    public static extern int OverdriveCaps(IntPtr context, int adapterIndex, out int supported, out int enabled, out int version);

    [DllImport(LibraryName, EntryPoint = "ADL2_Overdrive8_Init_SettingX2_Get")]
    public static extern int Overdrive8InitSettingX2Get(IntPtr context, int adapterIndex, out int capabilities, out int numberOfFeatures, out IntPtr initSettingList);

    [DllImport(LibraryName, EntryPoint = "ADL2_Overdrive8_Current_SettingX2_Get")]
    public static extern int Overdrive8CurrentSettingX2Get(IntPtr context, int adapterIndex, out int numberOfFeatures, out IntPtr currentSettingList);

    [DllImport(LibraryName, EntryPoint = "ADL2_Overdrive8_Setting_Set")]
    public static extern int Overdrive8SettingSet(IntPtr context, int adapterIndex, IntPtr setSetting, IntPtr currentSetting);

    /// <summary>True when the library can be loaded at all (AMD driver installed).</summary>
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

    /// <summary>Reads every adapter the driver knows about.</summary>
    public static AdapterInfo[] GetAdapters(IntPtr context)
    {
        if (NumberOfAdaptersGet(context, out int count) != AdlOk || count <= 0)
        {
            return [];
        }

        int size = Marshal.SizeOf<AdapterInfo>();
        var buffer = Marshal.AllocHGlobal(size * count);
        try
        {
            Marshal.Copy(new byte[size * count], 0, buffer, size * count);

            if (AdapterInfoGet(context, buffer, size * count) != AdlOk)
            {
                return [];
            }

            var result = new AdapterInfo[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStructure<AdapterInfo>(buffer + i * size);
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads the Overdrive8 feature ranges; the list is allocated through <see cref="Malloc"/> and freed here.</summary>
    public static (int Code, int Capabilities, Od8Range[] Ranges) GetInitSettings(IntPtr context, int adapterIndex)
    {
        int code = Overdrive8InitSettingX2Get(context, adapterIndex, out int caps, out int count, out IntPtr list);
        if (code != AdlOk || list == IntPtr.Zero || count <= 0)
        {
            return (code, caps, []);
        }

        try
        {
            count = Math.Min(count, Od8Feature.MaxCount);
            int size = Marshal.SizeOf<Od8SingleInitSetting>();
            var ranges = new Od8Range[count];
            for (int i = 0; i < count; i++)
            {
                var s = Marshal.PtrToStructure<Od8SingleInitSetting>(list + i * size);
                ranges[i] = new Od8Range(s.FeatureId == 0 && i > 0 ? i : s.FeatureId, s.MinValue, s.MaxValue, s.DefaultValue);
            }

            return (code, caps, ranges);
        }
        finally
        {
            Marshal.FreeHGlobal(list);
        }
    }

    /// <summary>Reads the current Overdrive8 values indexed by feature id (the list index is the feature id).</summary>
    public static (int Code, int[] Values) GetCurrentSettings(IntPtr context, int adapterIndex)
    {
        int code = Overdrive8CurrentSettingX2Get(context, adapterIndex, out int count, out IntPtr list);
        if (code != AdlOk || list == IntPtr.Zero || count <= 0)
        {
            return (code, []);
        }

        try
        {
            count = Math.Min(count, Od8Feature.MaxCount);
            var values = new int[count];
            Marshal.Copy(list, values, 0, count);
            return (code, values);
        }
        finally
        {
            Marshal.FreeHGlobal(list);
        }
    }

    /// <summary>
    /// Sends a set table built by <see cref="AdlValueRules.BuildSetEntries"/> or <see cref="AdlValueRules.BuildResetEntries"/>.
    /// Buffers are over-allocated (<see cref="Od8Feature.MaxCount"/> entries) so a driver with a larger OD8_COUNT than
    /// the SDK we compiled against can never write past the end.
    /// </summary>
    public static int SetSettings(IntPtr context, int adapterIndex, IReadOnlyList<Od8SetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int entrySize = Marshal.SizeOf<Od8SingleSetSetting>();
        int setBytes = 4 + Od8Feature.MaxCount * entrySize;
        int currentBytes = 4 + Od8Feature.MaxCount * 4;
        var set = Marshal.AllocHGlobal(setBytes);
        var current = Marshal.AllocHGlobal(currentBytes);
        try
        {
            Marshal.Copy(new byte[setBytes], 0, set, setBytes);
            Marshal.Copy(new byte[currentBytes], 0, current, currentBytes);

            int count = Math.Clamp(entries.Count, 1, Od8Feature.MaxCount);
            Marshal.WriteInt32(set, count);
            Marshal.WriteInt32(current, count);
            for (int i = 0; i < count && i < entries.Count; i++)
            {
                var e = entries[i];
                var entry = new Od8SingleSetSetting { Value = e.Value, Requested = e.Requested ? 1 : 0, Reset = e.Reset ? 1 : 0 };
                Marshal.StructureToPtr(entry, set + 4 + i * entrySize, false);
            }

            return Overdrive8SettingSet(context, adapterIndex, set, current);
        }
        finally
        {
            Marshal.FreeHGlobal(set);
            Marshal.FreeHGlobal(current);
        }
    }
}
