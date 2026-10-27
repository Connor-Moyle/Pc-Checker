using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class HistoryPage
{
    public HistoryPage(HistoryViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        IsVisibleChanged += (_, e) => ViewModel.OnPageVisibilityChanged(e.NewValue is true);
    }

    public HistoryViewModel ViewModel { get; }
}
