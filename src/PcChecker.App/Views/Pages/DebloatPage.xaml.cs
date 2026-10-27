using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class DebloatPage
{
    public DebloatPage(DebloatViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public DebloatViewModel ViewModel { get; }
}
