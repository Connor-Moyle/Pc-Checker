using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PcChecker.App.Converters;

namespace PcChecker.App.Tray;

/// <summary>Borderless, rounded popup shown next to the tray icon; hides itself when it loses focus.</summary>
public partial class TrayPopupWindow : Window
{
    public TrayPopupWindow(TrayPopupViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public TrayPopupViewModel ViewModel { get; }

    /// <summary>When the popup last hid itself, so a click on the tray icon that caused the hide does not reopen it.</summary>
    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;

    private void OnDeactivated(object? sender, EventArgs e)
    {
        LastHiddenUtc = DateTime.UtcNow;
        Hide();
    }
}

/// <summary>Alert count → badge colour: green with none, amber with one, red with more.</summary>
public sealed class AlertCountToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        int and > 1 => HealthStatusToBrushConverter.Critical,
        int and 1 => SeverityToBrushConverter.High,
        _ => HealthStatusToBrushConverter.Good,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
