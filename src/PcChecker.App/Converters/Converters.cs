using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PcChecker.Core.Common;
using PcChecker.App.Services;
using PcChecker.Core.Accessibility;

namespace PcChecker.App.Converters;

/// <summary>true → Visible, false/null → Collapsed. Pass "Hidden" as the parameter to hide instead of collapse.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        var hidden = parameter is string s && s.Equals("Hidden", StringComparison.OrdinalIgnoreCase) ? Visibility.Hidden : Visibility.Collapsed;
        return flag ? Visibility.Visible : hidden;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>true → Collapsed, false/null → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not Visibility.Visible;
}

/// <summary>Byte counts (long, int, double, nullable) → "1.23 GB".</summary>
public sealed class BytesToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => "—",
        long l => ByteSize.Format(l),
        int i => ByteSize.Format(i),
        ulong ul => ByteSize.Format((long)Math.Min(ul, long.MaxValue)),
        double d => ByteSize.Format((long)d),
        _ => value.ToString() ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Severity → brush (grey/blue/yellow/orange/red), in the current theme's shades (see <see cref="ThemeColors"/>).</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public static Brush Info => ThemeColors.Accent(AccentRole.Blue);
    public static Brush Low => ThemeColors.Accent(AccentRole.Grey);
    public static Brush Medium => ThemeColors.Accent(AccentRole.Yellow);
    public static Brush High => ThemeColors.Accent(AccentRole.Orange);
    public static Brush Critical => ThemeColors.Accent(AccentRole.Red);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Severity.Critical => Critical,
        Severity.High => High,
        Severity.Medium => Medium,
        Severity.Low => Low,
        _ => Info,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>HealthStatus → brush (grey/green/yellow/red), in the current theme's shades.</summary>
public sealed class HealthStatusToBrushConverter : IValueConverter
{
    public static Brush Unknown => ThemeColors.Accent(AccentRole.Grey);
    public static Brush Good => ThemeColors.Accent(AccentRole.Green);
    public static Brush Warning => ThemeColors.Accent(AccentRole.Yellow);
    public static Brush Critical => ThemeColors.Accent(AccentRole.Red);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HealthStatus.Good => Good,
        HealthStatus.Warning => Warning,
        HealthStatus.Critical => Critical,
        _ => Unknown,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null/empty → Collapsed, anything else → Visible. Parameter "Invert" flips it.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isNull = value is null || value is string s && string.IsNullOrWhiteSpace(s);
        bool invert = parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        return isNull ^ invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Enum → its [Description] text, or the name split into words ("HighPerformance" → "High performance").</summary>
public sealed class EnumToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is not Enum e)
        {
            return value.ToString() ?? string.Empty;
        }

        var member = e.GetType().GetField(e.ToString());
        var description = member?.GetCustomAttribute<DescriptionAttribute>()?.Description;
        return Localization.Loc.T(description ?? SplitWords(e.ToString()));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    public static string SplitWords(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var sb = new System.Text.StringBuilder(name.Length + 4);
        sb.Append(name[0]);
        for (int i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                sb.Append(' ').Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                sb.Append(name[i]);
            }
        }

        return sb.ToString();
    }
}

/// <summary>0..1 or 0..100 → "42 %". Parameter "Fraction" treats the input as 0..1.</summary>
public sealed class DoubleToPercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return "—";
        }

        double d = value switch
        {
            double x => x,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            _ => double.NaN,
        };
        if (double.IsNaN(d))
        {
            return "—";
        }

        if (parameter is string p && p.Equals("Fraction", StringComparison.OrdinalIgnoreCase))
        {
            d *= 100;
        }

        return d.ToString("0", culture) + " %";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Temperature → brush using warn/crit thresholds. Parameter "warn,crit" (e.g. "85,95"); defaults 80/90.</summary>
public sealed class TemperatureToBrushConverter : IValueConverter
{
    public static Brush Cool => ThemeColors.Accent(AccentRole.Cyan);
    public static Brush Normal => ThemeColors.Accent(AccentRole.Green);
    public static Brush Warm => ThemeColors.Accent(AccentRole.Yellow);
    public static Brush Hot => ThemeColors.Accent(AccentRole.Red);
    public static Brush Unknown => ThemeColors.Accent(AccentRole.Grey);

    public double DefaultWarn { get; set; } = 80;
    public double DefaultCrit { get; set; } = 90;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double? temp = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => null,
        };
        if (temp is null || double.IsNaN(temp.Value))
        {
            return Unknown;
        }

        double warn = DefaultWarn, crit = DefaultCrit;
        if (parameter is string p)
        {
            var parts = p.Split(',', ';');
            if (parts.Length >= 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
            {
                warn = w;
            }

            if (parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var c))
            {
                crit = c;
            }
        }

        if (temp >= crit)
        {
            return Hot;
        }

        if (temp >= warn)
        {
            return Warm;
        }

        return temp < 40 ? Cool : Normal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Translates bound English interface text (for labels that come from code, such as section tabs).</summary>
public sealed class TranslateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? Localization.Loc.T(text) : value ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
