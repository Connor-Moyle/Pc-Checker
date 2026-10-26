using System.Globalization;
using System.Windows.Data;
using PcChecker.Core.Common;
using Wpf.Ui.Controls;

namespace PcChecker.App.Converters;

/// <summary>Severity → InfoBar severity (Low/Info → Informational, Medium → Warning, High/Critical → Error).</summary>
public sealed class SeverityToInfoBarSeverityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Severity.Critical or Severity.High => InfoBarSeverity.Error,
        Severity.Medium => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null/empty → false, anything else → true (for IsOpen-style bool properties).</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !(value is null || value is string s && string.IsNullOrWhiteSpace(s));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A timestamp → "just now", "5 min ago", "yesterday 14:02", "2026-10-20".</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var time = value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Local) : dt),
            _ => (DateTimeOffset?)null,
        };
        return time is null ? "—" : Format(time.Value, DateTimeOffset.Now, culture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    public static string Format(DateTimeOffset time, DateTimeOffset now, CultureInfo? culture = null)
    {
        var age = now - time;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromHours(24) && time.Date == now.Date)
        {
            return "today " + time.ToString("HH:mm", culture ?? CultureInfo.CurrentCulture);
        }

        if (time.Date == now.Date.AddDays(-1))
        {
            return "yesterday " + time.ToString("HH:mm", culture ?? CultureInfo.CurrentCulture);
        }

        return time.ToString("yyyy-MM-dd HH:mm", culture ?? CultureInfo.CurrentCulture);
    }
}
