using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class AudioPage
{
    public AudioPage(AudioViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public AudioViewModel ViewModel { get; }
}
