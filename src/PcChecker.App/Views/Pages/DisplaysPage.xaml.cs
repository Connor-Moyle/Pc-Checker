using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class DisplaysPage
{
    public DisplaysPage(DisplaysViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public DisplaysViewModel ViewModel { get; }
}
