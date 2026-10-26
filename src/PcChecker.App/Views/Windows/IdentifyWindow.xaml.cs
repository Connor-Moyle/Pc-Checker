using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PcChecker.Core.Displays;

namespace PcChecker.App.Views.Windows;

/// <summary>
/// Big number shown in the middle of a display for a few seconds (like the "Identify" button in Windows settings).
/// Positioned in physical pixels so it lands on the right monitor whatever its DPI scale.
/// </summary>
public partial class IdentifyWindow : Window
{
    private const int WindowWidthDip = 280;
    private const int WindowHeightDip = 200;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private readonly DisplayInfo _display;
    private readonly TimeSpan _duration;

    public IdentifyWindow(DisplayInfo display, int number, TimeSpan duration)
    {
        _display = display;
        _duration = duration;
        InitializeComponent();
        NumberText.Text = number.ToString();
        NameText.Text = display.FriendlyName;
        ModeText.Text = display.Current.ToString();
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>Shows one window per display and closes them all after <paramref name="duration"/>.</summary>
    public static void ShowFor(IEnumerable<(DisplayInfo Display, int Number)> displays, TimeSpan duration)
    {
        foreach (var (display, number) in displays)
        {
            var window = new IdentifyWindow(display, number, duration);
            window.Show();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            double scale = _display.ScalePercent > 0 ? _display.ScalePercent / 100.0 : 1.0;
            int width = (int)Math.Round(WindowWidthDip * scale);
            int height = (int)Math.Round(WindowHeightDip * scale);
            int x = _display.PositionX + (_display.Current.Width - width) / 2;
            int y = _display.PositionY + (_display.Current.Height - height) / 2;
            SetWindowPos(hwnd, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);
        }
        catch
        {
            // If positioning fails the window still shows on the primary display; that is better than nothing.
        }

        var timer = new DispatcherTimer { Interval = _duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        timer.Start();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
