using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class BatteryPage
{
    public BatteryPage(BatteryViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public BatteryViewModel ViewModel { get; }
}
