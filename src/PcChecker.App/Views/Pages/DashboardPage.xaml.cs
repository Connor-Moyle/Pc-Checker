using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class DashboardPage
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Coming back to the Dashboard (from Startup, Cleanup, Fans…) refreshes What needs attention.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                ViewModel.OnPageShown();
            }
        };
    }

    public DashboardViewModel ViewModel { get; }
}
