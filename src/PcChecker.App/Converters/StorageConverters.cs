using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace PcChecker.App.Converters;

/// <summary>true → false, false/null → true (for IsEnabled bindings on "is running" flags).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not (bool and true);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not (bool and true);
}

/// <summary>true (an error) → <see cref="InfoBarSeverity.Error"/>, otherwise <see cref="InfoBarSeverity.Success"/>.</summary>
public sealed class BoolToInfoBarSeverityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool and true ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
