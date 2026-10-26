using PcChecker.Core.Settings;

namespace PcChecker.Core.Common;

/// <summary>A rectangle in device-independent pixels (a monitor's work area, or the overlay's size).</summary>
public readonly record struct OverlayRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>Pure placement math for the overlay window: corner → top-left position inside a monitor's work area.</summary>
public static class OverlayLayout
{
    /// <summary>Default distance from the screen edge.</summary>
    public const double DefaultMargin = 16;

    /// <summary>
    /// Returns where the top-left corner of the overlay should go. For <see cref="OverlayCorner.Custom"/> the saved
    /// X/Y is used, clamped so the whole overlay stays visible inside the work area when it fits.
    /// </summary>
    public static (double X, double Y) Place(OverlayCorner corner, double customX, double customY, OverlayRect workArea, double width, double height, double margin = DefaultMargin)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        margin = Math.Max(0, margin);

        double x, y;
        switch (corner)
        {
            case OverlayCorner.TopRight:
                x = workArea.Right - width - margin;
                y = workArea.Y + margin;
                break;
            case OverlayCorner.BottomLeft:
                x = workArea.X + margin;
                y = workArea.Bottom - height - margin;
                break;
            case OverlayCorner.BottomRight:
                x = workArea.Right - width - margin;
                y = workArea.Bottom - height - margin;
                break;
            case OverlayCorner.Custom:
                x = customX;
                y = customY;
                break;
            default:
                x = workArea.X + margin;
                y = workArea.Y + margin;
                break;
        }

        return Clamp(x, y, workArea, width, height);
    }

    /// <summary>Keeps the overlay inside the work area (the top-left edge wins when it is larger than the area).</summary>
    public static (double X, double Y) Clamp(double x, double y, OverlayRect workArea, double width, double height)
    {
        double maxX = Math.Max(workArea.X, workArea.Right - width);
        double maxY = Math.Max(workArea.Y, workArea.Bottom - height);
        return (Math.Clamp(x, workArea.X, maxX), Math.Clamp(y, workArea.Y, maxY));
    }

    /// <summary>Picks the corner a dragged overlay is closest to (used to describe a custom position).</summary>
    public static OverlayCorner NearestCorner(double x, double y, OverlayRect workArea, double width, double height)
    {
        double centerX = x + width / 2;
        double centerY = y + height / 2;
        bool right = centerX > workArea.X + workArea.Width / 2;
        bool bottom = centerY > workArea.Y + workArea.Height / 2;
        return (right, bottom) switch
        {
            (false, false) => OverlayCorner.TopLeft,
            (true, false) => OverlayCorner.TopRight,
            (false, true) => OverlayCorner.BottomLeft,
            _ => OverlayCorner.BottomRight,
        };
    }
}
