using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class ProcessesPage
{
    public ProcessesPage(ProcessesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
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
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not DataGridRow)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        if (element is DataGridRow row)
        {
            row.IsSelected = true;
        }
    }
}
