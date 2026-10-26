namespace PcChecker.Core.Displays;

/// <summary>One display's rectangle in desktop coordinates (pixels), as Windows arranges it.</summary>
public sealed record ArrangementInput(string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary, string Label);

/// <summary>One display's rectangle scaled to fit a preview canvas (device-independent units).</summary>
public sealed record ArrangementRect(string DeviceName, int Number, double X, double Y, double Width, double Height, bool IsPrimary, string Label);

/// <summary>
/// Pure maths for the "arrangement" preview: scales the desktop (which may span negative coordinates) into a canvas
/// of the requested size while keeping every display proportional and centred.
/// </summary>
public static class ArrangementLayout
{
    /// <summary>
    /// Fits <paramref name="displays"/> into a canvas of <paramref name="canvasWidth"/> × <paramref name="canvasHeight"/>
    /// with <paramref name="padding"/> on every side. Numbers follow the input order (1-based), like Windows does.
    /// </summary>
    public static IReadOnlyList<ArrangementRect> Compute(IEnumerable<ArrangementInput> displays, double canvasWidth, double canvasHeight, double padding = 12, double gap = 3)
    {
        var list = (displays ?? []).Where(d => d.Width > 0 && d.Height > 0).ToList();
        if (list.Count == 0 || canvasWidth <= 0 || canvasHeight <= 0)
        {
            return [];
        }

        long minX = list.Min(d => (long)d.X);
        long minY = list.Min(d => (long)d.Y);
        long maxX = list.Max(d => (long)d.X + d.Width);
        long maxY = list.Max(d => (long)d.Y + d.Height);
        double desktopWidth = Math.Max(1, maxX - minX);
        double desktopHeight = Math.Max(1, maxY - minY);

        double availableWidth = Math.Max(1, canvasWidth - 2 * padding);
        double availableHeight = Math.Max(1, canvasHeight - 2 * padding);
        double scale = Math.Min(availableWidth / desktopWidth, availableHeight / desktopHeight);

        double offsetX = padding + (availableWidth - desktopWidth * scale) / 2;
        double offsetY = padding + (availableHeight - desktopHeight * scale) / 2;

        var result = new List<ArrangementRect>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var d = list[i];
            double x = offsetX + (d.X - minX) * scale + gap / 2;
            double y = offsetY + (d.Y - minY) * scale + gap / 2;
            double w = Math.Max(1, d.Width * scale - gap);
            double h = Math.Max(1, d.Height * scale - gap);
            result.Add(new ArrangementRect(d.DeviceName, i + 1, Math.Round(x, 2), Math.Round(y, 2), Math.Round(w, 2), Math.Round(h, 2), d.IsPrimary, d.Label));
        }

        return result;
    }

    /// <summary>Builds the inputs from display records, in the given order.</summary>
    public static IReadOnlyList<ArrangementInput> FromDisplays(IEnumerable<DisplayInfo> displays) =>
        (displays ?? []).Select(d => new ArrangementInput(d.DeviceName, d.PositionX, d.PositionY, d.Current.Width, d.Current.Height, d.IsPrimary, d.FriendlyName)).ToList();

    /// <summary>The 1-based number Windows would show for a device ("Identify"), or 0 when it is not in the list.</summary>
    public static int NumberOf(IEnumerable<DisplayInfo> displays, string deviceName)
    {
        int i = 0;
        foreach (var d in displays ?? [])
        {
            i++;
            if (string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }
}
