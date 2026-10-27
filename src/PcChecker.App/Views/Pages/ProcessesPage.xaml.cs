using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

/// <summary>
/// The Processes page. Code-behind only handles view concerns: showing the chosen columns, the column and update speed
/// menus, sorting arrows, and the Task Manager keys (Enter, Delete, Ctrl+F, Ctrl+C, Left and Right).
/// </summary>
public partial class ProcessesPage
{
    private readonly ContextMenu _headerColumnsMenu;

    public ProcessesPage(ProcessesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ColumnsButton.Flyout = BuildColumnsMenu();
        SpeedButton.Flyout = BuildSpeedMenu();
        _headerColumnsMenu = BuildColumnsMenu();
        ViewModel.ColumnsChanged += (_, _) => ApplyColumns();
        ApplyColumns();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ProcessesViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectionRequested += OnSelectionRequested;
        ViewModel.StartLive();
        ViewModel.RefreshTaskManagerReplacement();

        // Arrived from "Go to the process": show the selected row.
        if (ViewModel.SelectedRow is { } selected)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => ProcessGrid.ScrollIntoView(selected));
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectionRequested -= OnSelectionRequested;
        ViewModel.StopLive();
    }

    private void OnSelectionRequested(object? sender, RunningProcessRow row) => ProcessGrid.ScrollIntoView(row);

    /// <summary>Shows the grid columns that are ticked for the current view (the column's sort key is its id).</summary>
    private void ApplyColumns()
    {
        foreach (var column in ProcessGrid.Columns)
        {
            column.Visibility = ViewModel.IsColumnVisible(column.SortMemberPath) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>One checkable item per column, then "Reset to default columns".</summary>
    private ContextMenu BuildColumnsMenu()
    {
        var menu = new ContextMenu { DataContext = ViewModel };
        foreach (var option in ViewModel.ColumnOptions)
        {
            var item = new MenuItem
            {
                Header = option.Header,
                ToolTip = option.Description,
                IsCheckable = true,
                StaysOpenOnClick = true,
                IsEnabled = option.CanHide,
                DataContext = option,
            };
            item.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(ProcessColumnOption.IsVisible)) { Mode = BindingMode.TwoWay });
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = Localization.Loc.T("Reset to default columns"),
            Command = ViewModel.ResetColumnsCommand,
        });
        return menu;
    }

    /// <summary>High, Normal, Low and Paused, with a tick on the current speed.</summary>
    private ContextMenu BuildSpeedMenu()
    {
        var menu = new ContextMenu { DataContext = ViewModel };
        foreach (var option in ViewModel.SpeedOptions)
        {
            var item = new MenuItem
            {
                Header = option.Label,
                ToolTip = option.Description,
                Command = ViewModel.SetUpdateSpeedCommand,
                CommandParameter = option.Speed,
                DataContext = option,
            };
            item.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(UpdateSpeedOption.IsSelected)) { Mode = BindingMode.OneWay });
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>The view model sorts (the list is rebuilt every refresh); the grid only shows the arrow.</summary>
    private void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        bool descending = ViewModel.SortBy(e.Column.SortMemberPath);
        foreach (var column in ProcessGrid.Columns)
        {
            column.SortDirection = null;
        }

        e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
    }

    /// <summary>Right-click selects the row under the pointer so the menu acts on it.</summary>
    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { } row)
        {
            row.IsSelected = true;
        }
    }

    /// <summary>Right-click on a column title opens the column chooser instead of the program actions.</summary>
    private void OnGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is null)
        {
            return;
        }

        e.Handled = true;
        _headerColumnsMenu.PlacementTarget = ProcessGrid;
        _headerColumnsMenu.Placement = PlacementMode.MousePoint;
        _headerColumnsMenu.IsOpen = true;
    }

    /// <summary>Double-click opens or closes a group, or switches to a program's window.</summary>
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext is not RunningProcessRow row)
        {
            return;
        }

        if (row.IsGroup)
        {
            ViewModel.ToggleGroupCommand.Execute(row);
        }
        else if (ViewModel.SwitchToCommand.CanExecute(null))
        {
            ViewModel.SwitchToCommand.Execute(null);
        }
    }

    /// <summary>Task Manager keys: Ctrl+F searches anywhere on the page; Enter, Delete, Ctrl+C, Left and Right act on the list.</summary>
    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key is Key.F or Key.E)
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            e.Handled = true;
            return;
        }

        if (!ProcessGrid.IsKeyboardFocusWithin)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when Run(ViewModel.SwitchToCommand):
            case Key.Delete when Run(ViewModel.EndTaskCommand):
            case Key.C when ctrl && Run(ViewModel.CopyDetailsCommand):
                e.Handled = true;
                break;
            case Key.Right when ViewModel.SelectedRow is { IsGroup: true, IsExpanded: false }:
                ViewModel.SetSelectedExpanded(true);
                e.Handled = true;
                break;
            case Key.Left when ViewModel.SelectedRow is { IsGroup: true, IsExpanded: true }:
                ViewModel.SetSelectedExpanded(false);
                e.Handled = true;
                break;
        }
    }

    private static bool Run(ICommand command)
    {
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return element as T;
    }
}
