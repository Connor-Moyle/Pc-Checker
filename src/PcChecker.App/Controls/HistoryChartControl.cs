using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PcChecker.Core.History;

namespace PcChecker.App.Controls;

/// <summary>
/// A line chart for one recorded metric over a time range: value labels on the left, time labels underneath, an
/// optional dashed limit line, breaks where nothing was recorded (PC off), and the nearest value under the mouse.
/// Drawn directly in <see cref="OnRender"/> so hundreds of points stay cheap. Set the brushes from theme resources.
/// </summary>
public sealed class HistoryChartControl : FrameworkElement
{
    private const double LeftAxis = 46;
    private const double RightPad = 8;
    private const double TopPad = 8;
    private const double BottomAxis = 22;
    private const double LabelSize = 11;

    public static readonly DependencyProperty PointsProperty = Register(nameof(Points), typeof(IReadOnlyList<HistoryPoint>), null);
    public static readonly DependencyProperty FromProperty = Register(nameof(From), typeof(DateTimeOffset), DateTimeOffset.MinValue);
    public static readonly DependencyProperty ToProperty = Register(nameof(To), typeof(DateTimeOffset), DateTimeOffset.MinValue);
    public static readonly DependencyProperty MinimumProperty = Register(nameof(Minimum), typeof(double), 0d);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), typeof(double), 100d);
    public static readonly DependencyProperty UnitProperty = Register(nameof(Unit), typeof(string), string.Empty);
    public static readonly DependencyProperty LimitProperty = Register(nameof(Limit), typeof(double), double.NaN);
    public static readonly DependencyProperty GapThresholdProperty = Register(nameof(GapThreshold), typeof(TimeSpan), TimeSpan.FromMinutes(5));
    public static readonly DependencyProperty StrokeProperty = Register(nameof(Stroke), typeof(Brush), new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)));
    public static readonly DependencyProperty AxisBrushProperty = Register(nameof(AxisBrush), typeof(Brush), new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)));
    public static readonly DependencyProperty TextBrushProperty = Register(nameof(TextBrush), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty LimitBrushProperty = Register(nameof(LimitBrush), typeof(Brush), new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16)));
    public static readonly DependencyProperty HoverBackgroundProperty = Register(nameof(HoverBackground), typeof(Brush), new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)));
    public static readonly DependencyProperty HoverForegroundProperty = Register(nameof(HoverForeground), typeof(Brush), Brushes.White);

    private Point? _mouse;

    public HistoryChartControl()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        MinHeight = 120;

        // Theme colours unless the page sets its own: the axis labels are small text, so they use the Caption colour,
        // which meets 4.5:1 on every theme; the line and the limit follow the accent palette.
        SetResourceReference(StrokeProperty, "AccentCyanBrush");
        SetResourceReference(LimitBrushProperty, "AccentOrangeBrush");
        SetResourceReference(TextBrushProperty, "CaptionTextBrush");
    }

    public IReadOnlyList<HistoryPoint>? Points { get => (IReadOnlyList<HistoryPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public DateTimeOffset From { get => (DateTimeOffset)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public DateTimeOffset To { get => (DateTimeOffset)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <summary>A value drawn as a dashed line (for example the warning temperature). NaN hides it.</summary>
    public double Limit { get => (double)GetValue(LimitProperty); set => SetValue(LimitProperty, value); }

    /// <summary>Neighbouring points further apart than this are not joined.</summary>
    public TimeSpan GapThreshold { get => (TimeSpan)GetValue(GapThresholdProperty); set => SetValue(GapThresholdProperty, value); }

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush AxisBrush { get => (Brush)GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush LimitBrush { get => (Brush)GetValue(LimitBrushProperty); set => SetValue(LimitBrushProperty, value); }
    public Brush HoverBackground { get => (Brush)GetValue(HoverBackgroundProperty); set => SetValue(HoverBackgroundProperty, value); }
    public Brush HoverForeground { get => (Brush)GetValue(HoverForegroundProperty); set => SetValue(HoverForegroundProperty, value); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        // A transparent background so the whole area receives mouse moves for the hover value.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        var plot = new Rect(LeftAxis, TopPad, Math.Max(0, width - LeftAxis - RightPad), Math.Max(0, height - TopPad - BottomAxis));
        if (plot.Width < 20 || plot.Height < 20 || To <= From)
        {
            return;
        }

        double min = Minimum, max = Maximum > Minimum ? Maximum : Minimum + 1;
        var axisPen = Frozen(new Pen(AxisBrush, 1));
        DrawValueAxis(dc, plot, min, max, axisPen);
        DrawTimeAxis(dc, plot);

        var points = Points ?? [];
        if (points.Count == 0)
        {
            var empty = Text(Localization.Loc.T("Nothing recorded in this period"), TextBrush, 12);
            dc.DrawText(empty, new Point(plot.Left + (plot.Width - empty.Width) / 2, plot.Top + (plot.Height - empty.Height) / 2));
            return;
        }

        if (!double.IsNaN(Limit) && Limit > min && Limit < max)
        {
            var limitPen = Frozen(new Pen(LimitBrush, 1) { DashStyle = DashStyles.Dash });
            double y = Y(Limit, plot, min, max);
            dc.DrawLine(limitPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        DrawSeries(dc, plot, points, min, max);
        DrawHover(dc, plot, points, min, max);
    }

    private void DrawValueAxis(DrawingContext dc, Rect plot, double min, double max, Pen pen)
    {
        for (int i = 0; i <= 2; i++)
        {
            double value = min + (max - min) * i / 2;
            double y = Y(value, plot, min, max);
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(FormatValue(value, max - min), TextBrush);
            dc.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
        }
    }

    private void DrawTimeAxis(DrawingContext dc, Rect plot)
    {
        var span = To - From;
        const int ticks = 4;
        for (int i = 0; i < ticks; i++)
        {
            var time = From + TimeSpan.FromTicks(span.Ticks * i / (ticks - 1));
            var label = Text(FormatTime(time, span), TextBrush);
            double x = X(time, plot);
            double left = i == 0 ? x : i == ticks - 1 ? x - label.Width : x - label.Width / 2;
            dc.DrawText(label, new Point(left, plot.Bottom + 4));
        }
    }

    private void DrawSeries(DrawingContext dc, Rect plot, IReadOnlyList<HistoryPoint> points, double min, double max)
    {
        var linePen = Frozen(new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        var fill = Stroke.CloneCurrentValue();
        fill.Opacity = 0.16;
        fill.Freeze();
        foreach (var segment in HistoryMath.SplitAtGaps(points, GapThreshold))
        {
            if (segment.Count == 1)
            {
                dc.DrawEllipse(Stroke, null, new Point(X(segment[0].Time, plot), Y(segment[0].Value, plot, min, max)), 2, 2);
                continue;
            }

            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var lc = line.Open())
            using (var ac = area.Open())
            {
                var first = new Point(X(segment[0].Time, plot), Y(segment[0].Value, plot, min, max));
                lc.BeginFigure(first, false, false);
                ac.BeginFigure(new Point(first.X, plot.Bottom), true, true);
                ac.LineTo(first, false, false);
                var rest = segment.Skip(1).Select(p => new Point(X(p.Time, plot), Y(p.Value, plot, min, max))).ToList();
                lc.PolyLineTo(rest, true, true);
                ac.PolyLineTo(rest, false, false);
                ac.LineTo(new Point(rest[^1].X, plot.Bottom), false, false);
            }

            line.Freeze();
            area.Freeze();
            dc.DrawGeometry(fill, null, area);
            dc.DrawGeometry(null, linePen, line);
        }
    }

    private void DrawHover(DrawingContext dc, Rect plot, IReadOnlyList<HistoryPoint> points, double min, double max)
    {
        if (_mouse is not { } mouse || !plot.Contains(mouse))
        {
            return;
        }

        var time = From + TimeSpan.FromTicks((long)((To - From).Ticks * ((mouse.X - plot.Left) / plot.Width)));
        if (HistoryMath.Nearest(points, time) is not { } nearest)
        {
            return;
        }

        var at = new Point(X(nearest.Time, plot), Y(nearest.Value, plot, min, max));
        dc.DrawLine(Frozen(new Pen(TextBrush, 1) { DashStyle = DashStyles.Dot }), new Point(at.X, plot.Top), new Point(at.X, plot.Bottom));
        dc.DrawEllipse(Stroke, Frozen(new Pen(HoverForeground, 1.5)), at, 4, 4);

        var label = Text($"{FormatValue(nearest.Value, 0)} {Unit}".Trim() + "  ·  "
            + nearest.Time.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture), HoverForeground, 12);
        double boxWidth = label.Width + 16, boxHeight = label.Height + 8;
        double left = at.X + 10 + boxWidth > plot.Right ? at.X - 10 - boxWidth : at.X + 10;
        var box = new Rect(Math.Max(plot.Left, left), plot.Top + 2, boxWidth, boxHeight);
        dc.DrawRoundedRectangle(HoverBackground, null, box, 4, 4);
        dc.DrawText(label, new Point(box.Left + 8, box.Top + 4));
    }

    private double X(DateTimeOffset time, Rect plot) =>
        plot.Left + plot.Width * Math.Clamp((time - From).Ticks / (double)(To - From).Ticks, 0, 1);

    private static double Y(double value, Rect plot, double min, double max) =>
        plot.Bottom - plot.Height * Math.Clamp((value - min) / (max - min), 0, 1);

    private static string FormatValue(double value, double range) =>
        value.ToString(range is > 0 and < 10 || (range == 0 && Math.Abs(value) < 10) ? "0.#" : "0", CultureInfo.CurrentCulture);

    private static string FormatTime(DateTimeOffset time, TimeSpan span)
    {
        var local = time.ToLocalTime();
        string format = span <= TimeSpan.FromHours(25) ? "HH:mm" : span <= TimeSpan.FromDays(8) ? "ddd HH:mm" : "d MMM";
        return local.ToString(format, CultureInfo.CurrentCulture);
    }

    private FormattedText Text(string text, Brush brush, double size = LabelSize)
    {
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(HistoryChartControl),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}
