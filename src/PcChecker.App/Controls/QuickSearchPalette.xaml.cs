using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Controls;

/// <summary>
/// The quick search palette. Keys are handled for the whole palette: Up and Down move the highlight, Page Up and Page Down jump,
/// Enter opens, Esc closes. The main window forwards these keys too when focus is elsewhere (after a click on the title
/// bar, say), see <see cref="HandleKey"/>. Clicking a result opens it; clicking outside the panel closes it. When the
/// palette is closed without a choice, focus goes back to where it was before it opened.
/// </summary>
public partial class QuickSearchPalette : UserControl
{
    private QuickSearchViewModel? _viewModel;
    private WeakReference<IInputElement>? _focusBeforeOpen;

    public QuickSearchPalette()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Opened -= OnOpened;
            _viewModel.Dismissed -= OnDismissed;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as QuickSearchViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Opened += OnOpened;
            _viewModel.Dismissed += OnDismissed;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>Focus the search box once the palette is visible (it is collapsed until then and cannot take focus).</summary>
    private void OnOpened(object? sender, EventArgs e)
    {
        // Remember where the keyboard was, unless it is already inside the palette.
        var focused = Keyboard.FocusedElement;
        _focusBeforeOpen = focused is DependencyObject d && !IsAncestorOf(d) ? new WeakReference<IInputElement>(focused) : null;
        FocusSearchBox();
    }

    private void FocusSearchBox() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        });

    /// <summary>Gives focus back to the element that had it before the palette opened, if it can still take it.</summary>
    private void OnDismissed(object? sender, EventArgs e)
    {
        var previous = _focusBeforeOpen;
        _focusBeforeOpen = null;
        if (previous is null || !previous.TryGetTarget(out var element))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (element is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } or ContentElement { IsEnabled: true, Focusable: true })
            {
                element.Focus();
                Keyboard.Focus(element);
            }
        });
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickSearchViewModel.Selected) && _viewModel?.Selected is { } selected)
        {
            ResultList.ScrollIntoView(selected);
        }
    }

    private void OnPaletteKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleKey(e.Key))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Handles a palette key (arrows, Page Up/Down, Enter, Esc) wherever keyboard focus is; returns true when the key was
    /// used. If focus had left the palette, it is put back in the search box so typing continues there.
    /// </summary>
    public bool HandleKey(Key key)
    {
        if (_viewModel is not { IsOpen: true })
        {
            return false;
        }

        if (key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Enter && !IsKeyboardFocusWithin)
        {
            FocusSearchBox();
        }

        switch (key)
        {
            case Key.Down:
                _viewModel.MoveSelection(1);
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                break;
            case Key.PageDown:
                _viewModel.MoveSelection(5);
                break;
            case Key.PageUp:
                _viewModel.MoveSelection(-5);
                break;
            case Key.Enter:
                _ = _viewModel.ActivateCommand.ExecuteAsync(null);
                break;
            case Key.Escape:
                _viewModel.CloseCommand.Execute(null);
                break;
            default:
                return false;
        }

        return true;
    }

    private void OnResultClicked(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is not null && sender is ListBoxItem { DataContext: QuickSearchRow row })
        {
            e.Handled = true;
            _ = _viewModel.ActivateCommand.ExecuteAsync(row);
        }
    }

    private void OnBackdropClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel?.CloseCommand.Execute(null);
    }
}
