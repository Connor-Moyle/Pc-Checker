using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class RegistryPage
{
    public RegistryPage(RegistryViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public RegistryViewModel ViewModel { get; }
}
