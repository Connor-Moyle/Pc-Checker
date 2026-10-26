using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcChecker.Core.Storage;

namespace PcChecker.App.Controls;

/// <summary>Grid of coloured cells showing the state of every block of a surface scan, with a legend.</summary>
public partial class BlockMapControl : UserControl
{
    public static readonly DependencyProperty MapProperty =
        DependencyProperty.Register(nameof(Map), typeof(IEnumerable), typeof(BlockMapControl), new PropertyMetadata(null));

    public static readonly DependencyProperty CellSizeProperty =
        DependencyProperty.Register(nameof(CellSize), typeof(double), typeof(BlockMapControl), new PropertyMetadata(7d));

    public static readonly DependencyProperty ShowLegendProperty =
        DependencyProperty.Register(nameof(ShowLegend), typeof(bool), typeof(BlockMapControl), new PropertyMetadata(true));

    public BlockMapControl()
    {
        InitializeComponent();
    }

    /// <summary>Sequence of <see cref="BlockState"/> values (already downsampled to a few thousand cells at most).</summary>
    public IEnumerable? Map { get => (IEnumerable?)GetValue(MapProperty); set => SetValue(MapProperty, value); }

    public double CellSize { get => (double)GetValue(CellSizeProperty); set => SetValue(CellSizeProperty, value); }

    public bool ShowLegend { get => (bool)GetValue(ShowLegendProperty); set => SetValue(ShowLegendProperty, value); }
}

/// <summary>
/// Draws the cells. One frozen geometry per state keeps a 2000-cell map cheap to redraw many times a second while
/// a scan is running. Width comes from the layout; height grows with the number of rows needed.
/// </summary>
public sealed class BlockMapCanvas : FrameworkElement
{
    public static readonly Brush UntestedBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x9C, 0xA3, 0xAF)));
    public static readonly Brush OkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    public static readonly Brush SlowBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)));
    public static readonly Brush BadBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));

    public static readonly DependencyProperty MapProperty =
        DependencyProperty.Register(nameof(Map), typeof(IEnumerable), typeof(BlockMapCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnMapChanged));

    public static readonly DependencyProperty CellSizeProperty =
        DependencyProperty.Register(nameof(CellSize), typeof(double), typeof(BlockMapCanvas),
            new FrameworkPropertyMetadata(7d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Gap = 1;
    private BlockState[] _cells = [];

    public IEnumerable? Map { get => (IEnumerable?)GetValue(MapProperty); set => SetValue(MapProperty, value); }

    public double CellSize { get => (double)GetValue(CellSizeProperty); set => SetValue(CellSizeProperty, value); }

    private static void OnMapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (BlockMapCanvas)d;
        var list = new List<BlockState>();
        if (e.NewValue is IEnumerable items)
        {
            foreach (var item in items)
            {
                if (item is BlockState state)
                {
                    list.Add(state);
                }
            }
        }

        canvas._cells = list.ToArray();
    }

    private int ColumnsFor(double width)
    {
        double pitch = Math.Max(2, CellSize + Gap);
        return Math.Max(1, (int)Math.Floor((width + Gap) / pitch));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        if (_cells.Length == 0)
        {
            return new Size(width, Math.Max(MinHeight, CellSize + Gap));
        }

        int columns = ColumnsFor(width);
        int rows = (int)Math.Ceiling(_cells.Length / (double)columns);
        double height = rows * (CellSize + Gap) - Gap;
        return new Size(width, Math.Max(MinHeight, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double width = ActualWidth;
        if (_cells.Length == 0 || width <= 0)
        {
            return;
        }

        int columns = ColumnsFor(width);
        double pitch = CellSize + Gap;
        var groups = new Dictionary<BlockState, StreamGeometry>();
        var contexts = new Dictionary<BlockState, StreamGeometryContext>();
        for (int i = 0; i < _cells.Length; i++)
        {
            var state = _cells[i];
            if (!groups.TryGetValue(state, out var geometry))
            {
                geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
                groups[state] = geometry;
                contexts[state] = geometry.Open();
            }

            var ctx = contexts[state];
            double x = (i % columns) * pitch;
            double y = (i / columns) * pitch;
            ctx.BeginFigure(new Point(x, y), true, true);
            ctx.LineTo(new Point(x + CellSize, y), false, false);
            ctx.LineTo(new Point(x + CellSize, y + CellSize), false, false);
            ctx.LineTo(new Point(x, y + CellSize), false, false);
        }

        foreach (var (state, ctx) in contexts)
        {
            ctx.Close();
            var geometry = groups[state];
            geometry.Freeze();
            drawingContext.DrawGeometry(BrushFor(state), null, geometry);
        }
    }

    private static Brush BrushFor(BlockState state) => state switch
    {
        BlockState.Ok => OkBrush,
        BlockState.Slow => SlowBrush,
        BlockState.Bad => BadBrush,
        _ => UntestedBrush,
    };

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
