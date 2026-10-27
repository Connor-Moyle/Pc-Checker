using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class WindowsUpdatePage
{
    public WindowsUpdatePage(WindowsUpdateViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public WindowsUpdateViewModel ViewModel { get; }
}
