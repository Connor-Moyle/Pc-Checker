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

    /// <summary>Set before closing for good (Exit, shutdown); otherwise Alt+F4 only hides the popup so it can open again.</summary>
    public bool AllowClose { get; set; }

    /// <summary>True while a confirmation dialog from the popup is open, so losing focus to it does not hide the popup.</summary>
    public bool KeepOpen { get; set; }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (KeepOpen)
        {
            return;
        }

        LastHiddenUtc = DateTime.UtcNow;
        Hide();
    }

    /// <summary>Esc hides the popup, like any flyout.</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && !KeepOpen)
        {
            e.Handled = true;
            LastHiddenUtc = DateTime.UtcNow;
            Hide();
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            LastHiddenUtc = DateTime.UtcNow;
            Hide();
        }

        base.OnClosing(e);
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
