using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class AppHistoryPage
{
    public AppHistoryPage(AppHistoryViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public AppHistoryViewModel ViewModel { get; }

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
