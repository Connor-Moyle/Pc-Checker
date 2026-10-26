using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class AssistantPage
{
    public AssistantPage(AssistantViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public AssistantViewModel ViewModel { get; }
}
