using System.Windows;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class AudioPage
{
    public AudioPage(AudioViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public AudioViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.StartLiveMeters();

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.StopLiveMeters();
}
