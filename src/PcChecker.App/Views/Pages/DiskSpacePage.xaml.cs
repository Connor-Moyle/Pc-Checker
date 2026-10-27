using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class DiskSpacePage
{
    public DiskSpacePage(DiskSpaceViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public DiskSpaceViewModel ViewModel { get; }
}
