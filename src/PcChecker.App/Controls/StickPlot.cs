using System.Windows;
using System.Windows.Media;

namespace PcChecker.App.Controls;

/// <summary>
/// A controller stick drawn as a circle: the outer edge (full travel), a dashed dead-zone ring, cross hairs, the
/// recent readings as a faint trail and the current position as a dot. Positions are -1..1 with Y pointing up.
/// Colours come from the theme, so it reads in light and dark mode.
/// </summary>
public sealed class StickPlot : FrameworkElement
{
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(Point), typeof(StickPlot), new FrameworkPropertyMetadata(default(Point), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrailProperty = DependencyProperty.Register(
        nameof(Trail), typeof(IReadOnlyList<Point>), typeof(StickPlot), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DeadzoneProperty = DependencyProperty.Register(
        nameof(Deadzone), typeof(double), typeof(StickPlot), new FrameworkPropertyMetadata(0.1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsPressedProperty = DependencyProperty.Register(
        nameof(IsPressed), typeof(bool), typeof(StickPlot), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The current stick position (-1..1, Y up).</summary>
    public Point Position
    {
        get => (Point)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>Recent positions, oldest first.</summary>
    public IReadOnlyList<Point>? Trail
    {
        get => (IReadOnlyList<Point>?)GetValue(TrailProperty);
        set => SetValue(TrailProperty, value);
    }

    /// <summary>Radius of the dead-zone ring (0..1).</summary>
    public double Deadzone
    {
        get => (double)GetValue(DeadzoneProperty);
        set => SetValue(DeadzoneProperty, value);
    }

    /// <summary>True while the stick is clicked in; the dot is drawn larger.</summary>
    public bool IsPressed
    {
        get => (bool)GetValue(IsPressedProperty);
        set => SetValue(IsPressedProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double size = Math.Min(
            double.IsInfinity(availableSize.Width) ? 180 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 180 : availableSize.Height);
        return new Size(size, size);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 4)
        {
            return;
        }

        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = size / 2 - 6;
        var stroke = Brush("ControlStrokeColorDefaultBrush", Brushes.Gray);
        var muted = Brush("TextFillColorTertiaryBrush", Brushes.Gray);
        var accent = Brush("AccentBlueBrush", Brushes.DodgerBlue);
        var fill = Brush("ControlFillColorDefaultBrush", Brushes.Transparent);

        dc.DrawEllipse(fill, new Pen(stroke, 1.5), centre, radius, radius);
        var thin = new Pen(muted, 0.75);
        dc.DrawLine(thin, new Point(centre.X - radius, centre.Y), new Point(centre.X + radius, centre.Y));
        dc.DrawLine(thin, new Point(centre.X, centre.Y - radius), new Point(centre.X, centre.Y + radius));

        double dead = Math.Clamp(Deadzone, 0, 1) * radius;
        if (dead > 1)
        {
            var dashed = new Pen(muted, 1) { DashStyle = DashStyles.Dash };
            dc.DrawEllipse(null, dashed, centre, dead, dead);
        }

        if (Trail is { Count: > 1 } trail)
        {
            // One path through the recent readings: cheap to draw even with a thousand points.
            var trailBrush = accent.Clone();
            trailBrush.Opacity = 0.45;
            trailBrush.Freeze();
            var path = new StreamGeometry();
            using (var context = path.Open())
            {
                context.BeginFigure(ToScreen(trail[0], centre, radius), false, false);
                for (int i = 1; i < trail.Count; i++)
                {
                    context.LineTo(ToScreen(trail[i], centre, radius), true, false);
                }
            }

            path.Freeze();
            dc.DrawGeometry(null, new Pen(trailBrush, 1.5) { LineJoin = PenLineJoin.Round }, path);
        }

        double dot = IsPressed ? 9 : 6;
        dc.DrawEllipse(accent, new Pen(Brush("TextFillColorPrimaryBrush", Brushes.White), 1.5), ToScreen(Position, centre, radius), dot, dot);
    }

    private static Point ToScreen(Point p, Point centre, double radius) =>
        new(centre.X + Math.Clamp(p.X, -1.5, 1.5) * radius, centre.Y - Math.Clamp(p.Y, -1.5, 1.5) * radius);

    private Brush Brush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}
