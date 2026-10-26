using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class LightingPage
{
    public LightingPage(LightingViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public LightingViewModel ViewModel { get; }
}
