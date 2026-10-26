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

    public MainWindowViewModel(ISettingsService settings, INavigationService navigation, IAppUpdateService updates)
        : base(settings)
    {
        _navigation = navigation;
        _appVersion = "v" + updates.CurrentVersion;
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
