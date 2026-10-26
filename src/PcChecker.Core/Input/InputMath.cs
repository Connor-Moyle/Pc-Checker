namespace PcChecker.Core.Input;

/// <summary>
/// Pure conversions between the raw Windows values (SystemParametersInfo, registry) and the words shown in the UI.
/// No Windows dependency so everything here is unit-testable.
/// </summary>
public static class InputMath
{
    /// <summary>The 11 notches of the Windows pointer-speed slider map to these speed values (1–20).</summary>
    public static readonly int[] SliderNotchSpeeds = [1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20];

    /// <summary>Lowest and highest value Windows accepts for SPI_SETMOUSESPEED.</summary>
    public const int MinSpeed = 1;
    public const int MaxSpeed = 20;

    /// <summary>Default speed (the "6/11" notch): no scaling of mouse movement.</summary>
    public const int DefaultSpeed = 10;

    /// <summary>Lowest and highest double-click time Windows accepts.</summary>
    public const int MinDoubleClickMs = 100;
    public const int MaxDoubleClickMs = 5000;

    /// <summary>Repeat delay index range (0 = shortest, 3 = longest).</summary>
    public const int MaxRepeatDelay = 3;

    /// <summary>Repeat rate index range (0 = slowest, 31 = fastest).</summary>
    public const int MaxRepeatRate = 31;

    /// <summary>
    /// The SPI_SETMOUSE triplet (threshold 1, threshold 2, acceleration). "Enhance pointer precision" on is {6, 10, 1};
    /// off is {0, 0, 0}.
    /// </summary>
    public static int[] MouseThresholds(bool enhancePointerPrecision) =>
        enhancePointerPrecision ? [6, 10, 1] : [0, 0, 0];

    /// <summary>Reads the "Enhance pointer precision" flag back from an SPI_GETMOUSE triplet.</summary>
    public static bool PrecisionFromThresholds(IReadOnlyList<int>? triplet) =>
        triplet is { Count: >= 3 } && triplet[2] != 0;

    /// <summary>Clamps a pointer speed to the 1–20 range Windows accepts.</summary>
    public static int ClampSpeed(int speed) => Math.Clamp(speed, MinSpeed, MaxSpeed);

    /// <summary>
    /// Converts a pointer speed (1–20) to the slider notch shown in the Mouse control panel, e.g. 10 → "6/11".
    /// Odd values between notches are shown as "≈ 6.5/11".
    /// </summary>
    public static string SpeedToSliderText(int speed)
    {
        speed = ClampSpeed(speed);
        int index = Array.IndexOf(SliderNotchSpeeds, speed);
        if (index >= 0)
        {
            return $"{index + 1}/11";
        }

        // Speeds 3, 5, 7, … sit between two notches.
        int lower = SliderNotchSpeeds.Count(s => s < speed);
        return $"≈ {lower}.5/11";
    }

    /// <summary>Converts a slider notch (1–11) to the pointer speed value it represents.</summary>
    public static int SliderNotchToSpeed(int notch) => SliderNotchSpeeds[Math.Clamp(notch, 1, 11) - 1];

    /// <summary>True when the pointer speed is the Windows default (no scaling), where raw input and in-game sensitivity match.</summary>
    public static bool IsDefaultSpeed(int speed) => speed == DefaultSpeed;

    /// <summary>Plain-English note for a pointer speed.</summary>
    public static string SpeedNote(int speed)
    {
        speed = ClampSpeed(speed);
        if (speed == DefaultSpeed)
        {
            return "Windows default (6/11): mouse movement is passed through 1:1. Recommended for gaming.";
        }

        return speed < DefaultSpeed
            ? "Below 6/11 Windows skips some mouse counts, which can make the pointer feel less precise."
            : "Above 6/11 Windows multiplies movement, so the pointer jumps in steps larger than one pixel.";
    }

    /// <summary>Approximate repeat delay in milliseconds for a repeat delay index (0–3 → 250–1000 ms).</summary>
    public static int RepeatDelayToMs(int delayIndex) => 250 * (Math.Clamp(delayIndex, 0, MaxRepeatDelay) + 1);

    /// <summary>
    /// Approximate repeat rate in characters per second for a repeat rate index (0 ≈ 2.5/s, 31 ≈ 30/s), as documented
    /// for SPI_SETKEYBOARDSPEED.
    /// </summary>
    public static double RepeatRateToCharsPerSecond(int rateIndex)
    {
        rateIndex = Math.Clamp(rateIndex, 0, MaxRepeatRate);
        return Math.Round(2.5 + rateIndex * (27.5 / MaxRepeatRate), 1);
    }

    /// <summary>"About 30 characters per second" style text for the repeat rate slider.</summary>
    public static string RepeatRateText(int rateIndex) =>
        $"about {RepeatRateToCharsPerSecond(rateIndex):0.#} characters per second";

    /// <summary>"Short (250 ms)" style text for the repeat delay slider.</summary>
    public static string RepeatDelayText(int delayIndex)
    {
        delayIndex = Math.Clamp(delayIndex, 0, MaxRepeatDelay);
        var word = delayIndex switch
        {
            0 => "Short",
            1 => "Normal",
            2 => "Long",
            _ => "Longest",
        };
        return $"{word} ({RepeatDelayToMs(delayIndex)} ms)";
    }

    /// <summary>Text for the double-click slider: "500 ms (Windows default)".</summary>
    public static string DoubleClickText(int ms)
    {
        ms = Math.Clamp(ms, MinDoubleClickMs, MaxDoubleClickMs);
        return ms == 500 ? $"{ms} ms (Windows default)" : ms < 500 ? $"{ms} ms (fast)" : $"{ms} ms (relaxed)";
    }

    /// <summary>Text for the wheel scroll setting.</summary>
    public static string ScrollText(int lines, bool oneScreen) =>
        oneScreen ? "One screen per notch" : lines == 1 ? "1 line per notch" : $"{Math.Max(1, lines)} lines per notch";

    /// <summary>Text for the pointer trails setting (0 or 1 = off, 2–7 = short to long).</summary>
    public static string TrailsText(int trails) => trails < 2 ? "Off" : trails <= 3 ? $"Short ({trails})" : trails <= 5 ? $"Medium ({trails})" : $"Long ({trails})";

    /// <summary>Caret blink text: "530 ms (Windows default)"; -1 or 0 means no blinking.</summary>
    public static string CursorBlinkText(int ms) => ms <= 0 ? "No blinking" : ms == 530 ? "530 ms (Windows default)" : $"{ms} ms";

    /// <summary>Returns a copy with every value clamped to what Windows accepts.</summary>
    public static MouseSettings Normalize(MouseSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        copy.Speed = ClampSpeed(copy.Speed);
        copy.DoubleClickMs = Math.Clamp(copy.DoubleClickMs, MinDoubleClickMs, MaxDoubleClickMs);
        copy.ScrollLines = Math.Clamp(copy.ScrollLines, 1, 100);
        copy.PointerTrails = Math.Clamp(copy.PointerTrails, 0, 7);
        if (copy.PointerTrails == 1)
        {
            copy.PointerTrails = 0;
        }

        return copy;
    }

    /// <summary>Returns a copy with every value clamped to what Windows accepts.</summary>
    public static KeyboardSettings Normalize(KeyboardSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        copy.RepeatDelay = Math.Clamp(copy.RepeatDelay, 0, MaxRepeatDelay);
        copy.RepeatRate = Math.Clamp(copy.RepeatRate, 0, MaxRepeatRate);
        copy.CursorBlinkMs = copy.CursorBlinkMs <= 0 ? -1 : Math.Clamp(copy.CursorBlinkMs, 200, 1200);
        return copy;
    }

    /// <summary>Value equality for mouse settings (the models are mutable classes).</summary>
    public static bool SameAs(MouseSettings? a, MouseSettings? b)
    {
        if (a is null || b is null)
        {
            return ReferenceEquals(a, b);
        }

        return a.Speed == b.Speed
            && a.EnhancePointerPrecision == b.EnhancePointerPrecision
            && a.DoubleClickMs == b.DoubleClickMs
            && a.SwapButtons == b.SwapButtons
            && a.ScrollLines == b.ScrollLines
            && a.ScrollOneScreen == b.ScrollOneScreen
            && a.PointerTrails == b.PointerTrails
            && a.HidePointerWhileTyping == b.HidePointerWhileTyping
            && a.SnapToDefaultButton == b.SnapToDefaultButton;
    }

    /// <summary>Value equality for keyboard settings.</summary>
    public static bool SameAs(KeyboardSettings? a, KeyboardSettings? b)
    {
        if (a is null || b is null)
        {
            return ReferenceEquals(a, b);
        }

        return a.RepeatDelay == b.RepeatDelay
            && a.RepeatRate == b.RepeatRate
            && a.CursorBlinkMs == b.CursorBlinkMs
            && a.StickyKeys == b.StickyKeys
            && a.FilterKeys == b.FilterKeys
            && a.ToggleKeys == b.ToggleKeys;
    }

    /// <summary>Short list of the differences between two mouse settings, for confirmation dialogs.</summary>
    public static IReadOnlyList<string> Describe(MouseSettings from, MouseSettings to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var lines = new List<string>();
        if (from.Speed != to.Speed)
        {
            lines.Add($"Pointer speed {SpeedToSliderText(from.Speed)} → {SpeedToSliderText(to.Speed)}");
        }

        if (from.EnhancePointerPrecision != to.EnhancePointerPrecision)
        {
            lines.Add($"Enhance pointer precision {OnOff(from.EnhancePointerPrecision)} → {OnOff(to.EnhancePointerPrecision)}");
        }

        if (from.DoubleClickMs != to.DoubleClickMs)
        {
            lines.Add($"Double-click speed {from.DoubleClickMs} ms → {to.DoubleClickMs} ms");
        }

        if (from.SwapButtons != to.SwapButtons)
        {
            lines.Add($"Swap primary buttons {OnOff(from.SwapButtons)} → {OnOff(to.SwapButtons)}");
        }

        if (from.ScrollLines != to.ScrollLines || from.ScrollOneScreen != to.ScrollOneScreen)
        {
            lines.Add($"Wheel scrolling: {ScrollText(from.ScrollLines, from.ScrollOneScreen)} → {ScrollText(to.ScrollLines, to.ScrollOneScreen)}");
        }

        if (from.PointerTrails != to.PointerTrails)
        {
            lines.Add($"Pointer trails {TrailsText(from.PointerTrails)} → {TrailsText(to.PointerTrails)}");
        }

        if (from.HidePointerWhileTyping != to.HidePointerWhileTyping)
        {
            lines.Add($"Hide pointer while typing {OnOff(from.HidePointerWhileTyping)} → {OnOff(to.HidePointerWhileTyping)}");
        }

        if (from.SnapToDefaultButton != to.SnapToDefaultButton)
        {
            lines.Add($"Snap pointer to default button {OnOff(from.SnapToDefaultButton)} → {OnOff(to.SnapToDefaultButton)}");
        }

        return lines;
    }

    /// <summary>Short list of the differences between two keyboard settings, for confirmation dialogs.</summary>
    public static IReadOnlyList<string> Describe(KeyboardSettings from, KeyboardSettings to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var lines = new List<string>();
        if (from.RepeatDelay != to.RepeatDelay)
        {
            lines.Add($"Repeat delay {RepeatDelayText(from.RepeatDelay)} → {RepeatDelayText(to.RepeatDelay)}");
        }

        if (from.RepeatRate != to.RepeatRate)
        {
            lines.Add($"Repeat rate {RepeatRateText(from.RepeatRate)} → {RepeatRateText(to.RepeatRate)}");
        }

        if (from.CursorBlinkMs != to.CursorBlinkMs)
        {
            lines.Add($"Cursor blink {CursorBlinkText(from.CursorBlinkMs)} → {CursorBlinkText(to.CursorBlinkMs)}");
        }

        if (from.StickyKeys != to.StickyKeys)
        {
            lines.Add($"Sticky Keys {OnOff(from.StickyKeys)} → {OnOff(to.StickyKeys)}");
        }

        if (from.FilterKeys != to.FilterKeys)
        {
            lines.Add($"Filter Keys {OnOff(from.FilterKeys)} → {OnOff(to.FilterKeys)}");
        }

        if (from.ToggleKeys != to.ToggleKeys)
        {
            lines.Add($"Toggle Keys {OnOff(from.ToggleKeys)} → {OnOff(to.ToggleKeys)}");
        }

        return lines;
    }

    private static string OnOff(bool value) => value ? "on" : "off";
}
