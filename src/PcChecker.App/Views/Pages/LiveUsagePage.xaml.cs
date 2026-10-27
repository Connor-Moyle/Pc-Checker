using System.Windows;
using PcChecker.App.Services;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class LiveUsagePage
{
    private Window? _host;

    public LiveUsagePage(LiveUsageViewModel viewModel, CompactUsageWindowService compactWindow)
    {
        ViewModel = viewModel;
        DataContext = viewModel;

        // Created with the page so the view model's "Compact view" request always has a window service listening.
        _ = compactWindow;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateLive();
    }

    public LiveUsageViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Hidden to the tray turns IsVisible off; minimized does not, so watch the window's state too. The compact
        // window is a viewer of its own and keeps the readings going while it is open.
        _host = Window.GetWindow(this);
        if (_host is not null)
        {
            _host.StateChanged += OnHostStateChanged;
        }

        UpdateLive();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_host is not null)
        {
            _host.StateChanged -= OnHostStateChanged;
            _host = null;
        }

        ViewModel.StopLive(this);
    }

    private void OnHostStateChanged(object? sender, EventArgs e) => UpdateLive();

    /// <summary>Reads once a second only while the page can actually be seen.</summary>
    private void UpdateLive()
    {
        bool showing = IsLoaded && IsVisible && _host?.WindowState != WindowState.Minimized;
        if (showing)
        {
            ViewModel.StartLive(this);
        }
        else
        {
            ViewModel.StopLive(this);
        }
    }
}
