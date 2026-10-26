using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class HealthPage
{
    public HealthPage(HealthViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public HealthViewModel ViewModel { get; }
}
