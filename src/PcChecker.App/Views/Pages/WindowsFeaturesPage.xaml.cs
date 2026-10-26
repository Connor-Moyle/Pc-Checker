using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class WindowsFeaturesPage
{
    public WindowsFeaturesPage(WindowsFeaturesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public WindowsFeaturesViewModel ViewModel { get; }
}
