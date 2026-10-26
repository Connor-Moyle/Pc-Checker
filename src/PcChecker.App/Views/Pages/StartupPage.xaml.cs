using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class StartupPage
{
    public StartupPage(StartupViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public StartupViewModel ViewModel { get; }
}
