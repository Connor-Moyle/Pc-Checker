using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class DashboardPage
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public DashboardViewModel ViewModel { get; }
}
