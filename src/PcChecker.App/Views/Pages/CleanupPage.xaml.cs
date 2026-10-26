using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class CleanupPage
{
    public CleanupPage(CleanupViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public CleanupViewModel ViewModel { get; }
}
