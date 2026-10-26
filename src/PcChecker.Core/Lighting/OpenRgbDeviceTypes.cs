using OpenRGB.NET;

namespace PcChecker.Core.Lighting;

/// <summary>Friendly names for OpenRGB device types and the peripheral/component split the page uses. Pure.</summary>
public static class OpenRgbDeviceTypes
{
    public const string Motherboard = "Motherboard";
    public const string Ram = "RAM";
    public const string Gpu = "GPU";
    public const string Cooler = "Cooler";
    public const string LedStrip = "LED strip";
    public const string Keyboard = "Keyboard";
    public const string Mouse = "Mouse";
    public const string MousePad = "Mouse pad";
    public const string Headset = "Headset";
    public const string HeadsetStand = "Headset stand";
    public const string Gamepad = "Controller";
    public const string Light = "Light";
    public const string Speaker = "Speaker";
    public const string Virtual = "Virtual";
    public const string CaseFan = "Case fan";
    public const string Unknown = "Other";

    /// <summary>OpenRGB's enum → the label shown on the device card.</summary>
    public static string FriendlyName(DeviceType type) => type switch
    {
        DeviceType.Motherboard => Motherboard,
        DeviceType.Dram => Ram,
        DeviceType.Gpu => Gpu,
        DeviceType.Cooler => Cooler,
        DeviceType.Ledstrip => LedStrip,
        DeviceType.Keyboard => Keyboard,
        DeviceType.Mouse => Mouse,
        DeviceType.Mousemat => MousePad,
        DeviceType.Headset => Headset,
        DeviceType.HeadsetStand => HeadsetStand,
        DeviceType.Gamepad => Gamepad,
        DeviceType.Light => Light,
        DeviceType.Speaker => Speaker,
        DeviceType.Virtual => Virtual,
        _ => Unknown,
    };

    /// <summary>Same as <see cref="FriendlyName(DeviceType)"/> but from the raw protocol value (unknown numbers → "Other").</summary>
    public static string FriendlyName(int typeCode) =>
        typeCode >= 0 && Enum.IsDefined(typeof(DeviceType), (uint)typeCode) ? FriendlyName((DeviceType)(uint)typeCode) : Unknown;

    /// <summary>Keyboards, mice, headsets and the like belong in the "Peripherals" section; everything else is a component inside the case.</summary>
    public static bool IsPeripheral(string? friendlyType) => friendlyType switch
    {
        Keyboard or Mouse or MousePad or Headset or HeadsetStand or Gamepad or Speaker => true,
        _ => false,
    };

    /// <summary>Guesses a type from a device name when the server reports "Unknown" (e.g. fan hubs named "... Fan ...").</summary>
    public static string Refine(string friendlyType, string? deviceName)
    {
        if (friendlyType != Unknown || string.IsNullOrWhiteSpace(deviceName))
        {
            return friendlyType;
        }

        var n = deviceName;
        if (n.Contains("fan", StringComparison.OrdinalIgnoreCase))
        {
            return CaseFan;
        }

        if (n.Contains("strip", StringComparison.OrdinalIgnoreCase))
        {
            return LedStrip;
        }

        if (n.Contains("keyboard", StringComparison.OrdinalIgnoreCase))
        {
            return Keyboard;
        }

        if (n.Contains("mouse", StringComparison.OrdinalIgnoreCase))
        {
            return Mouse;
        }

        return friendlyType;
    }
}
