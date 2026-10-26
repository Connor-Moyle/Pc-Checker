using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class OverclockPage
{
    public OverclockPage(OverclockViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public OverclockViewModel ViewModel { get; }
}
