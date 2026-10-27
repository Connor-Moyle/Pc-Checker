using System.Windows;
using System.Windows.Controls;

namespace PcChecker.App.Controls;

/// <summary>
/// Lays children out in equal-width columns, as many as fit at <see cref="MinItemWidth"/>, so stat tiles and cards
/// line up in a tidy grid at any window width instead of wrapping raggedly. Every item in a row gets the row's height.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(220d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(6, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The narrowest an item may get before the grid drops a column. Default 220.</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>The most columns the grid uses however wide it gets. Default 6.</summary>
    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>The gap between items, horizontally and vertically. Default 12.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private static (int Columns, double ItemWidth) Layout(double availableWidth, double minItemWidth, int maxColumns, double spacing, int count) =>
        Core.Common.AdaptiveGridMath.Layout(availableWidth, minItemWidth, maxColumns, spacing, count);

    private IEnumerable<UIElement> VisibleChildren => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed);

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = VisibleChildren.ToList();
        var (columns, itemWidth) = Layout(availableSize.Width, MinItemWidth, MaxColumns, Spacing, children.Count);
        if (columns == 0)
        {
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(0, 0));
            }

            return new Size(0, 0);
        }

        double height = 0;
        for (int start = 0; start < children.Count; start += columns)
        {
            double rowHeight = 0;
            foreach (var child in children.Skip(start).Take(columns))
            {
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            height += rowHeight + (start > 0 ? Spacing : 0);
        }

        double width = double.IsInfinity(availableSize.Width)
            ? columns * itemWidth + Spacing * (columns - 1)
            : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = VisibleChildren.ToList();
        var (columns, itemWidth) = Layout(finalSize.Width, MinItemWidth, MaxColumns, Spacing, children.Count);
        double y = 0;
        for (int start = 0; start < children.Count; start += columns)
        {
            var row = children.Skip(start).Take(columns).ToList();
            double rowHeight = row.Max(c => c.DesiredSize.Height);
            for (int i = 0; i < row.Count; i++)
            {
                row[i].Arrange(new Rect(i * (itemWidth + Spacing), y, itemWidth, rowHeight));
            }

            y += rowHeight + Spacing;
        }

        return finalSize;
    }
}
