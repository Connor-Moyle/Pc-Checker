using System.Windows;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Settings;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PcChecker.App.Views.Windows;

/// <summary>Shell window: title bar, side navigation, snackbar and dialog hosts.</summary>
public partial class MainWindow : FluentWindow
{
    private readonly INavigationService _navigation;
    private readonly ISnackbarService _snackbar;
    private readonly IContentDialogService _dialogs;
    private readonly ISettingsService _settings;

    public MainWindow(MainWindowViewModel viewModel, IPageService pageService, INavigationService navigation,
        ISnackbarService snackbar, IContentDialogService dialogs, ISettingsService settings)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        _navigation = navigation;
        _snackbar = snackbar;
        _dialogs = dialogs;
        _settings = settings;

        InitializeComponent();

        NavigationView.SetPageService(pageService);
        _navigation.SetNavigationControl(NavigationView);
        _snackbar.SetSnackbarPresenter(SnackbarPresenter);
        _dialogs.SetDialogHost(DialogHost);
    }

    public MainWindowViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings.Current.General.Theme == AppTheme.System)
        {
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        }

        _navigation.Navigate(typeof(DashboardPage));
    }

    /// <summary>After each navigation, run the target page's one-time initialization.</summary>
    private void OnNavigated(NavigationView sender, NavigatedEventArgs args)
    {
        if (args.Page is FrameworkElement { DataContext: ViewModelBase viewModel })
        {
            _ = viewModel.EnsureInitializedAsync();
        }
    }
}
