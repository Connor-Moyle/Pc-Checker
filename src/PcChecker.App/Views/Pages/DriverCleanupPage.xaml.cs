using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class DriverCleanupPage
{
    public DriverCleanupPage(DriverCleanupViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public DriverCleanupViewModel ViewModel { get; }
}
