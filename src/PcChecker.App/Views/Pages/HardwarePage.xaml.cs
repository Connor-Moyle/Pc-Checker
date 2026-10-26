using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class HardwarePage
{
    public HardwarePage(HardwareViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public HardwareViewModel ViewModel { get; }
}
