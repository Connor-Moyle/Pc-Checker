using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class PowerPage
{
    public PowerPage(PowerViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public PowerViewModel ViewModel { get; }
}
