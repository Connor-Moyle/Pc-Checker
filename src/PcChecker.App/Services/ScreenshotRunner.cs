using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Windows;
using Wpf.Ui;

namespace PcChecker.App.Services;

/// <summary>
/// Walks every page in navigation order, waits for it to load, renders the window to PNG, captures the overlay over a
/// dark gradient and the tray popup over a desktop backdrop, and writes <c>index.json</c>. Started by <c>App</c> when <c>--screenshots &lt;dir&gt;</c> is given.
/// </summary>
public sealed class ScreenshotRunner
{
    public const int WindowWidth = 1440;
    public const int WindowHeight = 900;
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(45);

    private readonly IServiceProvider _services;
    private readonly INavigationService _navigation;
    private readonly ILogger<ScreenshotRunner> _logger;

    public ScreenshotRunner(IServiceProvider services, INavigationService navigation, ILogger<ScreenshotRunner> logger)
    {
        _services = services;
        _navigation = navigation;
        _logger = logger;
    }

    /// <summary>Runs the capture. Returns the process exit code: 0 on success, 1 on any failure.</summary>
    public async Task<int> RunAsync(MainWindow window, string outputDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(dir);
            _logger.LogInformation("Screenshot run started → {Directory}", dir);

            window.WindowState = WindowState.Normal;
            AllowLargerThanScreen(window);
            // WPF clamps Width to the screen-sized limit it cached earlier, and MinWidth wins over that limit, so pin
            // the minimum too. Assigning twice makes Windows send WM_GETMINMAXINFO again with the raised limits.
            window.MinWidth = WindowWidth;
            window.MinHeight = WindowHeight;
            window.Width = WindowWidth - 1;
            window.Height = WindowHeight - 1;
            window.Width = WindowWidth;
            window.Height = WindowHeight;
            window.UpdateLayout();
            _logger.LogInformation("Capture window is {Width} x {Height}.", window.ActualWidth, window.ActualHeight);

            // Screenshots use the dark theme, the app's intended look, whatever the capturing machine prefers.
            ViewModels.SettingsViewModel.ApplyTheme(PcChecker.Core.Settings.AppTheme.Dark);
            await IdleAsync(window.Dispatcher);
            await Task.Delay(600, cancellationToken);

            // Notifications raised while pages load (a finished check-up, say) would otherwise sit over a later page.
            if (_services.GetService(typeof(Wpf.Ui.ISnackbarService)) is Wpf.Ui.ISnackbarService snackbar && snackbar.GetSnackbarPresenter() is { } presenter)
            {
                presenter.Visibility = Visibility.Collapsed;
            }

            var index = new Dictionary<string, string>();
            foreach (var entry in NavigationCatalog.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _navigation.Navigate(entry.PageType);
                await IdleAsync(window.Dispatcher);

                if (_services.GetService(entry.PageType) is FrameworkElement { DataContext: ViewModelBase vm })
                {
                    await vm.EnsureInitializedAsync(cancellationToken);
                    await WaitUntilAsync(() => !vm.IsBusy, BusyTimeout, cancellationToken);
                }

                await Task.Delay(400, cancellationToken);
                await IdleAsync(window.Dispatcher);

                var fileName = $"{entry.FileName}.png";
                RenderPage(window, Path.Combine(dir, fileName));
                index[$"{entry.Section} / {entry.Title}"] = fileName;
                _logger.LogInformation("Captured {Page} → {File}", entry.Title, fileName);
            }

            var overlayFile = "overlay.png";
            await CaptureOverlayAsync(Path.Combine(dir, overlayFile), cancellationToken);
            index["Overlay"] = overlayFile;

            // A tray failure must not cost the page screenshots, so they and index.json are still written, but the
            // run fails at the end: the README shows the tray image, so a missing one has to be noticed.
            var trayFile = "tray.png";
            string? trayError = null;
            try
            {
                await Tray.TrayScreenshot.CaptureAsync(_services, Path.Combine(dir, trayFile), cancellationToken);
                index["Tray"] = trayFile;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "The tray popup could not be captured.");
                trayError = ex.Message;
            }

            var json = JsonSerializer.Serialize(new { generated = DateTimeOffset.Now, width = WindowWidth, height = WindowHeight, pages = index },
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(dir, "index.json"), json, cancellationToken);
            _logger.LogInformation("Screenshot run finished: {Count} images.", index.Count);
            if (trayError is not null)
            {
                WriteError($"The tray popup ({trayFile}) could not be captured: {trayError}");
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Screenshot run failed.");
            WriteError("Screenshot run failed: " + ex.Message);
            return 1;
        }
    }

    private static void WriteError(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch
        {
            // No console attached.
        }
    }

    private async Task CaptureOverlayAsync(string path, CancellationToken cancellationToken)
    {
        var overlay = _services.GetRequiredService<OverlayWindow>();
        overlay.Left = -10000;
        overlay.Top = -10000;
        overlay.Show();
        await IdleAsync(overlay.Dispatcher);
        await Task.Delay(400, cancellationToken);

        var root = overlay.Content as FrameworkElement ?? throw new InvalidOperationException("The overlay window has no content to render.");
        const int width = 960, height = 540;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var gradient = new LinearGradientBrush(Color.FromRgb(0x12, 0x16, 0x24), Color.FromRgb(0x05, 0x06, 0x0A), new Point(0, 0), new Point(1, 1));
            dc.DrawRectangle(gradient, null, new Rect(0, 0, width, height));
            double w = Math.Max(1, root.ActualWidth);
            double h = Math.Max(1, root.ActualHeight);
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(24, 24, w, h));
        }

        RenderToPng(visual, width, height, path);
        overlay.Hide();
    }

    /// <summary>Renders the window content over the theme's background so the PNG never has transparent areas.</summary>
    private static void RenderPage(Window window, string path)
    {
        if (window.Content is not FrameworkElement content)
        {
            throw new InvalidOperationException("The main window has no content to render.");
        }

        content.UpdateLayout();
        var size = new Size(Math.Max(1, content.ActualWidth), Math.Max(1, content.ActualHeight));
        var background = Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush
            ?? window.Background
            ?? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(size));
        }

        RenderToPng(visual, size.Width, size.Height, path);
    }

    /// <summary>
    /// CI runners have screens smaller than the screenshot size and Windows caps a window at the screen size.
    /// For screenshot runs only, answer WM_GETMINMAXINFO with a larger maximum so the window really is 1440 x 900.
    /// </summary>
    private static void AllowLargerThanScreen(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            const int WmGetMinMaxInfo = 0x0024;
            if (msg == WmGetMinMaxInfo)
            {
                var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                info.MaxTrackSize = new NativePoint { X = Math.Max(info.MaxTrackSize.X, WindowWidth + 64), Y = Math.Max(info.MaxTrackSize.Y, WindowHeight + 64) };
                info.MaxSize = new NativePoint { X = Math.Max(info.MaxSize.X, WindowWidth + 64), Y = Math.Max(info.MaxSize.Y, WindowHeight + 64) };
                Marshal.StructureToPtr(info, lParam, false);
                // Leave handled false: WPF's own handler reads these raised values into the limits it lays out with.
            }

            return IntPtr.Zero;
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    private static void RenderToPng(Visual visual, double width, double height, string path)
    {
        int w = Math.Max(1, (int)Math.Ceiling(width));
        int h = Math.Max(1, (int)Math.Ceiling(height));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Task IdleAsync(Dispatcher dispatcher) =>
        dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow - started > timeout)
            {
                throw new TimeoutException("The page did not finish loading in time.");
            }

            await Task.Delay(100, cancellationToken);
        }
    }
}
