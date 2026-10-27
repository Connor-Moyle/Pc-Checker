using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace PcChecker.App.Controls;

/// <summary>
/// A Task Manager style usage graph: a grid, a filled line for <see cref="Values"/>, an optional darker band for
/// <see cref="ShadeValues"/> (kernel time) and an optional dashed second line (<see cref="SecondValues"/>, for example
/// disk writes next to reads). The newest value is at the right edge; NaN slots (no reading yet) are skipped. Colours
/// follow the theme unless the page sets them. Screen readers get it as an image with <see cref="Description"/> as help
/// text, so set <c>AutomationProperties.Name</c> and bind the description to the current value.
/// </summary>
public sealed class LiveGraph : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = Register(nameof(Values), typeof(IReadOnlyList<double>), null);
    public static readonly DependencyProperty SecondValuesProperty = Register(nameof(SecondValues), typeof(IReadOnlyList<double>), null);
    public static readonly DependencyProperty ShadeValuesProperty = Register(nameof(ShadeValues), typeof(IReadOnlyList<double>), null);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), typeof(double), 100d);
    public static readonly DependencyProperty StrokeProperty = Register(nameof(Stroke), typeof(Brush), Brushes.DeepSkyBlue);
    public static readonly DependencyProperty SecondStrokeProperty = Register(nameof(SecondStroke), typeof(Brush), null);
    public static readonly DependencyProperty GridBrushProperty = Register(nameof(GridBrush), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty ShowGridProperty = Register(nameof(ShowGrid), typeof(bool), true);
    public static readonly DependencyProperty StrokeThicknessProperty = Register(nameof(StrokeThickness), typeof(double), 1.5d);

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(LiveGraph), new PropertyMetadata(null));

    /// <summary>The theme brush key for the line ("AccentPurpleBrush"); the colour then follows theme changes.</summary>
    public static readonly DependencyProperty AccentKeyProperty =
        DependencyProperty.Register(nameof(AccentKey), typeof(string), typeof(LiveGraph), new PropertyMetadata(null, OnAccentKeyChanged));

    public LiveGraph()
    {
        SetResourceReference(StrokeProperty, "AccentCyanBrush");
        SetResourceReference(GridBrushProperty, "ControlStrokeColorDefaultBrush");
        SnapsToDevicePixels = true;
    }

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<double>? SecondValues { get => (IReadOnlyList<double>?)GetValue(SecondValuesProperty); set => SetValue(SecondValuesProperty, value); }
    public IReadOnlyList<double>? ShadeValues { get => (IReadOnlyList<double>?)GetValue(ShadeValuesProperty); set => SetValue(ShadeValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? SecondStroke { get => (Brush?)GetValue(SecondStrokeProperty); set => SetValue(SecondStrokeProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    /// <summary>What the graph shows right now ("CPU 12 %"), read out as the screen reader's help text.</summary>
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public string? AccentKey { get => (string?)GetValue(AccentKeyProperty); set => SetValue(AccentKeyProperty, value); }

    private static void OnAccentKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is string { Length: > 0 } key)
        {
            ((LiveGraph)d).SetResourceReference(StrokeProperty, key);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new LiveGraphAutomationPeer(this);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 60 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width < 4 || height < 4)
        {
            return;
        }

        var outline = new Pen(GridBrush, 1);
        outline.Freeze();
        dc.DrawRectangle(Brushes.Transparent, outline, new Rect(0.5, 0.5, width - 1, height - 1));
        if (ShowGrid)
        {
            DrawGrid(dc, width, height);
        }

        double max = Maximum > 0 && double.IsFinite(Maximum) ? Maximum : 100;
        if (ShadeValues is { Count: > 1 } shade)
        {
            DrawArea(dc, shade, max, width, height, WithOpacity(Stroke, 0.38), null);
        }

        if (Values is { Count: > 1 } values)
        {
            var pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            DrawArea(dc, values, max, width, height, WithOpacity(Stroke, 0.16), pen);
        }

        if (SecondValues is { Count: > 1 } second)
        {
            var pen = new Pen(SecondStroke ?? Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round, DashStyle = DashStyles.Dash };
            pen.Freeze();
            DrawArea(dc, second, max, width, height, null, pen);
        }
    }

    private void DrawGrid(DrawingContext dc, double width, double height)
    {
        var pen = new Pen(WithOpacity(GridBrush, 0.45), 1);
        pen.Freeze();
        int rows = height >= 120 ? 10 : 4;
        for (int i = 1; i < rows; i++)
        {
            double y = Math.Round(height * i / rows) + 0.5;
            dc.DrawLine(pen, new Point(1, y), new Point(width - 1, y));
        }

        int columns = width >= 240 ? 10 : 4;
        for (int i = 1; i < columns; i++)
        {
            double x = Math.Round(width * i / columns) + 0.5;
            dc.DrawLine(pen, new Point(x, 1), new Point(x, height - 1));
        }
    }

    private static void DrawArea(DrawingContext dc, IReadOnlyList<double> values, double max, double width, double height, Brush? fill, Pen? line)
    {
        var points = new List<Point>(values.Count);
        double inset = 1;
        for (int i = 0; i < values.Count; i++)
        {
            double v = values[i];
            if (!double.IsFinite(v))
            {
                continue;
            }

            double x = inset + (width - 2 * inset) * i / (values.Count - 1);
            double y = inset + (height - 2 * inset) * (1 - Math.Clamp(v / max, 0, 1));
            points.Add(new Point(x, y));
        }

        if (points.Count < 2)
        {
            return;
        }

        if (fill is not null)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, height - inset), true, true);
                ctx.PolyLineTo(points, false, true);
                ctx.LineTo(new Point(points[^1].X, height - inset), false, true);
            }

            area.Freeze();
            dc.DrawGeometry(fill, null, area);
        }

        if (line is not null)
        {
            var path = new StreamGeometry();
            using (var ctx = path.Open())
            {
                ctx.BeginFigure(points[0], false, false);
                ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
            }

            path.Freeze();
            dc.DrawGeometry(null, line, path);
        }
    }

    private static Brush WithOpacity(Brush brush, double opacity)
    {
        var copy = brush.CloneCurrentValue();
        copy.Opacity = opacity;
        copy.Freeze();
        return copy;
    }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(LiveGraph), new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}

/// <summary>Exposes a <see cref="LiveGraph"/> to screen readers as an image whose help text is the current value.</summary>
public sealed class LiveGraphAutomationPeer : FrameworkElementAutomationPeer
{
    public LiveGraphAutomationPeer(LiveGraph owner)
        : base(owner)
    {
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

    protected override string GetClassNameCore() => nameof(LiveGraph);

    protected override string GetHelpTextCore() => ((LiveGraph)Owner).Description ?? base.GetHelpTextCore();

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}
