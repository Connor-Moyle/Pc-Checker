using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PcChecker.App.Helpers;
using PcChecker.App.Services;
using PcChecker.App.ViewModels;
using PcChecker.Core.Common;

namespace PcChecker.App.Views.Windows;

/// <summary>
/// Click-through, always-on-top, never-activated window that hosts the <c>OverlayPanel</c>. The code here is
/// limited to the Win32 interop a transparent overlay needs (extended styles, topmost, monitor work area, drag);
/// behaviour lives in <see cref="OverlayService"/> and <see cref="OverlayWindowViewModel"/>.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int ClickThroughStyles = NativeMethods.WsExTransparent;
    private const int BaseStyles = NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate | NativeMethods.WsExLayered;

    private bool _clickThrough = true;

    public OverlayWindow(IOverlayService overlayService)
    {
        ViewModel = overlayService.WindowViewModel;
        DataContext = ViewModel;
        InitializeComponent();
    }

    public OverlayWindowViewModel ViewModel { get; }

    /// <summary>Raised after the user finished dragging the overlay in edit mode (Left/Top hold the new position).</summary>
    public event EventHandler? DragCompleted;

    /// <summary>The native handle, or zero before the window has been created.</summary>
    public IntPtr Handle
    {
        get
        {
            try
            {
                return new WindowInteropHelper(this).Handle;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }

    /// <summary>Lets mouse clicks pass through to the game (true) or makes the overlay draggable (false).</summary>
    public void SetClickThrough(bool clickThrough)
    {
        _clickThrough = clickThrough;
        var handle = Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (clickThrough)
            {
                NativeMethods.AddExtendedStyle(handle, ClickThroughStyles);
            }
            else
            {
                NativeMethods.RemoveExtendedStyle(handle, ClickThroughStyles);
            }
        }
        catch
        {
            // Style changes are cosmetic; the window still works without them.
        }
    }

    /// <summary>Puts the window back above everything (games re-assert their own topmost state).</summary>
    public void ReassertTopmost()
    {
        try
        {
            NativeMethods.BringToTopmost(Handle);
        }
        catch
        {
            // Ignore: Topmost="True" remains set on the WPF side.
        }
    }

    /// <summary>
    /// Work area of the monitor under the mouse (or the primary one) in device-independent pixels, so it can be
    /// compared with <see cref="Window.Left"/>/<see cref="Window.Top"/>.
    /// </summary>
    public OverlayRect GetWorkAreaDips()
    {
        try
        {
            var physical = NativeMethods.QueryWorkAreaUnderCursor();
            if (physical is { } r)
            {
                var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
                if (transform is { } m)
                {
                    var topLeft = m.Transform(new Point(r.Left, r.Top));
                    var bottomRight = m.Transform(new Point(r.Right, r.Bottom));
                    return new OverlayRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
                }

                double scale = 96.0 / Math.Max(1, VisualTreeHelper.GetDpi(this).PixelsPerInchX);
                return new OverlayRect(r.Left * scale, r.Top * scale, (r.Right - r.Left) * scale, (r.Bottom - r.Top) * scale);
            }
        }
        catch
        {
            // Fall through to the primary work area.
        }

        var area = SystemParameters.WorkArea;
        return new OverlayRect(area.Left, area.Top, area.Width, area.Height);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            NativeMethods.AddExtendedStyle(Handle, BaseStyles);
            SetClickThrough(_clickThrough);
        }
        catch
        {
            // Without the styles the overlay still shows; it may just take focus when clicked.
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The window is a singleton that is shown and hidden; never let it be destroyed by Alt+F4 or similar.
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private void OnPanelMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ViewModel.IsEditMode || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws when the button was released before the call; nothing to do.
            return;
        }

        DragCompleted?.Invoke(this, EventArgs.Empty);
    }
}
