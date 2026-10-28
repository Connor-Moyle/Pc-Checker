using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class GameReadyPage
{
    public GameReadyPage(GameReadyViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public GameReadyViewModel ViewModel { get; }
}
