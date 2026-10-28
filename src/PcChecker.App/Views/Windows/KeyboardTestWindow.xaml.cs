using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using PcChecker.App.ViewModels;
using PcChecker.Core.DeviceTests;

namespace PcChecker.App.Views.Windows;

/// <summary>
/// Keyboard test. Key messages for this window are read straight from the message loop
/// (<see cref="ComponentDispatcher.ThreadPreprocessMessage"/>) before WPF sees them, so every key reaches the test,
/// including Tab, the arrows, F10 and Alt, and none of them presses a button. Alt+F4 is left alone so the window can
/// still be closed from the keyboard.
/// </summary>
public partial class KeyboardTestWindow
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkF4 = 0x73;

    private IntPtr _handle;

    public KeyboardTestWindow(KeyboardTestViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Deactivated += (_, _) => ViewModel.FocusLost();
        Closed += (_, _) => ComponentDispatcher.ThreadPreprocessMessage -= OnPreprocessMessage;
    }

    public KeyboardTestViewModel ViewModel { get; }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        ComponentDispatcher.ThreadPreprocessMessage += OnPreprocessMessage;
        try
        {
            ViewModel.ApplyKeyNames(KeyName);
        }
        catch
        {
            // The US labels stay; the test works the same.
        }
    }

    private void OnPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (handled || msg.hwnd != _handle || msg.message is not (WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp))
        {
            return;
        }

        int vk = (int)msg.wParam.ToInt64();
        if (msg.message == WmSysKeyDown && vk == VkF4)
        {
            return;
        }

        var (scan, extended, _) = KeyCodes.SplitLParam(msg.lParam.ToInt64());
        if (KeyCodes.FromMessage(vk, scan, extended) is { } code)
        {
            string? name = (code & KeyCodes.VirtualOnly) != 0 ? null : KeyName(code);
            if (msg.message is WmKeyDown or WmSysKeyDown)
            {
                ViewModel.KeyDown(code, name);
            }
            else
            {
                ViewModel.KeyUp(code, name);
            }
        }

        handled = true;
    }

    /// <summary>Windows' own name for a physical key in the current keyboard language, or null.</summary>
    private static string? KeyName(int code)
    {
        try
        {
            int lParam = ((code & 0xFF) << 16) | ((code & KeyCodes.Extended) != 0 ? 1 << 24 : 0);
            var buffer = new StringBuilder(64);
            int length = GetKeyNameText(lParam, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString(0, length) : null;
        }
        catch
        {
            return null;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetKeyNameTextW")]
    private static extern int GetKeyNameText(int lParam, StringBuilder buffer, int size);
}
