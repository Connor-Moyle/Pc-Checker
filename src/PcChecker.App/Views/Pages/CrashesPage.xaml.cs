using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class CrashesPage
{
    public CrashesPage(CrashesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public CrashesViewModel ViewModel { get; }
}
