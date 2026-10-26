using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class OptimizePage
{
    public OptimizePage(OptimizeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public OptimizeViewModel ViewModel { get; }
}
