using System.Windows;
using System.Windows.Threading;
using PcChecker.App.Services;
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
        BuildMenu();

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

    /// <summary>One menu item per section; pages inside a section are reached through the tabs above the content.</summary>
    private void BuildMenu()
    {
        foreach (var section in NavigationCatalog.Sections)
        {
            var item = new NavigationViewItem
            {
                Content = Localization.Loc.T(section.Title),
                Icon = new SymbolIcon { Symbol = section.Icon },
                TargetPageType = section.FirstPage,
                ToolTip = section.HasTabs ? string.Join(", ", section.Pages.Select(p => Localization.Loc.T(p.Title))) : null,
            };
            (section.IsFooter ? NavigationView.FooterMenuItems : NavigationView.MenuItems).Add(item);
        }
    }

    /// <summary>After each navigation, run the target page's one-time initialization and update the tabs.</summary>
    private void OnNavigated(NavigationView sender, NavigatedEventArgs args)
    {
        if (args.Page is FrameworkElement { DataContext: ViewModelBase viewModel })
        {
            _ = viewModel.EnsureInitializedAsync();
        }

        if (args.Page is not FrameworkElement page)
        {
            return;
        }

        var pageType = page.GetType();
        ViewModel.SyncToPage(pageType);

        // A page opened from a tab has no menu item of its own, so the navigation control clears the highlight.
        // Once it has finished, highlight the item of the section that owns the page.
        var section = NavigationCatalog.SectionOf(pageType);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            foreach (var item in NavigationView.MenuItems.Cast<object>().Concat(NavigationView.FooterMenuItems.Cast<object>()).OfType<NavigationViewItem>())
            {
                item.SetCurrentValue(NavigationViewItem.IsActiveProperty, section is not null && item.TargetPageType == section.FirstPage);
            }
        });
    }
}
