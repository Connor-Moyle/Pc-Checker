using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class GamesPage
{
    public GamesPage(GamesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public GamesViewModel ViewModel { get; }
}
