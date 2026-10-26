using System.Runtime.InteropServices;

namespace PcChecker.Core.Input;

/// <summary>P/Invoke declarations for the mouse and keyboard parameters exposed through user32.</summary>
internal static class InputNativeMethods
{
    public const uint SpiGetMouse = 0x0003;
    public const uint SpiSetMouse = 0x0004;
    public const uint SpiGetKeyboardSpeed = 0x000A;
    public const uint SpiSetKeyboardSpeed = 0x000B;
    public const uint SpiGetKeyboardDelay = 0x0016;
    public const uint SpiSetKeyboardDelay = 0x0017;
    public const uint SpiSetDoubleClickTime = 0x0020;
    public const uint SpiGetFilterKeys = 0x0032;
    public const uint SpiSetFilterKeys = 0x0033;
    public const uint SpiGetToggleKeys = 0x0034;
    public const uint SpiSetToggleKeys = 0x0035;
    public const uint SpiGetStickyKeys = 0x003A;
    public const uint SpiSetStickyKeys = 0x003B;
    public const uint SpiSetMouseTrails = 0x005D;
    public const uint SpiGetMouseTrails = 0x005E;
    public const uint SpiGetSnapToDefButton = 0x005F;
    public const uint SpiSetSnapToDefButton = 0x0060;
    public const uint SpiGetWheelScrollLines = 0x0068;
    public const uint SpiSetWheelScrollLines = 0x0069;
    public const uint SpiGetMouseSpeed = 0x0070;
    public const uint SpiSetMouseSpeed = 0x0071;
    public const uint SpiGetMouseVanish = 0x1020;
    public const uint SpiSetMouseVanish = 0x1021;

    public const uint SpifUpdateIniFile = 0x01;
    public const uint SpifSendChange = 0x02;
    public const uint SpifPersist = SpifUpdateIniFile | SpifSendChange;

    public const int SmSwapButton = 23;

    public const uint SkfStickyKeysOn = 0x00000001;
    public const uint FkfFilterKeysOn = 0x00000001;
    public const uint TkfToggleKeysOn = 0x00000001;

    /// <summary>SPI_GETWHEELSCROLLLINES returns this when the wheel scrolls one screen per notch.</summary>
    public const uint WheelPageScroll = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    public struct StickyKeys
    {
        public uint cbSize;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ToggleKeys
    {
        public uint cbSize;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FilterKeys
    {
        public uint cbSize;
        public uint dwFlags;
        public uint iWaitMSec;
        public uint iDelayMSec;
        public uint iRepeatMSec;
        public uint iBounceMSec;
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint uiParam, ref int pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint uiParam, ref uint pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint uiParam, int[] pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint uiParam, ref StickyKeys pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint uiParam, ref ToggleKeys pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint uiParam, ref FilterKeys pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SwapMouseButton([MarshalAs(UnmanagedType.Bool)] bool swap);

    [DllImport("user32.dll")]
    public static extern uint GetCaretBlinkTime();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCaretBlinkTime(uint milliseconds);
}
