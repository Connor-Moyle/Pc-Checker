using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PcChecker.App.ViewModels;

namespace PcChecker.App.Views.Pages;

public partial class ConnectionsPage
{
    private Window? _host;

    public ConnectionsPage(ConnectionsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateLive();
    }

    public ConnectionsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Hidden to the tray turns IsVisible off; minimized does not, so watch the window's state too.
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

    /// <summary>Reads every two seconds only while the page can actually be seen.</summary>
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

    /// <summary>Right-click selects the row under the pointer so the menu acts on it.</summary>
    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not DataGridRow)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        if (element is DataGridRow row)
        {
            row.IsSelected = true;
        }
    }
}
