using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class PrivacyPage
{
    public PrivacyPage(PrivacyViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public PrivacyViewModel ViewModel { get; }
}
