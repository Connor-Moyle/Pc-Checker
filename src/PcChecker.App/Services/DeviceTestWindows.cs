using System.Windows;
using Microsoft.Extensions.Logging;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Windows;
using PcChecker.Core.Displays;
using PcChecker.Core.Input;

namespace PcChecker.App.Services;

/// <summary>
/// Opens the Device tests page's own windows (keyboard, mouse, full-screen screen test), one of each at a time:
/// opening a test that is already open brings it to the front.
/// </summary>
public sealed class DeviceTestWindows
{
    private readonly IMousePollingMeter _pollingMeter;
    private readonly ILogger<MouseTestViewModel> _mouseLogger;
    private KeyboardTestWindow? _keyboard;
    private MouseTestWindow? _mouse;
    private ScreenTestWindow? _screen;

    public DeviceTestWindows(IMousePollingMeter pollingMeter, ILogger<MouseTestViewModel> mouseLogger)
    {
        _pollingMeter = pollingMeter;
        _mouseLogger = mouseLogger;
    }

    public void OpenKeyboard()
    {
        if (BringToFront(_keyboard))
        {
            return;
        }

        _keyboard = new KeyboardTestWindow(new KeyboardTestViewModel()) { Owner = MainWindow() };
        _keyboard.Closed += (_, _) => _keyboard = null;
        _keyboard.Show();
        _keyboard.Activate();
    }

    public void OpenMouse()
    {
        if (BringToFront(_mouse))
        {
            return;
        }

        _mouse = new MouseTestWindow(new MouseTestViewModel(_pollingMeter, _mouseLogger)) { Owner = MainWindow() };
        _mouse.Closed += (_, _) => _mouse = null;
        _mouse.Show();
        _mouse.Activate();
    }

    /// <summary>Opens the full-screen test on <paramref name="display"/>, or on the main screen when it is null.</summary>
    public void OpenScreen(DisplayInfo? display)
    {
        _screen?.Close();
        _screen = new ScreenTestWindow(display);
        _screen.Closed += (_, _) => _screen = null;
        _screen.Show();
    }

    private static bool BringToFront(Window? window)
    {
        if (window is null)
        {
            return false;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        return true;
    }

    private static Window? MainWindow()
    {
        var main = Application.Current?.MainWindow;
        return main is { IsVisible: true } ? main : null;
    }
}
