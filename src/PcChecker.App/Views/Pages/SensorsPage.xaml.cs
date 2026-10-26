using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class SensorsPage
{
    public SensorsPage(SensorsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public SensorsViewModel ViewModel { get; }
}
