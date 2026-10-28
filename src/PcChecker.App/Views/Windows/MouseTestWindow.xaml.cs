using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PcChecker.App.ViewModels;
using PcChecker.Core.DeviceTests;

namespace PcChecker.App.Views.Windows;

/// <summary>
/// Mouse test. Buttons and the vertical wheel come through WPF's mouse events on the test box; the horizontal wheel
/// (tilt) is not a WPF event, so WM_MOUSEHWHEEL is read from the window's message hook while the pointer is over the box.
/// </summary>
public partial class MouseTestWindow
{
    private const int WmMouseHWheel = 0x020E;
    private HwndSource? _source;

    public MouseTestWindow(MouseTestViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        try
        {
            ViewModel.DoubleClickLimitMs = (int)GetDoubleClickTime();
        }
        catch
        {
            // Keep the Windows default of 500 ms.
        }
    }

    public MouseTestViewModel ViewModel { get; }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WndProc);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _source?.RemoveHook(WndProc);
        ViewModel.Stop();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseHWheel && TestArea.IsMouseOver)
        {
            short delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
            ViewModel.Flash(delta > 0 ? MouseTestInput.TiltRight : MouseTestInput.TiltLeft);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnTestAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Map(e.ChangedButton) is { } input)
        {
            ViewModel.Press(input, e.Timestamp);
        }

        e.Handled = true;
    }

    private void OnTestAreaMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Map(e.ChangedButton) is { } input)
        {
            ViewModel.Release(input);
        }

        e.Handled = true;
    }

    private void OnTestAreaWheel(object sender, MouseWheelEventArgs e)
    {
        ViewModel.Flash(e.Delta > 0 ? MouseTestInput.WheelUp : MouseTestInput.WheelDown);
        e.Handled = true;
    }

    private static MouseTestInput? Map(MouseButton button) => button switch
    {
        MouseButton.Left => MouseTestInput.Left,
        MouseButton.Right => MouseTestInput.Right,
        MouseButton.Middle => MouseTestInput.Middle,
        MouseButton.XButton1 => MouseTestInput.Back,
        MouseButton.XButton2 => MouseTestInput.Forward,
        _ => null,
    };

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
