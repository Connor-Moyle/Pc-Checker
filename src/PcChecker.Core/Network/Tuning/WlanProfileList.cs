using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Network.Tuning;

/// <summary>
/// Reads the buffers the native WLAN API returns (<c>WLAN_INTERFACE_INFO_LIST</c>, <c>WLAN_PROFILE_INFO_LIST</c>).
/// The API is Unicode end to end, unlike <c>netsh wlan show profiles</c>, whose output is written in the console's
/// OEM code page and turns names such as "Connor’s iPhone" or emoji into look-alikes or question marks. Pure.
/// </summary>
public static class WlanProfileListReader
{
    /// <summary>WCHAR strProfileName[256] + DWORD dwFlags.</summary>
    public const int ProfileInfoSize = (256 * 2) + 4;

    /// <summary>GUID InterfaceGuid + WCHAR strInterfaceDescription[256] + WLAN_INTERFACE_STATE isState.</summary>
    public const int InterfaceInfoSize = 16 + (256 * 2) + 4;

    /// <summary>dwNumberOfItems + dwIndex before the items.</summary>
    public const int HeaderSize = 8;

    /// <summary>Bytes a list with <paramref name="count"/> items of <paramref name="itemSize"/> occupies.</summary>
    public static int ListSize(int count, int itemSize) => HeaderSize + (Math.Max(0, count) * itemSize);

    /// <summary>Profile names in preference order (the first is tried first).</summary>
    public static IReadOnlyList<string> ProfileNames(ReadOnlySpan<byte> list)
    {
        var names = new List<string>();
        foreach (var item in Items(list, ProfileInfoSize))
        {
            string name = FixedString(list.Slice(item, 256 * 2));
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>The GUIDs of the wireless interfaces.</summary>
    public static IReadOnlyList<Guid> InterfaceIds(ReadOnlySpan<byte> list)
    {
        var ids = new List<Guid>();
        foreach (var item in Items(list, InterfaceInfoSize))
        {
            ids.Add(new Guid(list.Slice(item, 16)));
        }

        return ids;
    }

    private static List<int> Items(ReadOnlySpan<byte> list, int itemSize)
    {
        var offsets = new List<int>();
        if (list.Length < HeaderSize)
        {
            return offsets;
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(list);
        for (int i = 0; i < count; i++)
        {
            int offset = HeaderSize + (i * itemSize);
            if (offset + itemSize > list.Length)
            {
                break;
            }

            offsets.Add(offset);
        }

        return offsets;
    }

    private static string FixedString(ReadOnlySpan<byte> utf16)
    {
        string text = Encoding.Unicode.GetString(utf16);
        int end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }
}

/// <summary>Saved Wi-Fi profile names per wireless interface, straight from wlanapi.dll.</summary>
internal static class WlanProfileList
{
    /// <summary>Each wireless interface's profile names in preference order; null when the WLAN service cannot be asked.</summary>
    public static IReadOnlyList<(Guid InterfaceId, IReadOnlyList<string> Profiles)>? Read()
    {
        if (!PlatformGuard.IsWindows)
        {
            return null;
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            if (NativeMethods.WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0)
            {
                return null;
            }

            if (NativeMethods.WlanEnumInterfaces(handle, IntPtr.Zero, out var interfaceList) != 0)
            {
                return null;
            }

            IReadOnlyList<Guid> interfaces;
            try
            {
                interfaces = WlanProfileListReader.InterfaceIds(Copy(interfaceList, WlanProfileListReader.InterfaceInfoSize));
            }
            finally
            {
                NativeMethods.WlanFreeMemory(interfaceList);
            }

            var result = new List<(Guid, IReadOnlyList<string>)>();
            foreach (var id in interfaces)
            {
                var guid = id;
                if (NativeMethods.WlanGetProfileList(handle, ref guid, IntPtr.Zero, out var profileList) != 0)
                {
                    return null;
                }

                try
                {
                    result.Add((id, WlanProfileListReader.ProfileNames(Copy(profileList, WlanProfileListReader.ProfileInfoSize))));
                }
                finally
                {
                    NativeMethods.WlanFreeMemory(profileList);
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.WlanCloseHandle(handle, IntPtr.Zero);
            }
        }
    }

    private static byte[] Copy(IntPtr list, int itemSize)
    {
        if (list == IntPtr.Zero)
        {
            return [];
        }

        int count = Marshal.ReadInt32(list);
        var buffer = new byte[WlanProfileListReader.ListSize(count, itemSize)];
        Marshal.Copy(list, buffer, 0, buffer.Length);
        return buffer;
    }

    private static class NativeMethods
    {
        [DllImport("wlanapi.dll")]
        public static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanGetProfileList(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved, out IntPtr profileList);

        [DllImport("wlanapi.dll")]
        public static extern void WlanFreeMemory(IntPtr memory);
    }
}
