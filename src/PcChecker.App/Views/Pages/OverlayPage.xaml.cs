using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class OverlayPage
{
    public OverlayPage(OverlayViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => ViewModel.OnPageLoaded();
        Unloaded += (_, _) => ViewModel.OnPageUnloaded();
    }

    public OverlayViewModel ViewModel { get; }
}
