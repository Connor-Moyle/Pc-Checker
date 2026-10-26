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
