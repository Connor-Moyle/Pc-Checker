using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class MissingDllsPage
{
    public MissingDllsPage(MissingDllsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public MissingDllsViewModel ViewModel { get; }
}
