using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PcChecker.Core.WindowsFeatures;

namespace PcChecker.App.Converters;

/// <summary>FeatureState → badge brush (green when on/installed, grey otherwise).</summary>
public sealed class FeatureStateToBrushConverter : IValueConverter
{
    public static readonly Brush On = SeverityToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)));
    public static readonly Brush Off = SeverityToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    public static readonly Brush Unknown = SeverityToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FeatureState.Enabled => On,
        FeatureState.Disabled or FeatureState.NotPresent => Off,
        _ => Unknown,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
