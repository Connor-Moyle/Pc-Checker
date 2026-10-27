using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PcChecker.Core.WindowsFeatures;
using PcChecker.App.Services;
using PcChecker.Core.Accessibility;

namespace PcChecker.App.Converters;

/// <summary>FeatureState → badge brush (green when on/installed, grey otherwise), in the current theme's shades.</summary>
public sealed class FeatureStateToBrushConverter : IValueConverter
{
    public static Brush On => ThemeColors.Accent(AccentRole.Green);
    public static Brush Off => ThemeColors.Accent(AccentRole.Grey);
    public static Brush Unknown => ThemeColors.Accent(AccentRole.Grey);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FeatureState.Enabled => On,
        FeatureState.Disabled or FeatureState.NotPresent => Off,
        _ => Unknown,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
