namespace PcChecker.Core.Scheduling;

/// <summary>A rectangle in device-independent pixels (WPF units).</summary>
public readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>Which screen edge the taskbar (and so the tray) sits on.</summary>
public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>
/// Places the tray popup next to the notification area: above a bottom taskbar, below a top one, beside a side one,
/// horizontally near the click, and always fully inside the work area. Pure geometry so it is unit-tested.
/// </summary>
public static class TrayPopupPlacement
{
    public const double DefaultMargin = 12;

    /// <summary>
    /// Infers the taskbar edge from where the click happened relative to the work area (the screen minus the taskbar).
    /// A click inside the work area (the tray overflow flyout) is treated as a bottom taskbar, the Windows default.
    /// </summary>
    public static TaskbarEdge InferEdge(ScreenRect workArea, double cursorX, double cursorY)
    {
        if (cursorY >= workArea.Bottom)
        {
            return TaskbarEdge.Bottom;
        }

        if (cursorY < workArea.Top)
        {
            return TaskbarEdge.Top;
        }

        if (cursorX < workArea.Left)
        {
            return TaskbarEdge.Left;
        }

        return cursorX >= workArea.Right ? TaskbarEdge.Right : TaskbarEdge.Bottom;
    }

    /// <summary>Returns the popup's top-left corner.</summary>
    public static (double Left, double Top) Compute(ScreenRect workArea, double cursorX, double cursorY, double popupWidth, double popupHeight, double margin = DefaultMargin)
    {
        double width = Math.Max(1, popupWidth);
        double height = Math.Max(1, popupHeight);
        double left, top;
        switch (InferEdge(workArea, cursorX, cursorY))
        {
            case TaskbarEdge.Top:
                left = cursorX - (width / 2);
                top = workArea.Top + margin;
                break;
            case TaskbarEdge.Left:
                left = workArea.Left + margin;
                top = cursorY - (height / 2);
                break;
            case TaskbarEdge.Right:
                left = workArea.Right - width - margin;
                top = cursorY - (height / 2);
                break;
            default:
                left = cursorX - (width / 2);
                top = workArea.Bottom - height - margin;
                break;
        }

        return (Clamp(left, workArea.Left + margin, workArea.Right - width - margin),
                Clamp(top, workArea.Top + margin, workArea.Bottom - height - margin));
    }

    // When the popup is wider than the space, pin it to the start edge instead of throwing like Math.Clamp would.
    private static double Clamp(double value, double min, double max) => max < min ? min : Math.Min(Math.Max(value, min), max);
}
