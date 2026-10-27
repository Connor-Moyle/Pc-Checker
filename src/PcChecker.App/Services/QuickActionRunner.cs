using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Health;
using PcChecker.Core.Latency;
using PcChecker.Core.Performance;
using PcChecker.Core.Shell;
using Wpf.Ui;

namespace PcChecker.App.Services;

/// <summary>
/// Runs the quick actions from <see cref="QuickActionCatalog"/>: opens the page and presses the same button the user
/// would, so the page's own confirmation dialogs, busy overlay and safety steps all apply. View models are resolved
/// only when an action needs them, so nothing is created early.
/// </summary>
public sealed class QuickActionRunner
{
    private static readonly TimeSpan BusyPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan BusyWaitLimit = TimeSpan.FromSeconds(30);

    /// <summary>How long a queued action (see <c>waitWhileBusy</c>) waits for the page, e.g. for a slow start-up check.</summary>
    private static readonly TimeSpan QueuedWaitLimit = TimeSpan.FromMinutes(15);

    private readonly IServiceProvider _services;
    private readonly INavigationService _navigation;
    private readonly INotificationService _notifications;
    private readonly ILogger<QuickActionRunner> _logger;

    public QuickActionRunner(IServiceProvider services, INavigationService navigation, INotificationService notifications, ILogger<QuickActionRunner> logger)
    {
        _services = services;
        _navigation = navigation;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>
    /// Runs the action with <paramref name="actionId"/> (a <see cref="QuickActionIds"/> value). Never throws.
    /// With <paramref name="waitWhileBusy"/> the action is queued while its page is busy (for instance with the
    /// start-up health check) and runs as soon as the page is free, instead of giving up after a short wait.
    /// </summary>
    public async Task RunAsync(string actionId, bool waitWhileBusy = false)
    {
        try
        {
            await RunCoreAsync(actionId, waitWhileBusy);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quick action {Action} failed.", actionId);
            _notifications.Error(Localization.Loc.T("That did not work"), ex.Message);
        }
    }

    private Task RunCoreAsync(string actionId, bool waitWhileBusy)
    {
        var busyWait = waitWhileBusy ? QueuedWaitLimit : BusyWaitLimit;
        switch (actionId)
        {
            // In demo mode the first visit already runs the check-up.
            case QuickActionIds.Checkup:
                return RunOnPageAsync<CheckupViewModel>(typeof(CheckupPage), vm => vm.RunCommand, busyWait, firstVisitDoesIt: AppOptions.Current.IsDemo);
            case QuickActionIds.FullScan:
                return RunOnPageAsync<DashboardViewModel>(typeof(DashboardPage), vm => vm.FullScanCommand, busyWait);
            case QuickActionIds.ExportReport:
                return RunOnPageAsync<DashboardViewModel>(typeof(DashboardPage), vm => vm.ExportReportCommand, busyWait);

            // The first visit to Cleanup and Drivers already scans, so the command only runs on later visits.
            case QuickActionIds.CleanTempFiles:
                return RunOnPageAsync<CleanupViewModel>(typeof(CleanupPage), vm => vm.ScanCommand, busyWait, firstVisitDoesIt: true);
            case QuickActionIds.CheckDrivers:
                // In demo mode the first visit also checks for updates.
                return RunOnPageAsync<DriversViewModel>(typeof(DriversPage), vm => vm.CheckForUpdatesCommand, busyWait, firstVisitDoesIt: AppOptions.Current.IsDemo);

            // Health, Speed check and Latency scan on their first visit only when there is no earlier result.
            case QuickActionIds.HealthCheck:
                return RunOnPageAsync<HealthViewModel>(typeof(HealthPage), vm => vm.AnalyzeCommand, busyWait,
                    firstVisitDoesIt: _services.GetRequiredService<IHealthService>().Last is null);
            case QuickActionIds.SpeedCheck:
                return RunOnPageAsync<PerformanceViewModel>(typeof(PerformancePage), vm => vm.ScanCommand, busyWait,
                    firstVisitDoesIt: _services.GetRequiredService<IPerformanceAnalyzer>().Last is null);
            case QuickActionIds.LatencyCheck:
                return RunOnPageAsync<LatencyViewModel>(typeof(LatencyPage), vm => vm.RunCheckCommand, busyWait,
                    firstVisitDoesIt: _services.GetRequiredService<ILatencyService>().Last is null);

            // Asks for confirmation on the Storage page before writing its test file.
            case QuickActionIds.DiskBenchmark:
                return RunOnPageAsync<StorageViewModel>(typeof(StoragePage), vm => vm.RunBenchmarkCommand, busyWait);
            case QuickActionIds.CheckAppUpdates:
                return RunOnPageAsync<SettingsViewModel>(typeof(SettingsPage), vm => vm.CheckForUpdatesCommand, busyWait);

            // Both explain themselves in a dialog first; restoring shows what changes before anything is imported.
            case QuickActionIds.BackUpSetup:
                return RunOnPageAsync<SettingsViewModel>(typeof(SettingsPage), vm => vm.ExportSetupCommand, busyWait);
            case QuickActionIds.RestoreSetup:
                return RunOnPageAsync<SettingsViewModel>(typeof(SettingsPage), vm => vm.ImportSetupCommand, busyWait);

            case QuickActionIds.Optimize:
                _navigation.Navigate(typeof(OptimizePage));
                return Task.CompletedTask;
            case QuickActionIds.ToggleOverlay:
                ToggleOverlay();
                return Task.CompletedTask;
            case QuickActionIds.ShowWelcome:
                return _services.GetRequiredService<WelcomeService>().ShowAsync();
            default:
                _logger.LogDebug("Unknown quick action {Action}.", actionId);
                return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Opens the page, waits for its first load (and for anything else the page is busy with), then runs
    /// <paramref name="command"/> unless that first load already did the same work (<paramref name="firstVisitDoesIt"/>).
    /// </summary>
    private async Task RunOnPageAsync<TViewModel>(Type pageType, Func<TViewModel, ICommand> command, TimeSpan busyWait, bool firstVisitDoesIt = false)
        where TViewModel : ViewModelBase
    {
        var viewModel = _services.GetRequiredService<TViewModel>();
        bool firstVisit = !viewModel.IsInitialized;
        _navigation.Navigate(pageType);
        await viewModel.EnsureInitializedAsync();
        if (firstVisit && firstVisitDoesIt && viewModel.IsInitialized)
        {
            return;
        }

        if (viewModel.IsBusy && busyWait > BusyWaitLimit)
        {
            _notifications.Info(Localization.Loc.T("Starting soon"), Localization.Loc.T("This page is still working on something. Your request starts as soon as it has finished."));
        }

        if (!await WaitUntilIdleAsync(viewModel, busyWait))
        {
            _notifications.Info(Localization.Loc.T("Still busy"), Localization.Loc.T("This page is still working on something. Try again when it has finished."));
            return;
        }

        var target = command(viewModel);
        if (!target.CanExecute(null))
        {
            _logger.LogDebug("Quick action on {Page} skipped: the command cannot run right now.", pageType.Name);
            return;
        }

        if (target is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(null);
        }
        else
        {
            target.Execute(null);
        }
    }

    private static async Task<bool> WaitUntilIdleAsync(ViewModelBase viewModel, TimeSpan limit)
    {
        var waited = TimeSpan.Zero;
        while (viewModel.IsBusy)
        {
            if (waited >= limit)
            {
                return false;
            }

            await Task.Delay(BusyPollInterval);
            waited += BusyPollInterval;
        }

        return true;
    }

    private void ToggleOverlay()
    {
        var overlay = _services.GetRequiredService<IOverlayService>();
        overlay.Toggle();
        _notifications.Info(
            overlay.IsVisible ? Localization.Loc.T("Overlay shown") : Localization.Loc.T("Overlay hidden"),
            Localization.Loc.T("The overlay hotkey and the Overlay page do the same."));
    }
}
