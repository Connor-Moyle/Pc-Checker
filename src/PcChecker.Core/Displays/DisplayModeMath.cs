namespace PcChecker.Core.Displays;

/// <summary>Pure helpers for display modes: dedupe/sort, closest match, aspect ratio text and DPI → scale.</summary>
public static class DisplayModeMath
{
    /// <summary>Removes duplicates and sorts by resolution (largest first) then refresh rate (highest first).</summary>
    public static IReadOnlyList<DisplayMode> Normalize(IEnumerable<DisplayMode> modes) =>
        (modes ?? [])
            .Where(m => m.Width > 0 && m.Height > 0 && m.RefreshHz > 0)
            .Distinct()
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.RefreshHz)
            .ThenByDescending(m => m.BitsPerPixel)
            .ToList();

    /// <summary>Distinct resolutions in the list, largest first.</summary>
    public static IReadOnlyList<(int Width, int Height)> Resolutions(IEnumerable<DisplayMode> modes) =>
        Normalize(modes).Select(m => (m.Width, m.Height)).Distinct().ToList();

    /// <summary>Refresh rates available at one resolution, highest first.</summary>
    public static IReadOnlyList<int> RefreshRatesFor(IEnumerable<DisplayMode> modes, int width, int height) =>
        Normalize(modes).Where(m => m.Width == width && m.Height == height).Select(m => m.RefreshHz).Distinct().OrderByDescending(h => h).ToList();

    /// <summary>The supported mode nearest to <paramref name="target"/> (same resolution preferred, then nearest refresh).</summary>
    public static DisplayMode? Closest(IEnumerable<DisplayMode> modes, DisplayMode target)
    {
        var list = Normalize(modes);
        if (list.Count == 0)
        {
            return null;
        }

        return list
            .OrderBy(m => Math.Abs((long)m.Width * m.Height - (long)target.Width * target.Height))
            .ThenBy(m => Math.Abs(m.RefreshHz - target.RefreshHz))
            .ThenBy(m => Math.Abs(m.BitsPerPixel - target.BitsPerPixel))
            .First();
    }

    /// <summary>"16:9", "21:9", "4:3"… with a few friendly aliases for common ratios.</summary>
    public static string AspectRatioText(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return "?";
        }

        double ratio = (double)width / height;
        foreach (var (value, text) in KnownRatios)
        {
            if (Math.Abs(ratio - value) < 0.02)
            {
                return text;
            }
        }

        int g = Gcd(width, height);
        return $"{width / g}:{height / g}";
    }

    /// <summary>Windows scale percent from a DPI value (96 = 100 %).</summary>
    public static double ScaleFromDpi(double dpi) => dpi <= 0 ? 100 : Math.Round(dpi / 96.0 * 100, 0);

    private static readonly (double Value, string Text)[] KnownRatios =
    [
        (16.0 / 9, "16:9"), (16.0 / 10, "16:10"), (21.0 / 9, "21:9"), (2560.0 / 1080, "21:9"), (3440.0 / 1440, "21:9"),
        (32.0 / 9, "32:9"), (4.0 / 3, "4:3"), (5.0 / 4, "5:4"), (3.0 / 2, "3:2"), (1.0, "1:1"),
    ];

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return Math.Max(1, a);
    }
}
