using System.Windows;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class DeviceTestsPage
{
    public DeviceTestsPage(DeviceTestsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => ViewModel.OnPageShown();
        Unloaded += (_, _) => ViewModel.OnPageHidden();
        IsVisibleChanged += OnVisibleChanged;
    }

    public DeviceTestsViewModel ViewModel { get; }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // The window going to the tray hides the page without unloading it; stop the live tests then too.
        if (e.NewValue is true)
        {
            ViewModel.OnPageShown();
        }
        else
        {
            ViewModel.OnPageHidden();
        }
    }
}
