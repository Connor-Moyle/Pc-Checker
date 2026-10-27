using PcChecker.Core.Common;
using Xunit;

namespace PcChecker.Core.Tests;

public class AdaptiveGridMathTests
{
    [Theory]
    [InlineData(1000, 220, 6, 12, 8, 4)]   // (1000+12)/(232) = 4.36 -> 4 columns
    [InlineData(400, 220, 6, 12, 8, 1)]
    [InlineData(2000, 220, 6, 12, 8, 6)]   // capped by MaxColumns
    [InlineData(100, 220, 6, 12, 3, 1)]    // never below one column
    public void Picks_as_many_columns_as_fit(double width, double min, int max, double gap, int count, int expected)
    {
        var (columns, _) = AdaptiveGridMath.Layout(width, min, max, gap, count);
        Assert.Equal(expected, columns);
    }

    [Fact]
    public void Columns_and_gaps_fill_the_width_exactly()
    {
        var (columns, itemWidth) = AdaptiveGridMath.Layout(1000, 220, 6, 12, 8);
        Assert.Equal(1000, columns * itemWidth + 12 * (columns - 1), 6);
        Assert.True(itemWidth >= 220);
    }

    [Fact]
    public void No_items_means_no_columns()
    {
        Assert.Equal((0, 0d), AdaptiveGridMath.Layout(1000, 220, 6, 12, 0));
    }

    [Fact]
    public void Unbounded_width_uses_min_width_and_caps_columns_at_item_count()
    {
        var (columns, itemWidth) = AdaptiveGridMath.Layout(double.PositiveInfinity, 220, 6, 12, 3);
        Assert.Equal(3, columns);
        Assert.Equal(220, itemWidth);
    }
}
