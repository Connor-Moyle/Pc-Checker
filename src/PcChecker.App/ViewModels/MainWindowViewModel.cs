using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Settings;
using PcChecker.Core.Updates;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>State for the shell window: version, advanced mode and navigation helpers.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;

    [ObservableProperty]
    private string _appVersion;

    [ObservableProperty]
    private string _title = "PC Checker";

    /// <summary>Bound two-way by the Advanced mode toggle in the navigation footer; saves immediately.</summary>
    [ObservableProperty]
    private bool _advancedModeEnabled;

    private bool _syncingAdvancedMode;

    /// <summary>The section that owns the page on screen. Its pages are the tabs above the content.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tabs), nameof(HasTabs))]
    private Services.NavigationSection? _currentSection;

    /// <summary>The tab for the page on screen. Choosing another tab navigates to its page.</summary>
    [ObservableProperty]
    private Services.NavigationEntry? _selectedTab;

    private bool _syncingTabs;

    public MainWindowViewModel(ISettingsService settings, INavigationService navigation, IAppUpdateService updates)
        : base(settings)
    {
        _navigation = navigation;
        _appVersion = "v" + updates.CurrentVersion;
        _advancedModeEnabled = settings.Current.General.AdvancedMode;
        settings.Changed += (_, s) => Services.DispatcherHelper.PostToUi(() =>
        {
            _syncingAdvancedMode = true;
            try
            {
                AdvancedModeEnabled = s.General.AdvancedMode;
            }
            finally
            {
                _syncingAdvancedMode = false;
            }
        });
    }

    public IReadOnlyList<Services.NavigationEntry> Tabs => CurrentSection?.Pages ?? [];

    public bool HasTabs => CurrentSection?.HasTabs == true;

    /// <summary>Called after every navigation so the tab strip follows the page, however the page was opened.</summary>
    public void SyncToPage(Type pageType)
    {
        var section = Services.NavigationCatalog.SectionOf(pageType);
        if (section is null)
        {
            return;
        }

        _syncingTabs = true;
        try
        {
            CurrentSection = section;
            SelectedTab = section.Pages.First(p => p.PageType == pageType);
        }
        finally
        {
            _syncingTabs = false;
        }
    }

    partial void OnSelectedTabChanged(Services.NavigationEntry? value)
    {
        if (!_syncingTabs && value is not null)
        {
            _navigation.Navigate(value.PageType);
        }
    }

    public bool IsDemoMode => AppOptions.Mode == Core.Common.AppMode.Demo;

    partial void OnAdvancedModeEnabledChanged(bool value)
    {
        if (_syncingAdvancedMode || Settings.Current.General.AdvancedMode == value)
        {
            return;
        }

        Settings.Current.General.AdvancedMode = value;
        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = "Settings could not be saved: " + ex.Message;
        }
    }

    public void NavigateTo<TPage>() where TPage : class => _navigation.Navigate(typeof(TPage));

    [RelayCommand]
    private void GoToDashboard() => NavigateTo<DashboardPage>();

    [RelayCommand]
    private void GoToSettings() => NavigateTo<SettingsPage>();

    [RelayCommand]
    private void GoToAssistant() => NavigateTo<AssistantPage>();

    [RelayCommand]
    private void GoToHealth() => NavigateTo<HealthPage>();

    [RelayCommand]
    private void GoToCleanup() => NavigateTo<CleanupPage>();
}
