using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class UpgradeAdvicePage
{
    public UpgradeAdvicePage(UpgradeAdviceViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public UpgradeAdviceViewModel ViewModel { get; }
}
