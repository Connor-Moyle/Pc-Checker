using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PcChecker.Core.Logging;

namespace PcChecker.App.Converters;

/// <summary>Log level → badge brush (grey for Verbose/Debug, blue Information, yellow Warning, red Error/Fatal).</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public static readonly Brush Verbose = SeverityToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    public static readonly Brush Debug = SeverityToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));
    public static readonly Brush Information = SeverityToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)));
    public static readonly Brush Warning = SeverityToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)));
    public static readonly Brush Error = SeverityToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
    public static readonly Brush Fatal = SeverityToBrushConverter.Freeze(new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)));

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
