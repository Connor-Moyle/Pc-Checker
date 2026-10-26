using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PcChecker.Core.Optimization;
using Wpf.Ui.Controls;

namespace PcChecker.App.Converters;

/// <summary>RiskLevel → brush (green / yellow / red).</summary>
public sealed class RiskToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RiskLevel.Safe => HealthStatusToBrushConverter.Good,
        RiskLevel.Moderate => HealthStatusToBrushConverter.Warning,
        RiskLevel.Advanced => HealthStatusToBrushConverter.Critical,
        _ => HealthStatusToBrushConverter.Unknown,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>bool? applied state → brush: true green, false grey, null (unknown/on-demand) blue.</summary>
public sealed class AppliedToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => HealthStatusToBrushConverter.Good,
        false => HealthStatusToBrushConverter.Unknown,
        _ => SeverityToBrushConverter.Info,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Percent (0–100) → brush: under 70 green, 70–85 yellow, above red (used for memory bars).</summary>
public sealed class UsageToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double d = value switch { double x => x, int i => i, float f => f, _ => 0 };
        return d >= 85 ? HealthStatusToBrushConverter.Critical : d >= 70 ? HealthStatusToBrushConverter.Warning : HealthStatusToBrushConverter.Good;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Collection count → Visible when > 0 (parameter "Invert" shows when empty).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value switch { int i => i, long l => (int)Math.Min(l, int.MaxValue), System.Collections.ICollection c => c.Count, _ => 0 };
        bool invert = parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        return (count > 0) ^ invert ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Performance score (0–100) → brush: 75+ green, 50–74 yellow, below red.</summary>
public sealed class ScoreToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double d = value switch { double x => x, int i => i, float f => f, _ => 0 };
        return d >= 75 ? HealthStatusToBrushConverter.Good : d >= 50 ? HealthStatusToBrushConverter.Warning : HealthStatusToBrushConverter.Critical;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>SymbolRegular → a SymbolIcon element, for controls whose Icon property takes an IconElement.</summary>
public sealed class SymbolToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SymbolRegular symbol ? new SymbolIcon(symbol) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true ↔ false.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not (bool and true);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not (bool and true);
}
