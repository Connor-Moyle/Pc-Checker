using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class LeftoversPage
{
    public LeftoversPage(LeftoversViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public LeftoversViewModel ViewModel { get; }
}
