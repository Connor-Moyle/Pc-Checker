using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PcChecker.Core.Sensors;

namespace PcChecker.App.Tray;

/// <summary>Renders the tray popup above a desktop-like backdrop with a taskbar strip, for the screenshot runner.</summary>
public static class TrayScreenshot
{
    public const int Width = 720;
    public const int Height = 640;
    private const double TaskbarHeight = 48;

    public static async Task CaptureAsync(IServiceProvider services, string path, CancellationToken cancellationToken)
    {
        var sensors = services.GetRequiredService<ISensorService>();
        sensors.Start();
        for (int i = 0; i < 30 && sensors.Latest is null; i++)
        {
            await Task.Delay(100, cancellationToken);
        }

        var tray = services.GetRequiredService<TrayIconService>();
        var popup = tray.ShowPopupForCapture();
        await popup.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;
        await Task.Delay(300, cancellationToken);
        popup.ViewModel.Refresh(overlayVisible: false);
        await popup.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;

        try
        {
            var root = popup.Content as FrameworkElement ?? throw new InvalidOperationException("The tray popup has no content to render.");
            root.UpdateLayout();
            double w = Math.Max(1, root.ActualWidth + root.Margin.Left + root.Margin.Right);
            double h = Math.Max(1, root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var desktop = new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x4A), Color.FromRgb(0x07, 0x09, 0x12), new Point(0, 0), new Point(1, 1));
                dc.DrawRectangle(desktop, null, new Rect(0, 0, Width, Height));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1C)), null, new Rect(0, Height - TaskbarHeight, Width, TaskbarHeight));
                double left = Width - w - 12;
                double top = Height - TaskbarHeight - h;
                dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(left + root.Margin.Left, top + root.Margin.Top, root.ActualWidth, root.ActualHeight));
            }

            var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            popup.Hide();
        }
    }
}
