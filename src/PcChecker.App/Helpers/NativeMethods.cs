using System.Runtime.InteropServices;

namespace PcChecker.App.Helpers;

/// <summary>P/Invoke declarations used by the shell (window styles for the overlay, hotkeys, monitor and memory queries).</summary>
internal static class NativeMethods
{
    public const int GwlExStyle = -20;
    public const int WsExTransparent = 0x00000020;
    public const int WsExLayered = 0x00080000;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;
    public const int WmHotkey = 0x0312;

    public static readonly IntPtr HwndTopmost = new(-1);
    public static readonly IntPtr HwndMessage = new(-3);

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;

    public const uint MonitorDefaultToPrimary = 0x00000001;
    public const uint MonitorDefaultToNearest = 0x00000002;

    /// <summary>Win32 error returned by RegisterHotKey when another app already owns the combination.</summary>
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    [Flags]
    public enum HotkeyModifiers : uint
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, HotkeyModifiers fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>Adds the given extended-style bits to a window. Returns false when the call failed.</summary>
    public static bool AddExtendedStyle(IntPtr hWnd, int bits)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        int style = GetWindowLong(hWnd, GwlExStyle);
        return SetWindowLong(hWnd, GwlExStyle, style | bits) != 0 || Marshal.GetLastWin32Error() == 0;
    }

    /// <summary>Removes the given extended-style bits from a window. Returns false when the call failed.</summary>
    public static bool RemoveExtendedStyle(IntPtr hWnd, int bits)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        int style = GetWindowLong(hWnd, GwlExStyle);
        return SetWindowLong(hWnd, GwlExStyle, style & ~bits) != 0 || Marshal.GetLastWin32Error() == 0;
    }

    /// <summary>Re-asserts topmost without moving, resizing or activating the window.</summary>
    public static bool BringToTopmost(IntPtr hWnd) =>
        hWnd != IntPtr.Zero && SetWindowPos(hWnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    /// <summary>Physical memory in use (bytes) and the percentage Windows reports, or null when the call fails.</summary>
    public static (ulong UsedBytes, ulong TotalBytes, uint LoadPercent)? QueryMemory()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
            {
                return null;
            }

            return (status.TotalPhys - status.AvailPhys, status.TotalPhys, status.MemoryLoad);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Work area (physical pixels) of the monitor under the mouse cursor, falling back to the primary monitor.</summary>
    public static Rect? QueryWorkAreaUnderCursor()
    {
        try
        {
            IntPtr monitor = IntPtr.Zero;
            if (GetCursorPos(out var cursor))
            {
                monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
            }

            if (monitor == IntPtr.Zero)
            {
                monitor = MonitorFromPoint(new Point { X = 0, Y = 0 }, MonitorDefaultToPrimary);
            }

            if (monitor == IntPtr.Zero)
            {
                return null;
            }

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            return GetMonitorInfo(monitor, ref info) ? info.WorkArea : null;
        }
        catch
        {
            return null;
        }
    }
}
