using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class SecurityPage
{
    public SecurityPage(SecurityViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public SecurityViewModel ViewModel { get; }
}
