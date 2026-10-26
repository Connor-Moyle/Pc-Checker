using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PcChecker.Core.Lighting;

namespace PcChecker.App.Converters;

/// <summary>Media.Color, RgbColor or "#RRGGBB" → SolidColorBrush (used by colour swatches on the Lighting page).</summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        Color color = value switch
        {
            Color c => c,
            RgbColor rgb => Color.FromRgb(rgb.R, rgb.G, rgb.B),
            string s when RgbColorMath.TryParseHex(s, out var parsed) => Color.FromRgb(parsed.R, parsed.G, parsed.B),
            _ => Color.FromArgb(0x40, 0x80, 0x80, 0x80),
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>String icon name ("PlugConnected24") → <see cref="Wpf.Ui.Controls.SymbolRegular"/>.</summary>
public sealed class SymbolNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(s, out var symbol) ? symbol : Wpf.Ui.Controls.SymbolRegular.Info24;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
