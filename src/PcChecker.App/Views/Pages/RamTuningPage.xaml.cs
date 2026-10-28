using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class RamTuningPage
{
    public RamTuningPage(RamTuningViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public RamTuningViewModel ViewModel { get; }
}
