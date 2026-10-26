using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class LogsPage
{
    public LogsPage(LogsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public LogsViewModel ViewModel { get; }
}
