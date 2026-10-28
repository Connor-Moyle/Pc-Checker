using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PcChecker.App.Localization;
using PcChecker.Core.DeviceTests;
using PcChecker.Core.Displays;

namespace PcChecker.App.Views.Windows;

/// <summary>
/// Full-screen screen test on one monitor: solid colours for dead or stuck pixels, colour ramps for banding, a dark
/// grey for backlight evenness and a moving block with a frame counter. Left/Right (or a click) switch tests, H hides
/// the hint, Esc closes. Built in code because it is only coloured areas and a few lines of text drawn on top of them.
/// </summary>
public sealed class ScreenTestWindow : Window
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNull = 0;
    private static readonly TimeSpan HintTime = TimeSpan.FromSeconds(4);

    private readonly DisplayInfo? _display;
    private readonly List<(string Name, string Hint, Action Show)> _modes = [];
    private readonly Border _fill = new();
    private readonly Grid _ramps = new() { Visibility = Visibility.Collapsed };
    private readonly Canvas _motion = new() { Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)) };
    private readonly Rectangle _block = new() { Fill = Brushes.White };
    private readonly Border _hint = new();
    private readonly TextBlock _hintText = new();
    private readonly Border _readout = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _readoutText = new();
    private readonly DispatcherTimer _hintTimer = new() { Interval = HintTime };
    private readonly FrameTimingAnalyzer _frames;
    private int _index;
    private bool _hintPinnedOff;
    private bool _rendering;
    private double _lastReadoutMs;

    public ScreenTestWindow(DisplayInfo? display)
    {
        _display = display;
        _frames = new FrameTimingAnalyzer(display?.Current.RefreshHz ?? 60);
        Title = Loc.T("Screen test");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = true;
        Background = Brushes.Black;
        Cursor = Cursors.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        Width = 800;
        Height = 600;

        BuildModes();
        Content = BuildContent();
        SourceInitialized += OnSourceInitialized;
        KeyDown += OnKeyDown;
        MouseDown += OnMouseDown;
        Closed += OnClosed;
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            _hint.Visibility = Visibility.Collapsed;
        };
        Loaded += (_, _) =>
        {
            ShowMode(0);
            Activate();
            Focus();
        };
    }

    private UIElement BuildContent()
    {
        foreach (var colour in new[] { Colors.Gray, Colors.Red, Colors.Lime, Colors.Blue })
        {
            _ramps.RowDefinitions.Add(new RowDefinition());
            var ramp = new Rectangle { Fill = new LinearGradientBrush(Colors.Black, colour, 0) };
            if (colour == Colors.Gray)
            {
                ramp.Fill = new LinearGradientBrush(Colors.Black, Colors.White, 0);
            }

            Grid.SetRow(ramp, _ramps.RowDefinitions.Count - 1);
            _ramps.Children.Add(ramp);
        }

        _motion.Children.Add(_block);

        _hintText.Foreground = Brushes.White;
        _hintText.FontSize = 18;
        _hintText.TextAlignment = TextAlignment.Center;
        _hintText.TextWrapping = TextWrapping.Wrap;
        _hint.Child = _hintText;
        _hint.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x10, 0x10));
        _hint.CornerRadius = new CornerRadius(8);
        _hint.Padding = new Thickness(20, 12, 20, 12);
        _hint.Margin = new Thickness(40);
        _hint.MaxWidth = 900;
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.VerticalAlignment = VerticalAlignment.Bottom;

        _readoutText.Foreground = Brushes.White;
        _readoutText.FontSize = 18;
        _readoutText.FontFamily = new FontFamily("Consolas");
        _readout.Child = _readoutText;
        _readout.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x10, 0x10));
        _readout.Padding = new Thickness(14, 8, 14, 8);
        _readout.Margin = new Thickness(24);
        _readout.HorizontalAlignment = HorizontalAlignment.Left;
        _readout.VerticalAlignment = VerticalAlignment.Top;

        var root = new Grid();
        root.Children.Add(_fill);
        root.Children.Add(_ramps);
        root.Children.Add(_motion);
        root.Children.Add(_readout);
        root.Children.Add(_hint);
        return root;
    }

    private void BuildModes()
    {
        void Solid(string name, Color colour, string hint) => _modes.Add((name, hint, () => ShowSolid(colour)));

        string pixels = Loc.T("Look for dots that stay a different colour (stuck or dead pixels).");
        Solid(Loc.T("Black"), Colors.Black, Loc.T("Look for bright dots (stuck pixels) and light leaking in at the edges (backlight bleed)."));
        Solid(Loc.T("White"), Colors.White, Loc.T("Look for dark dots (dead pixels), dust under the screen and yellow or blue patches."));
        Solid(Loc.T("Red"), Colors.Red, pixels);
        Solid(Loc.T("Green"), Colors.Lime, pixels);
        Solid(Loc.T("Blue"), Colors.Blue, pixels);
        Solid(Loc.T("Grey"), Color.FromRgb(0x80, 0x80, 0x80), Loc.T("Look for darker corners, bands or a vignette: the brightness should be even."));
        _modes.Add((Loc.T("Colour ramps"), Loc.T("Each strip should fade smoothly. Visible steps or stripes are banding (common on 6-bit panels or with colour settings changed)."), ShowRamps));
        Solid(Loc.T("Evenness (dark grey)"), Color.FromRgb(0x1C, 0x1C, 0x1C), Loc.T("Best in a dark room: look for cloudy, brighter patches (clouding) and glowing corners (IPS glow)."));
        _modes.Add((Loc.T("Motion"), Loc.T("The block should move smoothly. Blur, ghost copies behind it or stutter show how the screen handles motion."), ShowMotion));
    }

    private void ShowMode(int index)
    {
        _index = (index % _modes.Count + _modes.Count) % _modes.Count;
        StopMotion();
        _fill.Visibility = Visibility.Collapsed;
        _ramps.Visibility = Visibility.Collapsed;
        _motion.Visibility = Visibility.Collapsed;
        _readout.Visibility = Visibility.Collapsed;
        var (name, hint, show) = _modes[_index];
        show();
        _hintText.Text = Loc.F("{0} ({1} of {2})", name, _index + 1, _modes.Count) + "\n" + hint + "\n" +
                         Loc.T("Left/Right arrow or click: next test · H: hide this text · Esc: close");
        ShowHint();
    }

    private void ShowHint()
    {
        if (_hintPinnedOff)
        {
            return;
        }

        _hint.Visibility = Visibility.Visible;
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void ShowSolid(Color colour)
    {
        _fill.Background = new SolidColorBrush(colour);
        _fill.Visibility = Visibility.Visible;
    }

    private void ShowRamps() => _ramps.Visibility = Visibility.Visible;

    private void ShowMotion()
    {
        _motion.Visibility = Visibility.Visible;
        _readout.Visibility = Visibility.Visible;
        _readoutText.Text = Loc.T("Measuring…");
        _frames.Reset();
        if (!_rendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
    }

    private void StopMotion()
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args)
        {
            return;
        }

        double now = args.RenderingTime.TotalMilliseconds;
        if (!_frames.AddFrame(now))
        {
            return;
        }

        double width = Math.Max(1, _motion.ActualWidth);
        double height = Math.Max(1, _motion.ActualHeight);
        double blockWidth = Math.Max(40, width / 12);
        double blockHeight = Math.Max(40, height / 5);
        double speed = width / 3;
        double travel = width + blockWidth;
        double x = now / 1000 * speed % travel - blockWidth;
        _block.Width = blockWidth;
        _block.Height = blockHeight;
        Canvas.SetLeft(_block, x);
        Canvas.SetTop(_block, (height - blockHeight) / 2);

        if (now - _lastReadoutMs >= 250)
        {
            _lastReadoutMs = now;
            _readoutText.Text = Loc.F("Frames per second: {0}", _frames.FramesPerSecond) + "\n" +
                                Loc.F("Screen refresh rate: {0:0} Hz", _frames.RefreshHz) + "\n" +
                                Loc.F("Skipped frames: {0}", _frames.SkippedFrames) + "\n" +
                                (_frames.DrawsSlowerThanScreen && _frames.DrawingHz is { } drawingHz
                                    ? Loc.F("Windows draws this test at about {0:0} Hz (usually the main screen's rate), so skipped frames are counted at that rate. Make this screen the main display to test its full refresh rate.", drawingHz)
                                    : Loc.T("A frame rate below the refresh rate can mean the PC is busy, not that the screen is slow."));
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Right or Key.Down or Key.Space or Key.PageDown:
                ShowMode(_index + 1);
                break;
            case Key.Left or Key.Up or Key.PageUp:
                ShowMode(_index - 1);
                break;
            case Key.H:
                _hintPinnedOff = _hint.Visibility == Visibility.Visible;
                if (_hintPinnedOff)
                {
                    _hint.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ShowHint();
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        ShowMode(e.ChangedButton == MouseButton.Right ? _index - 1 : _index + 1);
        e.Handled = true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_display is null)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int width = Math.Max(1, _display.Current.Width);
            int height = Math.Max(1, _display.Current.Height);
            var rect = new NativeRect { Left = _display.PositionX, Top = _display.PositionY, Right = _display.PositionX + width, Bottom = _display.PositionY + height };
            if (MonitorFromRect(ref rect, MonitorDefaultToNull) == IntPtr.Zero)
            {
                // The screen is not where Windows said (or this is demo data): fill the main screen instead.
                WindowState = WindowState.Maximized;
                return;
            }

            // Move a small window onto that screen (physical pixels), then maximise: a maximised borderless window
            // fills exactly that screen, whatever its scaling, taskbar included.
            SetWindowPos(hwnd, HwndTopmost, rect.Left + 16, rect.Top + 16, Math.Min(400, width), Math.Min(300, height), SwpNoActivate);
            WindowState = WindowState.Maximized;
        }
        catch
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopMotion();
        _hintTimer.Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
}
