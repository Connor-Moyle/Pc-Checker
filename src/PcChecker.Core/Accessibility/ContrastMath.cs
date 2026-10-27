namespace PcChecker.Core.Accessibility;

/// <summary>
/// WCAG 2.x contrast: relative luminance of sRGB colours and the contrast ratio between two of them (1:1 to 21:1).
/// Level AA asks for 4.5:1 for normal text, and 3:1 for large text and for the parts of graphics people need to see
/// (chart lines, status dots, focus marks).
/// </summary>
public static class ContrastMath
{
    /// <summary>AA minimum for normal text (below 18 pt, or 14 pt bold).</summary>
    public const double TextMinimum = 4.5;

    /// <summary>AA minimum for large text and for meaningful graphics such as chart lines and status dots.</summary>
    public const double GraphicsMinimum = 3.0;

    /// <summary>Relative luminance from 0 (black) to 1 (white); alpha is ignored.</summary>
    public static double RelativeLuminance(ThemeColor color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    /// <summary>
    /// Contrast between <paramref name="foreground"/> and an opaque <paramref name="background"/>. A see-through
    /// foreground (text brushes such as #9E000000) is first blended over the background, as it is on screen.
    /// </summary>
    public static double Ratio(ThemeColor foreground, ThemeColor background)
    {
        if (!background.IsOpaque)
        {
            throw new ArgumentException("The background must be opaque; blend it over the surface below first.", nameof(background));
        }

        double a = RelativeLuminance(foreground.Over(background));
        double b = RelativeLuminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>True when the pair reaches the AA minimum for normal text.</summary>
    public static bool MeetsText(ThemeColor foreground, ThemeColor background) => Ratio(foreground, background) >= TextMinimum;

    /// <summary>True when the pair reaches the AA minimum for large text and meaningful graphics.</summary>
    public static bool MeetsGraphics(ThemeColor foreground, ThemeColor background) => Ratio(foreground, background) >= GraphicsMinimum;

    private static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
