using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PcChecker.App.Services;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Settings;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PcChecker.App.Views.Windows;

/// <summary>Shell window: title bar, side navigation, quick search, snackbar and dialog hosts.</summary>
public partial class MainWindow : FluentWindow
{
    private readonly INavigationService _navigation;
    private readonly ISnackbarService _snackbar;
    private readonly IContentDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly WelcomeService _welcome;
    private FrameworkElement? _currentPage;

    public MainWindow(MainWindowViewModel viewModel, IPageService pageService, INavigationService navigation,
        ISnackbarService snackbar, IContentDialogService dialogs, ISettingsService settings, WelcomeService welcome)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        _navigation = navigation;
        _snackbar = snackbar;
        _dialogs = dialogs;
        _settings = settings;
        _welcome = welcome;

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

        NameTitleBarButtons();
        _navigation.Navigate(typeof(DashboardPage));

        // First start only (never in screenshot or scheduled runs); after the Dashboard has had its first layout.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _ = _welcome.ShowOnFirstStartAsync());
    }

    /// <summary>
    /// Window-wide keys: Esc closes an open dialog (as its Close button does), Ctrl+K opens or closes quick search,
    /// Ctrl+Tab and Ctrl+Shift+Tab (or Ctrl+Page Down and Ctrl+Page Up) switch between the section's tabs, and F6 and
    /// Shift+F6 move between the menu, the tabs and the page. See docs/ACCESSIBILITY.md.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DialogHost.Content is ContentDialog dialog)
        {
            // An open drop-down inside the dialog closes first, as usual.
            if (e.Key == Key.Escape && Keyboard.FocusedElement is not (System.Windows.Controls.ComboBox { IsDropDownOpen: true } or System.Windows.Controls.ComboBoxItem))
            {
                dialog.Hide(ContentDialogResult.None);
                e.Handled = true;
            }

            return;
        }

        // While the palette is open, its keys (Esc, arrows, Enter) work even when focus is outside it.
        if (ViewModel.QuickSearch.IsOpen && !QuickSearchPalette.IsKeyboardFocusWithin && QuickSearchPalette.HandleKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (!ViewModel.QuickSearch.IsOpen)
        {
            // A page's own tab control (Drivers, Windows Update, Disk space…) keeps Ctrl+Tab while focus is in it.
            bool tabKey = key == Key.Tab && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
            bool pageKey = (key is Key.PageDown or Key.PageUp) && modifiers == ModifierKeys.Control;
            if ((tabKey || pageKey) && !IsInsidePageTabControl(e.OriginalSource as DependencyObject) && ViewModel.SelectAdjacentTab(key == Key.PageUp || modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusSelectedTab());
                e.Handled = true;
                return;
            }

            if (key == Key.F6 && (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift))
            {
                MoveBetweenRegions(modifiers == ModifierKeys.Shift ? -1 : 1);
                e.Handled = true;
                return;
            }
        }

        if (e.Key != Key.K || Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        e.Handled = true;
        var search = ViewModel.QuickSearch;
        if (search.IsOpen)
        {
            search.CloseCommand.Execute(null);
        }
        else
        {
            search.OpenCommand.Execute(null);
        }
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

        _currentPage = page;
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

    /// <summary>F6: the side menu, then the section tabs (when there are any), then the page, and round again.</summary>
    private void MoveBetweenRegions(int offset)
    {
        Func<bool>[] regions = [FocusMenu, FocusSelectedTab, FocusPage];
        int current = SectionTabs.IsKeyboardFocusWithin ? 1 : _currentPage?.IsKeyboardFocusWithin == true ? 2 : 0;
        for (int step = 1; step <= regions.Length; step++)
        {
            if (regions[Core.Accessibility.AccessibleText.Cycle(current, offset * step, regions.Length)]())
            {
                return;
            }
        }
    }

    private bool FocusMenu()
    {
        var items = NavigationView.MenuItems.Cast<object>().Concat(NavigationView.FooterMenuItems.Cast<object>()).OfType<NavigationViewItem>().ToList();
        var target = items.FirstOrDefault(i => i.IsActive) ?? items.FirstOrDefault();
        return target?.Focus() == true;
    }

    private bool FocusSelectedTab()
    {
        if (!ViewModel.HasTabs || ViewModel.SelectedTab is null)
        {
            return false;
        }

        return SectionTabs.ItemContainerGenerator.ContainerFromItem(ViewModel.SelectedTab) is System.Windows.Controls.ListBoxItem item && item.Focus();
    }

    private static bool IsInsidePageTabControl(DependencyObject? element)
    {
        for (var node = element; node is not null; node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.TabControl)
            {
                return true;
            }

            if (node is System.Windows.Controls.Page)
            {
                return false;
            }
        }

        return false;
    }

    private bool FocusPage() =>
        _currentPage is not null && _currentPage.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

    /// <summary>The title bar's caption buttons come without names; give them the ones Windows uses.</summary>
    private void NameTitleBarButtons()
    {
        TitleBar.ApplyTemplate();
        (string Part, string Name)[] buttons =
        [
            ("PART_HelpButton", Localization.Loc.T("Help")),
            ("PART_MinimizeButton", Localization.Loc.T("Minimize")),
            ("PART_MaximizeButton", Localization.Loc.T("Maximize")),
            ("PART_CloseButton", Localization.Loc.T("Close")),
        ];
        foreach (var (part, name) in buttons)
        {
            if (TitleBar.Template?.FindName(part, TitleBar) is DependencyObject button &&
                string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(button)))
            {
                System.Windows.Automation.AutomationProperties.SetName(button, name);
            }
        }
    }
}
