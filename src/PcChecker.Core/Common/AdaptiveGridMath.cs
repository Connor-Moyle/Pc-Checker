namespace PcChecker.Core.Common;

/// <summary>Column maths for the app's adaptive tile grid: as many equal columns as fit at a minimum item width.</summary>
public static class AdaptiveGridMath
{
    public static (int Columns, double ItemWidth) Layout(double availableWidth, double minItemWidth, int maxColumns, double spacing, int count)
    {
        if (count <= 0)
        {
            return (0, 0);
        }

        maxColumns = Math.Max(1, maxColumns);
        spacing = Math.Max(0, spacing);
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth))
        {
            return (Math.Min(count, maxColumns), Math.Max(0, minItemWidth));
        }

        minItemWidth = Math.Max(1, minItemWidth);
        int columns = (int)Math.Floor((Math.Max(0, availableWidth) + spacing) / (minItemWidth + spacing));
        columns = Math.Clamp(columns, 1, maxColumns);
        double width = Math.Max(0, (availableWidth - spacing * (columns - 1)) / columns);
        return (columns, width);
    }
}
