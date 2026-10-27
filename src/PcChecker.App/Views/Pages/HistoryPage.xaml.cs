using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class HistoryPage
{
    public HistoryPage(HistoryViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public HistoryViewModel ViewModel { get; }
}
