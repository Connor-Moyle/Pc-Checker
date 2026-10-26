using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcChecker.App.Converters;

namespace PcChecker.App.Controls;

/// <summary>Half-circle gauge from <see cref="Minimum"/> to <see cref="Maximum"/> that changes colour at <see cref="WarnAt"/>/<see cref="CritAt"/>.</summary>
public partial class GaugeControl : UserControl
{
    private const double CenterX = 60, CenterY = 70, Radius = 50;

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(GaugeControl), new PropertyMetadata(0d, OnVisualChanged));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(GaugeControl), new PropertyMetadata(0d, OnVisualChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(GaugeControl), new PropertyMetadata(100d, OnVisualChanged));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(GaugeControl), new PropertyMetadata("%"));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(GaugeControl), new PropertyMetadata(null));

    public static readonly DependencyProperty WarnAtProperty =
        DependencyProperty.Register(nameof(WarnAt), typeof(double), typeof(GaugeControl), new PropertyMetadata(80d, OnVisualChanged));

    public static readonly DependencyProperty CritAtProperty =
        DependencyProperty.Register(nameof(CritAt), typeof(double), typeof(GaugeControl), new PropertyMetadata(90d, OnVisualChanged));

    public static readonly DependencyProperty ValueFormatProperty =
        DependencyProperty.Register(nameof(ValueFormat), typeof(string), typeof(GaugeControl), new PropertyMetadata("0", OnVisualChanged));

    private static readonly DependencyPropertyKey TrackGeometryKey =
        DependencyProperty.RegisterReadOnly(nameof(TrackGeometry), typeof(Geometry), typeof(GaugeControl), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey ValueGeometryKey =
        DependencyProperty.RegisterReadOnly(nameof(ValueGeometry), typeof(Geometry), typeof(GaugeControl), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey ValueBrushKey =
        DependencyProperty.RegisterReadOnly(nameof(ValueBrush), typeof(Brush), typeof(GaugeControl), new PropertyMetadata(TemperatureToBrushConverter.Normal));

    private static readonly DependencyPropertyKey ValueTextKey =
        DependencyProperty.RegisterReadOnly(nameof(ValueText), typeof(string), typeof(GaugeControl), new PropertyMetadata("0"));

    public static readonly DependencyProperty TrackGeometryProperty = TrackGeometryKey.DependencyProperty;
    public static readonly DependencyProperty ValueGeometryProperty = ValueGeometryKey.DependencyProperty;
    public static readonly DependencyProperty ValueBrushProperty = ValueBrushKey.DependencyProperty;
    public static readonly DependencyProperty ValueTextProperty = ValueTextKey.DependencyProperty;

    public GaugeControl()
    {
        InitializeComponent();
        Update();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string? Unit { get => (string?)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double WarnAt { get => (double)GetValue(WarnAtProperty); set => SetValue(WarnAtProperty, value); }
    public double CritAt { get => (double)GetValue(CritAtProperty); set => SetValue(CritAtProperty, value); }
    public string ValueFormat { get => (string)GetValue(ValueFormatProperty); set => SetValue(ValueFormatProperty, value); }
    public Geometry? TrackGeometry => (Geometry?)GetValue(TrackGeometryProperty);
    public Geometry? ValueGeometry => (Geometry?)GetValue(ValueGeometryProperty);
    public Brush ValueBrush => (Brush)GetValue(ValueBrushProperty);
    public string ValueText => (string)GetValue(ValueTextProperty);

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((GaugeControl)d).Update();

    private void Update()
    {
        double min = Minimum, max = Maximum;
        if (max <= min)
        {
            max = min + 1;
        }

        double fraction = Math.Clamp((Value - min) / (max - min), 0, 1);
        SetValue(TrackGeometryKey, BuildArc(1));
        SetValue(ValueGeometryKey, fraction <= 0 ? Geometry.Empty : BuildArc(fraction));
        SetValue(ValueBrushKey, Value >= CritAt ? TemperatureToBrushConverter.Hot : Value >= WarnAt ? TemperatureToBrushConverter.Warm : TemperatureToBrushConverter.Normal);
        SetValue(ValueTextKey, double.IsNaN(Value) ? "—" : Value.ToString(ValueFormat, System.Globalization.CultureInfo.CurrentCulture));
    }

    /// <summary>Builds an arc from 180° (left) sweeping clockwise through the top by fraction × 180°.</summary>
    private static Geometry BuildArc(double fraction)
    {
        double startAngle = Math.PI;
        double endAngle = Math.PI - fraction * Math.PI;
        var start = new Point(CenterX + Radius * Math.Cos(startAngle), CenterY - Radius * Math.Sin(startAngle));
        var end = new Point(CenterX + Radius * Math.Cos(endAngle), CenterY - Radius * Math.Sin(endAngle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(Radius, Radius), 0, false, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }
}
