using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class FansPage
{
    public FansPage(FansViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public FansViewModel ViewModel { get; }
}
