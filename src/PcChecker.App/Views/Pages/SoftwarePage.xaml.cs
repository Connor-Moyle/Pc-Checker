using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class SoftwarePage
{
    public SoftwarePage(SoftwareViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public SoftwareViewModel ViewModel { get; }
}
