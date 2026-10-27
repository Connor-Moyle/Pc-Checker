namespace PcChecker.Core.Lighting;

/// <summary>
/// Pure decisions about how to apply a colour, mode or profile to a device: which mode to switch to, which profile
/// entry applies, how brightness is folded into the colour, and whether a debounced change should be sent yet.
/// </summary>
public static class LightingApplyPlanner
{
    public const int DefaultDebounceMs = 150;

    /// <summary>Mode names that take a plain colour, in order of preference.</summary>
    private static readonly string[] PreferredColorModes = ["Direct", "Static", "Custom"];

    /// <summary>
    /// The mode index a device should be in before a colour is sent: the active mode when it accepts a colour,
    /// otherwise "Direct", then "Static", then the first mode with a colour. Null when nothing accepts a colour.
    /// </summary>
    public static int? PickColorModeIndex(IReadOnlyList<RgbMode> modes, int activeModeIndex)
    {
        if (modes.Count == 0)
        {
            return null;
        }

        if (activeModeIndex >= 0 && activeModeIndex < modes.Count && modes[activeModeIndex].HasColor)
        {
            return activeModeIndex;
        }

        foreach (var preferred in PreferredColorModes)
        {
            var hit = modes.FirstOrDefault(m => m.HasColor && m.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit.Index;
            }
        }

        return modes.FirstOrDefault(m => m.HasColor)?.Index;
    }

    /// <summary>True when the device must change mode before the colour can be shown.</summary>
    public static bool NeedsModeSwitch(IReadOnlyList<RgbMode> modes, int activeModeIndex, int targetModeIndex) =>
        activeModeIndex != targetModeIndex || activeModeIndex < 0 || activeModeIndex >= modes.Count;

    /// <summary>Finds a mode by name, case-insensitively. Null when the device has no such mode.</summary>
    public static int? FindModeIndex(IReadOnlyList<RgbMode> modes, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return modes.FirstOrDefault(m => m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))?.Index;
    }

    /// <summary>The "Off" mode when the device has one.</summary>
    public static int? FindOffModeIndex(IReadOnlyList<RgbMode> modes) => FindModeIndex(modes, "Off");

    /// <summary>Words that identify a device's own rainbow-style effect, most specific first.</summary>
    private static readonly string[] RainbowWords = ["rainbow", "spectrum", "cycle"];

    /// <summary>Mode names that show a steady colour (so the colour the server reports is the colour that was set).</summary>
    private static readonly string[] SteadyModes = ["Direct", "Static", "Custom"];

    /// <summary>True when a profile asks for a rainbow effect by name.</summary>
    public static bool IsRainbowRequest(string? modeName) => modeName?.Contains("rainbow", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The device's own rainbow-style effect ("Rainbow", "Rainbow Wave", "Spectrum Cycle", "Color Cycle"…), or null
    /// when it has none.
    /// </summary>
    public static int? FindRainbowModeIndex(IReadOnlyList<RgbMode> modes)
    {
        foreach (var word in RainbowWords)
        {
            var hit = modes.FirstOrDefault(m => m.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit.Index;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the mode a profile entry names: the exact name first; for a rainbow request, any rainbow-style effect the
    /// device offers. Null when the device has nothing matching.
    /// </summary>
    public static int? ResolveModeIndex(IReadOnlyList<RgbMode> modes, string? modeName)
    {
        var exact = FindModeIndex(modes, modeName);
        if (exact is not null || !IsRainbowRequest(modeName))
        {
            return exact;
        }

        return FindRainbowModeIndex(modes);
    }

    /// <summary>
    /// True when the mode shows a steady colour, so the first LED colour the server reports is the colour that was set
    /// (an animated effect such as Breathing reports whatever colour it is mid-way through).
    /// </summary>
    public static bool IsSteadyMode(RgbMode? mode) =>
        mode is not null
        && (SteadyModes.Any(n => mode.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) || (mode.HasColor && !mode.HasSpeed));

    /// <summary>
    /// Whether brightness can be applied to a mode. PC Checker dims by scaling the colour it sends (OpenRGB.NET 3.1.1
    /// cannot send a mode's own brightness value), so only modes that take a colour can be dimmed.
    /// </summary>
    public static bool CanDim(RgbMode? mode) => mode?.HasColor == true;

    /// <summary>Support statement for a mode's brightness, per the honesty rules.</summary>
    public const string BrightnessUnsupportedNote =
        "Brightness: not supported for this effect through the OpenRGB SDK. PC Checker dims by scaling the colour it sends, and this effect picks its own colours; set its brightness in OpenRGB's own window.";

    /// <summary>
    /// How long to wait before the next automatic reconnect after <paramref name="failedAttempts"/> failures in a row:
    /// 0.5 s, 1 s, 2 s, 4 s, 8 s, 16 s, then every 30 s.
    /// </summary>
    public static TimeSpan ReconnectBackoff(int failedAttempts)
    {
        if (failedAttempts <= 0)
        {
            return TimeSpan.Zero;
        }

        int exponent = Math.Min(failedAttempts - 1, 6);
        return TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * (1 << exponent)));
    }

    /// <summary>
    /// Which entry of a profile applies to a device: an exact device name, then the device type (e.g. "Keyboard"),
    /// then the "*" wildcard. Null when the profile says nothing about this device.
    /// </summary>
    public static LightingAssignment? ResolveAssignment(LightingProfile profile, RgbDevice device)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(device);
        if (profile.Devices.Count == 0)
        {
            return null;
        }

        if (TryGetAssignment(profile, device.Name, out var byName))
        {
            return byName;
        }

        if (!string.IsNullOrWhiteSpace(device.Type) && TryGetAssignment(profile, device.Type, out var byType))
        {
            return byType;
        }

        return TryGetAssignment(profile, "*", out var wildcard) ? wildcard : null;
    }

    /// <summary>
    /// Case-insensitive lookup that also works for profiles loaded from JSON, where the dictionary comes back with the
    /// default (case-sensitive) comparer.
    /// </summary>
    public static bool TryGetAssignment(LightingProfile profile, string key, out LightingAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Devices.TryGetValue(key, out var direct) && direct is not null)
        {
            assignment = direct;
            return true;
        }

        foreach (var pair in profile.Devices)
        {
            if (pair.Value is not null && string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                assignment = pair.Value;
                return true;
            }
        }

        assignment = null!;
        return false;
    }

    /// <summary>The colour actually sent: the requested colour dimmed to <paramref name="brightnessPercent"/> when one is given.</summary>
    public static RgbColor EffectiveColor(RgbColor color, int? brightnessPercent) =>
        brightnessPercent is { } b && b < 100 ? RgbColorMath.Scale(color, b) : color;

    /// <summary>Clamps a requested speed to the mode's range (or 0–100 when the mode has no range).</summary>
    public static int ClampSpeed(RgbMode mode, int speed)
    {
        int min = mode.MinSpeed ?? 0;
        int max = mode.MaxSpeed ?? 100;
        if (max < min)
        {
            (min, max) = (max, min);
        }

        return Math.Clamp(speed, min, max);
    }

    /// <summary>Clamps a requested LED count to the zone's limits (a fixed zone keeps its count).</summary>
    public static int ClampZoneSize(RgbZone zone, int ledCount)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (!zone.IsResizable)
        {
            return zone.LedCount;
        }

        return Math.Clamp(ledCount, zone.LedsMin!.Value, zone.LedsMax!.Value);
    }

    /// <summary>Maps a 0–100 slider position onto the mode's speed range.</summary>
    public static int SpeedFromPercent(RgbMode mode, int percent)
    {
        int min = mode.MinSpeed ?? 0;
        int max = mode.MaxSpeed ?? 100;
        if (max <= min)
        {
            return min;
        }

        double f = Math.Clamp(percent, 0, 100) / 100.0;
        return (int)Math.Round(min + (max - min) * f);
    }

    /// <summary>Maps a mode speed back onto a 0–100 slider.</summary>
    public static int PercentFromSpeed(RgbMode mode, int speed)
    {
        int min = mode.MinSpeed ?? 0;
        int max = mode.MaxSpeed ?? 100;
        if (max <= min)
        {
            return 0;
        }

        return (int)Math.Round(100.0 * (Math.Clamp(speed, min, max) - min) / (max - min));
    }

    /// <summary>
    /// Debounce rule for sliders and pickers: send when at least <paramref name="debounceMs"/> passed since the last
    /// send, or when nothing was ever sent.
    /// </summary>
    public static bool ShouldSend(DateTimeOffset? lastSentUtc, DateTimeOffset nowUtc, int debounceMs = DefaultDebounceMs) =>
        lastSentUtc is null || (nowUtc - lastSentUtc.Value).TotalMilliseconds >= debounceMs;

    /// <summary>Builds the per-LED colour array a device or zone needs (every LED the same colour).</summary>
    public static RgbColor[] Fill(int ledCount, RgbColor color)
    {
        var arr = new RgbColor[Math.Max(0, ledCount)];
        Array.Fill(arr, color);
        return arr;
    }

    /// <summary>A rainbow spread across the LEDs of a device (used when a device has no built-in rainbow mode).</summary>
    public static RgbColor[] RainbowFill(int ledCount)
    {
        if (ledCount <= 0)
        {
            return [];
        }

        var palette = RgbColorMath.Rainbow(ledCount);
        return palette.ToArray();
    }
}
