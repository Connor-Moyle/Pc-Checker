using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class NetworkPage
{
    public NetworkPage(NetworkViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public NetworkViewModel ViewModel { get; }
}
