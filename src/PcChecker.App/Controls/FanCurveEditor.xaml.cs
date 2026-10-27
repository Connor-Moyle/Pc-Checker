using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PcChecker.App.Services;
using PcChecker.Core.Accessibility;
using PcChecker.Core.Fans;

namespace PcChecker.App.Controls;

/// <summary>
/// Interactive fan curve: temperature (0–100 °C) across, fan speed (0–100 %) up. Drag a point to move it,
/// double-click empty space to add one, double-click a point to remove it. From the keyboard: Left and Right pick a
/// point, Up and Down change its fan speed (Shift for 5 %), Ctrl with Left or Right moves it along the temperature
/// axis, Insert adds a point after it and Delete removes it. A dashed marker shows where the current temperature
/// lands on the curve. Edits are written straight back into <see cref="Points"/>.
/// </summary>
public partial class FanCurveEditor : UserControl
{
    private const double PadLeft = 38, PadRight = 14, PadTop = 22, PadBottom = 24, HitRadius = 9;

    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(nameof(Points), typeof(IList), typeof(FanCurveEditor), new PropertyMetadata(null, OnPointsChanged));

    public static readonly DependencyProperty CurrentTemperatureProperty =
        DependencyProperty.Register(nameof(CurrentTemperature), typeof(double?), typeof(FanCurveEditor), new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty IsReadOnlyProperty =
        DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(FanCurveEditor), new PropertyMetadata(false, OnVisualChanged));

    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(FanCurveEditor),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)), OnVisualChanged));

    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)));
    private static readonly Brush PointFill = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));

    private int _dragIndex = -1;
    private int _keyIndex = -1;
    private bool _suppressRedraw;

    public FanCurveEditor()
    {
        InitializeComponent();

        // Theme colours: follow light, dark and high contrast unless the page sets its own accent.
        SetResourceReference(AccentProperty, AccentPalette.ResourceKey(AccentRole.Cyan));
        IsKeyboardFocusedChanged += (_, _) => Redraw();
    }

    // Axis labels are small text, so they use the Caption colour, which meets 4.5:1 in every theme.
    private Brush AxisTextBrush => TryFindResource(AccentPalette.CaptionTextKey) as Brush ?? Brushes.Gray;

    private static Brush MarkerBrush => ThemeColors.Accent(AccentRole.Yellow);

    /// <summary>The curve points; an <see cref="ObservableCollection{T}"/> of <see cref="FanCurvePoint"/> redraws automatically.</summary>
    public IList? Points
    {
        get => (IList?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double? CurrentTemperature
    {
        get => (double?)GetValue(CurrentTemperatureProperty);
        set => SetValue(CurrentTemperatureProperty, value);
    }

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>Raised after the user moved, added or removed a point.</summary>
    public event EventHandler? PointsEdited;

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (FanCurveEditor)d;
        if (e.OldValue is INotifyCollectionChanged oldChanged)
        {
            oldChanged.CollectionChanged -= editor.OnCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged newChanged)
        {
            newChanged.CollectionChanged += editor.OnCollectionChanged;
        }

        editor.Redraw();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((FanCurveEditor)d).Redraw();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressRedraw)
        {
            Redraw();
        }
    }

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private List<FanCurvePoint> CurrentPoints()
    {
        var list = new List<FanCurvePoint>();
        if (Points is not null)
        {
            foreach (var item in Points)
            {
                if (item is FanCurvePoint p)
                {
                    list.Add(p);
                }
            }
        }

        return list;
    }

    private double PlotWidth => Math.Max(1, Surface.ActualWidth - PadLeft - PadRight);
    private double PlotHeight => Math.Max(1, Surface.ActualHeight - PadTop - PadBottom);

    private Point ToCanvas(double tempC, double percent) =>
        new(PadLeft + PlotWidth * Math.Clamp(tempC, 0, 100) / 100, PadTop + PlotHeight * (1 - Math.Clamp(percent, 0, 100) / 100));

    private (double TempC, double Percent) FromCanvas(Point p) =>
        (Math.Clamp((p.X - PadLeft) / PlotWidth * 100, 0, 100), Math.Clamp((1 - (p.Y - PadTop) / PlotHeight) * 100, 0, 100));

    private void Redraw()
    {
        if (Surface is null || Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0)
        {
            return;
        }

        Surface.Children.Clear();
        DrawGrid();

        var points = CurrentPoints().OrderBy(p => p.TemperatureC).ToList();
        if (points.Count == 0)
        {
            return;
        }

        // Curve: flat before the first point, linear between, flat after the last.
        var figure = new PathFigure { StartPoint = ToCanvas(0, points[0].Percent), IsClosed = false };
        foreach (var p in points)
        {
            figure.Segments.Add(new LineSegment(ToCanvas(p.TemperatureC, p.Percent), true));
        }

        figure.Segments.Add(new LineSegment(ToCanvas(100, points[^1].Percent), true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        var fillFigure = figure.Clone();
        fillFigure.Segments.Add(new LineSegment(ToCanvas(100, 0), false));
        fillFigure.Segments.Add(new LineSegment(ToCanvas(0, 0), false));
        fillFigure.IsClosed = true;
        var fillGeometry = new PathGeometry();
        fillGeometry.Figures.Add(fillFigure);
        var fillBrush = Accent.Clone();
        fillBrush.Opacity = 0.12;
        Surface.Children.Add(new Path { Data = fillGeometry, Fill = fillBrush, IsHitTestVisible = false });
        Surface.Children.Add(new Path { Data = geometry, Stroke = Accent, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false });

        DrawMarker(points);

        var unsorted = CurrentPoints();
        for (int i = 0; i < unsorted.Count; i++)
        {
            var p = unsorted[i];
            var c = ToCanvas(p.TemperatureC, p.Percent);
            var dot = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = PointFill,
                Stroke = Accent,
                StrokeThickness = 2.5,
                Cursor = IsReadOnly ? Cursors.Arrow : Cursors.Hand,
                Tag = i,
                ToolTip = $"{p.TemperatureC:0} °C → {p.Percent:0} %",
            };
            Canvas.SetLeft(dot, c.X - 6);
            Canvas.SetTop(dot, c.Y - 6);
            Surface.Children.Add(dot);

            if (i == _keyIndex && IsKeyboardFocused)
            {
                // The point the arrow keys move: a ring in the focus colour around it.
                var ring = new Ellipse
                {
                    Width = 22,
                    Height = 22,
                    Stroke = TryFindResource("FocusStrokeColorOuterBrush") as Brush ?? SystemColors.HighlightBrush,
                    StrokeThickness = 2,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(ring, c.X - 11);
                Canvas.SetTop(ring, c.Y - 11);
                Surface.Children.Add(ring);
            }
        }

        UpdateAutomationText(unsorted);
    }

    /// <summary>Tells screen readers which point is selected and where it is.</summary>
    private void UpdateAutomationText(List<FanCurvePoint> points)
    {
        string help = Localization.Loc.T("Left and Right pick a point, Up and Down change the fan speed, Ctrl+Left and Ctrl+Right change the temperature, Insert adds a point, Delete removes it.");
        if (_keyIndex >= 0 && _keyIndex < points.Count)
        {
            var p = points[_keyIndex];
            help = Localization.Loc.F("Point {0} of {1}: {2} °C, {3} % fan speed.", _keyIndex + 1, points.Count, p.TemperatureC, p.Percent) + " " + help;
        }

        System.Windows.Automation.AutomationProperties.SetHelpText(this, help);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || IsReadOnly || Points is null || Points.Count == 0)
        {
            return;
        }

        var points = CurrentPoints();
        var byTemperature = Enumerable.Range(0, points.Count).OrderBy(i => points[i].TemperatureC).ToList();
        if (_keyIndex < 0 || _keyIndex >= points.Count)
        {
            _keyIndex = byTemperature[0];
        }

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;
        var selected = points[_keyIndex];
        int order = byTemperature.IndexOf(_keyIndex);

        switch (e.Key)
        {
            case Key.Left when !ctrl:
                _keyIndex = byTemperature[Math.Max(0, order - 1)];
                Redraw();
                break;
            case Key.Right when !ctrl:
                _keyIndex = byTemperature[Math.Min(byTemperature.Count - 1, order + 1)];
                Redraw();
                break;
            case Key.Left or Key.Right:
                MoveSelected(points, selected.TemperatureC + (e.Key == Key.Left ? -step : step), selected.Percent);
                break;
            case Key.Up or Key.PageUp:
                MoveSelected(points, selected.TemperatureC, selected.Percent + (e.Key == Key.PageUp ? 10 : step));
                break;
            case Key.Down or Key.PageDown:
                MoveSelected(points, selected.TemperatureC, selected.Percent - (e.Key == Key.PageDown ? 10 : step));
                break;
            case Key.Insert:
                if (FanCurveMath.PointBetween(points, _keyIndex) is { } added)
                {
                    Points.Add(added);
                    _keyIndex = Points.Count - 1;
                    PointsEdited?.Invoke(this, EventArgs.Empty);
                }

                break;
            case Key.Delete:
                if (Points.Count > 2)
                {
                    Points.RemoveAt(_keyIndex);
                    _keyIndex = Math.Min(_keyIndex, Points.Count - 1);
                    PointsEdited?.Invoke(this, EventArgs.Empty);
                    Redraw();
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void MoveSelected(List<FanCurvePoint> points, double tempC, double percent)
    {
        var moved = FanCurveMath.MoveWithinNeighbours(points, _keyIndex, tempC, percent);
        if (moved == points[_keyIndex] || Points is null)
        {
            return;
        }

        _suppressRedraw = true;
        try
        {
            Points[_keyIndex] = moved;
        }
        finally
        {
            _suppressRedraw = false;
        }

        PointsEdited?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    private void DrawGrid()
    {
        for (int v = 0; v <= 100; v += 10)
        {
            bool major = v % 20 == 0;
            var a = ToCanvas(v, 0);
            var b = ToCanvas(v, 100);
            Surface.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = GridBrush, StrokeThickness = major ? 1 : 0.5, IsHitTestVisible = false });
            var c = ToCanvas(0, v);
            var d = ToCanvas(100, v);
            Surface.Children.Add(new Line { X1 = c.X, Y1 = c.Y, X2 = d.X, Y2 = d.Y, Stroke = GridBrush, StrokeThickness = major ? 1 : 0.5, IsHitTestVisible = false });
            if (major)
            {
                AddLabel(v.ToString(CultureInfo.CurrentCulture) + "°", a.X - 8, Surface.ActualHeight - PadBottom + 4);
                AddLabel(v.ToString(CultureInfo.CurrentCulture) + "%", 4, c.Y - 8);
            }
        }
    }

    private void AddLabel(string text, double x, double y)
    {
        var label = new TextBlock { Text = text, FontSize = 10, Foreground = AxisTextBrush, IsHitTestVisible = false };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        Surface.Children.Add(label);
    }

    private void DrawMarker(List<FanCurvePoint> sorted)
    {
        if (CurrentTemperature is not { } temp || double.IsNaN(temp))
        {
            return;
        }

        double percent = FanCurveMath.Interpolate(sorted, temp);
        var top = ToCanvas(temp, 100);
        var bottom = ToCanvas(temp, 0);
        Surface.Children.Add(new Line
        {
            X1 = top.X, Y1 = top.Y, X2 = bottom.X, Y2 = bottom.Y,
            Stroke = MarkerBrush, StrokeThickness = 1.5, StrokeDashArray = [4, 3], IsHitTestVisible = false,
        });
        var at = ToCanvas(temp, percent);
        var dot = new Ellipse { Width = 10, Height = 10, Fill = MarkerBrush, IsHitTestVisible = false };
        Canvas.SetLeft(dot, at.X - 5);
        Canvas.SetTop(dot, at.Y - 5);
        Surface.Children.Add(dot);
        var label = new TextBlock
        {
            Text = $"{temp:0} °C → {percent:0} %",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = MarkerBrush,
            IsHitTestVisible = false,
        };
        double lx = at.X + 8;
        if (lx > Surface.ActualWidth - 90)
        {
            lx = at.X - 92;
        }

        Canvas.SetLeft(label, lx);
        Canvas.SetTop(label, Math.Max(PadTop, at.Y - 20));
        Surface.Children.Add(label);
    }

    private int HitTestPoint(Point position)
    {
        var points = CurrentPoints();
        int best = -1;
        double bestDistance = HitRadius;
        for (int i = 0; i < points.Count; i++)
        {
            var c = ToCanvas(points[i].TemperatureC, points[i].Percent);
            double distance = (c - position).Length;
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private void OnSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsReadOnly || Points is null)
        {
            return;
        }

        Focus();
        var position = e.GetPosition(Surface);
        int hit = HitTestPoint(position);

        if (e.ClickCount == 2)
        {
            if (hit >= 0)
            {
                if (Points.Count > 2)
                {
                    Points.RemoveAt(hit);
                    PointsEdited?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (IsInsidePlot(position))
            {
                var (t, p) = FromCanvas(position);
                Points.Add(new FanCurvePoint(Math.Round(t), Math.Round(p)));
                PointsEdited?.Invoke(this, EventArgs.Empty);
            }

            e.Handled = true;
            return;
        }

        if (hit >= 0)
        {
            _dragIndex = hit;
            _keyIndex = hit;
            Surface.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragIndex < 0 || Points is null || _dragIndex >= Points.Count || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var (t, p) = FromCanvas(e.GetPosition(Surface));

        // Keep the dragged point between its neighbours so the curve never folds back on itself.
        var moved = FanCurveMath.MoveWithinNeighbours(CurrentPoints(), _dragIndex, t, p);

        _suppressRedraw = true;
        try
        {
            Points[_dragIndex] = moved;
        }
        finally
        {
            _suppressRedraw = false;
        }

        Redraw();
    }

    private void OnSurfaceMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void OnSurfaceMouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
        }
    }

    private void EndDrag()
    {
        if (_dragIndex < 0)
        {
            return;
        }

        _dragIndex = -1;
        if (Surface.IsMouseCaptured)
        {
            Surface.ReleaseMouseCapture();
        }

        PointsEdited?.Invoke(this, EventArgs.Empty);
        Redraw();
    }

    private bool IsInsidePlot(Point p) =>
        p.X >= PadLeft && p.X <= Surface.ActualWidth - PadRight && p.Y >= PadTop && p.Y <= Surface.ActualHeight - PadBottom;

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
