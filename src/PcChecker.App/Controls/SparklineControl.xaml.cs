using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PcChecker.App.Controls;

/// <summary>Tiny line chart for a series of doubles (sensor history). Auto-scales to the data range.</summary>
public partial class SparklineControl : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(SparklineControl), new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(SparklineControl),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF))));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(SparklineControl),
            new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0x33, 0x4C, 0xC2, 0xFF))));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(SparklineControl), new PropertyMetadata(1.5));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double?), typeof(SparklineControl), new PropertyMetadata(null, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double?), typeof(SparklineControl), new PropertyMetadata(null, OnRangeChanged));

    private static readonly DependencyPropertyKey LineGeometryKey =
        DependencyProperty.RegisterReadOnly(nameof(LineGeometry), typeof(Geometry), typeof(SparklineControl), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey FillGeometryKey =
        DependencyProperty.RegisterReadOnly(nameof(FillGeometry), typeof(Geometry), typeof(SparklineControl), new PropertyMetadata(null));

    public static readonly DependencyProperty LineGeometryProperty = LineGeometryKey.DependencyProperty;
    public static readonly DependencyProperty FillGeometryProperty = FillGeometryKey.DependencyProperty;

    public SparklineControl()
    {
        InitializeComponent();

        // Follows the theme (light, dark, high contrast) unless the page sets a stroke.
        SetResourceReference(StrokeProperty, "AccentCyanBrush");
    }

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double? Minimum { get => (double?)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double? Maximum { get => (double?)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Geometry? LineGeometry => (Geometry?)GetValue(LineGeometryProperty);
    public Geometry? FillGeometry => (Geometry?)GetValue(FillGeometryProperty);

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SparklineControl)d;
        if (e.OldValue is INotifyCollectionChanged oldChanged)
        {
            oldChanged.CollectionChanged -= control.OnCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged newChanged)
        {
            newChanged.CollectionChanged += control.OnCollectionChanged;
        }

        control.Rebuild();
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SparklineControl)d).Rebuild();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var values = new List<double>();
        if (ItemsSource is not null)
        {
            foreach (var item in ItemsSource)
            {
                double v = item switch
                {
                    double x => x,
                    float f => f,
                    int i => i,
                    long l => l,
                    decimal m => (double)m,
                    _ => double.NaN,
                };
                if (!double.IsNaN(v))
                {
                    values.Add(v);
                }
            }
        }

        double width = ActualWidth, height = ActualHeight;
        if (values.Count < 2 || width <= 0 || height <= 0)
        {
            SetValue(LineGeometryKey, Geometry.Empty);
            SetValue(FillGeometryKey, Geometry.Empty);
            return;
        }

        double min = Minimum ?? values.Min();
        double max = Maximum ?? values.Max();
        if (max - min < 1e-9)
        {
            max = min + 1;
        }

        const double pad = 2;
        var points = new List<Point>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            double x = pad + (width - 2 * pad) * i / (values.Count - 1);
            double y = pad + (height - 2 * pad) * (1 - Math.Clamp((values[i] - min) / (max - min), 0, 1));
            points.Add(new Point(x, y));
        }

        var line = new PathFigure { StartPoint = points[0], IsClosed = false, IsFilled = false };
        line.Segments.Add(new PolyLineSegment(points.Skip(1), true));
        var lineGeometry = new PathGeometry();
        lineGeometry.Figures.Add(line);
        lineGeometry.Freeze();

        var fill = new PathFigure { StartPoint = new Point(points[0].X, height), IsClosed = true, IsFilled = true };
        fill.Segments.Add(new PolyLineSegment(points, false));
        fill.Segments.Add(new LineSegment(new Point(points[^1].X, height), false));
        var fillGeometry = new PathGeometry();
        fillGeometry.Figures.Add(fill);
        fillGeometry.Freeze();

        SetValue(LineGeometryKey, lineGeometry);
        SetValue(FillGeometryKey, fillGeometry);
    }
}
