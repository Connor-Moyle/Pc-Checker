using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class CheckupPage
{
    public CheckupPage(CheckupViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public CheckupViewModel ViewModel { get; }
}
