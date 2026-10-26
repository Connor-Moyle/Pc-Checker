using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class InputPage
{
    public InputPage(InputViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public InputViewModel ViewModel { get; }
}
