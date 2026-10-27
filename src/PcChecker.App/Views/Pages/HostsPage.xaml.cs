using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class HostsPage
{
    public HostsPage(HostsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public HostsViewModel ViewModel { get; }

    /// <summary>Right-click selects the row under the pointer so the menu acts on it.</summary>
    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e.OriginalSource) is { } row)
        {
            row.IsSelected = true;
        }
    }

    /// <summary>Double-clicking a row opens the edit box (but not when the tick box was clicked).</summary>
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<CheckBox>(source) is null
            && RowUnder(e.OriginalSource) is not null && ViewModel.EditEntryCommand.CanExecute(null))
        {
            ViewModel.EditEntryCommand.Execute(null);
        }
    }

    private static DataGridRow? RowUnder(object source) => FindParent<DataGridRow>(source as DependencyObject);

    private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return element as T;
    }
}
