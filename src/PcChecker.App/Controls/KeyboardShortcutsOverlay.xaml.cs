using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PcChecker.App.Localization;
using PcChecker.Core.Shell;

namespace PcChecker.App.Controls;

/// <summary>
/// The keyboard shortcuts overlay over the whole window: the keys that work everywhere and the extra keys of the page on
/// screen. Esc, "?" or a click outside the panel closes it, and focus goes back to where it was.
/// </summary>
public partial class KeyboardShortcutsOverlay : UserControl
{
    private IInputElement? _focusBeforeOpen;

    public KeyboardShortcutsOverlay()
    {
        InitializeComponent();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Shows the list for the page with <paramref name="pageKey"/> (titled <paramref name="pageTitle"/>).</summary>
    public void Open(string? pageKey, string? pageTitle, bool pageCanRefresh)
    {
        GlobalList.ItemsSource = KeyboardShortcutCatalog.Global
            .Where(s => pageCanRefresh || s.Keys != "F5")
            .Select(Translate)
            .ToList();
        var pageKeys = KeyboardShortcutCatalog.ForPage(pageKey).Select(Translate).ToList();
        PageList.ItemsSource = pageKeys;
        PageHeading.Text = Loc.F("On this page ({0})", pageTitle ?? string.Empty);
        PageBlock.Visibility = pageKeys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _focusBeforeOpen = Keyboard.FocusedElement;
        Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => CloseButton.Focus());
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        Visibility = Visibility.Collapsed;
        var previous = _focusBeforeOpen;
        _focusBeforeOpen = null;
        if (previous is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } element)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => element.Focus());
        }
    }

    private static KeyboardShortcut Translate(KeyboardShortcut shortcut) => shortcut with { Description = Loc.T(shortcut.Description) };

    private void OnOverlayKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    /// <summary>"?" closes the list again; typed text is used so it works on every keyboard layout.</summary>
    private void OnOverlayTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text == "?")
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnBackdropClicked(object sender, MouseButtonEventArgs e) => Close();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
