using OpenRGB.NET;

namespace PcChecker.Core.Lighting;

/// <summary>Converts OpenRGB.NET controller data into the app's <see cref="RgbDevice"/> model. Pure (no network).</summary>
public static class OpenRgbDeviceMapper
{
    public static RgbDevice Map(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var modes = (device.Modes ?? []).Select(MapMode).ToList();
        var zones = (device.Zones ?? []).Select((z, i) => MapZone(i, z.Name, z.LedCount, z.LedsMin, z.LedsMax)).ToList();
        var type = OpenRgbDeviceTypes.Refine(OpenRgbDeviceTypes.FriendlyName(device.Type), device.Name);
        var first = device.Colors is { Length: > 0 } colors ? ToRgb(colors[0]) : (RgbColor?)null;
        return new RgbDevice(
            device.Index,
            string.IsNullOrWhiteSpace(device.Name) ? $"Device {device.Index + 1}" : device.Name.Trim(),
            string.IsNullOrWhiteSpace(device.Vendor) ? GuessVendor(device.Name) : device.Vendor.Trim(),
            type,
            string.IsNullOrWhiteSpace(device.Serial) ? null : device.Serial.Trim(),
            LedCount(device.Leds?.Length, device.Colors?.Length, zones),
            zones,
            modes,
            device.ActiveModeIndex,
            first,
            string.IsNullOrWhiteSpace(device.Location) ? null : device.Location.Trim());
    }

    /// <summary>
    /// One zone. The index is the zone's position in the device's list (what the SDK's zone commands expect), not a
    /// value read back from the data. LedsMin/LedsMax are kept so resizable zones (addressable headers, which start
    /// at 0 LEDs) can be recognised; a minimum above the maximum is treated as "not resizable".
    /// </summary>
    public static RgbZone MapZone(int position, string? name, uint ledCount, uint ledsMin, uint ledsMax)
    {
        int count = (int)Math.Min(ledCount, int.MaxValue);
        int min = (int)Math.Min(ledsMin, int.MaxValue);
        int max = (int)Math.Min(ledsMax, int.MaxValue);
        if (min > max)
        {
            (min, max) = (count, count);
        }

        return new RgbZone(position, string.IsNullOrWhiteSpace(name) ? $"Zone {position + 1}" : name.Trim(), count, min, max);
    }

    /// <summary>
    /// The device's LED count: the LED list, else the colour list (one colour per LED), else the sum of the zones.
    /// Taking the larger of the LED and colour lists covers servers that send an empty LED-name list.
    /// </summary>
    public static int LedCount(int? leds, int? colors, IReadOnlyList<RgbZone> zones)
    {
        int fromLists = Math.Max(leds ?? 0, colors ?? 0);
        return fromLists > 0 ? fromLists : zones.Sum(z => z.LedCount);
    }

    public static RgbMode MapMode(Mode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return MapMode(mode.Index, mode.Name, mode.Flags, mode.SpeedMin, mode.SpeedMax);
    }

    /// <summary>Flag-level mapping so tests can cover every combination without a live client.</summary>
    public static RgbMode MapMode(int index, string? name, ModeFlags flags, uint speedMin, uint speedMax)
    {
        bool hasColor = flags.HasFlag(ModeFlags.HasPerLedColor) || flags.HasFlag(ModeFlags.HasModeSpecificColor);
        bool hasSpeed = flags.HasFlag(ModeFlags.HasSpeed);
        bool hasBrightness = flags.HasFlag(ModeFlags.HasBrightness);
        int? min = hasSpeed ? (int)Math.Min(speedMin, int.MaxValue) : null;
        int? max = hasSpeed ? (int)Math.Min(speedMax, int.MaxValue) : null;
        return new RgbMode(index, string.IsNullOrWhiteSpace(name) ? $"Mode {index + 1}" : name.Trim(), hasColor, hasSpeed, hasBrightness, min, max);
    }

    /// <summary>True when the mode lets the client set every LED (Direct/Static style modes).</summary>
    public static bool IsPerLed(Mode mode) => mode.Flags.HasFlag(ModeFlags.HasPerLedColor) || mode.ColorMode == ColorMode.PerLed;

    public static RgbColor ToRgb(Color color) => new(color.R, color.G, color.B);

    public static Color ToOpenRgb(RgbColor color) => new(color.R, color.G, color.B);

    public static Color[] ToOpenRgb(IReadOnlyList<RgbColor> colors)
    {
        var arr = new Color[colors.Count];
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i] = ToOpenRgb(colors[i]);
        }

        return arr;
    }

    /// <summary>Older OpenRGB servers (protocol &lt; 1) send no vendor string; take the first word of the name.</summary>
    public static string GuessVendor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unknown";
        }

        var first = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? "Unknown" : first;
    }
}
