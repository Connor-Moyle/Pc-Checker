using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class OverclockPage
{
    public OverclockPage(OverclockViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => ViewModel.OnPageLoaded();
        Unloaded += (_, _) => ViewModel.OnPageUnloaded();
    }

    public OverclockViewModel ViewModel { get; }
}
