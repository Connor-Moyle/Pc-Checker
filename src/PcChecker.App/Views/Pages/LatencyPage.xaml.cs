using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class LatencyPage
{
    public LatencyPage(LatencyViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public LatencyViewModel ViewModel { get; }
}
