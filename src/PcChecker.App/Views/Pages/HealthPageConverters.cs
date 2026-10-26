using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PcChecker.App.Views.Pages;

/// <summary>Visible only when every bound value is true (used to show the raw SMART table in advanced mode).</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[]? values, Type targetType, object? parameter, CultureInfo culture) =>
        values is { Length: > 0 } && values.All(v => v is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
