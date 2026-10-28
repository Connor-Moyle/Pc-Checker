using System.Runtime.InteropServices;

namespace PcChecker.Core.DeviceTests;

/// <summary>XInput 1.4 (part of Windows 8 and later) for Xbox controllers that Windows.Gaming.Input does not list.</summary>
internal static class XInputNative
{
    public const int MaxControllers = 4;
    public const int ErrorSuccess = 0;
    public const byte BatteryDevTypeGamepad = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BatteryInformation
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    public static extern int GetState(int userIndex, out State state);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    public static extern int SetState(int userIndex, ref Vibration vibration);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    public static extern int GetBatteryInformation(int userIndex, byte devType, out BatteryInformation information);
}
