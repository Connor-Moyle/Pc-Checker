using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Controls;

/// <summary>
/// The quick search palette. Keys are handled for the whole palette: Up and Down move the highlight, Page Up and Page Down jump,
/// Enter opens, Esc closes. Clicking a result opens it; clicking outside the panel closes it.
/// </summary>
public partial class QuickSearchPalette : UserControl
{
    private QuickSearchViewModel? _viewModel;

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
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as QuickSearchViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Opened += OnOpened;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>Focus the search box once the palette is visible (it is collapsed until then and cannot take focus).</summary>
    private void OnOpened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        });

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickSearchViewModel.Selected) && _viewModel?.Selected is { } selected)
        {
            ResultList.ScrollIntoView(selected);
        }
    }

    private void OnPaletteKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
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
                return;
        }

        e.Handled = true;
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
