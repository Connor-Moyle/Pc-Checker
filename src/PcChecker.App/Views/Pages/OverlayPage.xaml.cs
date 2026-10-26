using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class OverlayPage
{
    public OverlayPage(OverlayViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public OverlayViewModel ViewModel { get; }
}
