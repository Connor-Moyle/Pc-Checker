using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class UsersPage
{
    public UsersPage(UsersViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public UsersViewModel ViewModel { get; }
}
