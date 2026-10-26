using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class PerformancePage
{
    public PerformancePage(PerformanceViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public PerformanceViewModel ViewModel { get; }
}
