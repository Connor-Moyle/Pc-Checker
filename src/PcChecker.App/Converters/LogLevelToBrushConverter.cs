using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PcChecker.Core.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Accessibility;

namespace PcChecker.App.Converters;

/// <summary>Log level → badge brush (grey for Verbose/Debug, cyan Information, yellow Warning, red Error/Fatal), in the
/// current theme's shades.</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public static Brush Verbose => ThemeColors.Accent(AccentRole.Grey);
    public static Brush Debug => ThemeColors.Accent(AccentRole.Grey);
    public static Brush Information => ThemeColors.Accent(AccentRole.Cyan);
    public static Brush Warning => ThemeColors.Accent(AccentRole.Yellow);
    public static Brush Error => ThemeColors.Accent(AccentRole.Red);
    public static Brush Fatal => ThemeColors.Accent(AccentRole.Red);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogLevelSetting.Verbose => Verbose,
        LogLevelSetting.Debug => Debug,
        LogLevelSetting.Information => Information,
        LogLevelSetting.Warning => Warning,
        LogLevelSetting.Error => Error,
        LogLevelSetting.Fatal => Fatal,
        _ => Debug,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Journal outcome text → brush: green when it starts with "Succeeded", red for "Failed", grey otherwise.</summary>
public sealed class OutcomeToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        if (text.StartsWith("Succeeded", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Success", StringComparison.OrdinalIgnoreCase))
        {
            return HealthStatusToBrushConverter.Good;
        }

        if (text.StartsWith("Failed", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            return HealthStatusToBrushConverter.Critical;
        }

        if (text.StartsWith("Cancel", StringComparison.OrdinalIgnoreCase))
        {
            return HealthStatusToBrushConverter.Warning;
        }

        return HealthStatusToBrushConverter.Unknown;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
