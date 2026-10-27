using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class BenchmarkPage
{
    public BenchmarkPage(BenchmarkViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public BenchmarkViewModel ViewModel { get; }
}
