using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class MemoryPage
{
    public MemoryPage(MemoryViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public MemoryViewModel ViewModel { get; }
}
