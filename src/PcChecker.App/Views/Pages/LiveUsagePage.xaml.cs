using System.Windows;
using PcChecker.App.Services;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class LiveUsagePage
{
    public LiveUsagePage(LiveUsageViewModel viewModel, CompactUsageWindowService compactWindow)
    {
        ViewModel = viewModel;
        DataContext = viewModel;

        // Created with the page so the view model's "Compact view" request always has a window service listening.
        _ = compactWindow;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public LiveUsageViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.StartLive(this);

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.StopLive(this);
}
