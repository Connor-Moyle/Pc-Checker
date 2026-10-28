using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PcChecker.App.Tray;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Pages;
using PcChecker.App.Views.Windows;
using PcChecker.Claude;
using PcChecker.Core.Assistant;
using PcChecker.Core.Audio;
using PcChecker.Core.Backups;
using PcChecker.Core.Battery;
using PcChecker.Core.Benchmark;
using PcChecker.Core.Checkup;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.Connections;
using PcChecker.Core.Crashes;
using PcChecker.Core.ContextMenus;
using PcChecker.Core.Debloat;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.Displays;
using PcChecker.Core.Drivers;
using PcChecker.Core.Drivers.Platform;
using PcChecker.Core.Drivers.Sources;
using PcChecker.Core.Fans;
using PcChecker.Core.Gaming;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.AppHistory;
using PcChecker.Core.History;
using PcChecker.Core.Hosts;
using PcChecker.Core.Input;
using PcChecker.Core.GameRuntimes;
using PcChecker.Core.Games;
using PcChecker.Core.GameReady;
using PcChecker.Core.Latency;
using PcChecker.Core.Leftovers;
using PcChecker.Core.LiveUsage;
using PcChecker.Core.Lighting;
using PcChecker.Core.Logging;
using PcChecker.Core.Memory;
using PcChecker.Core.Network;
using PcChecker.Core.Network.Tuning;
using PcChecker.Core.Optimization;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Performance;
using PcChecker.Core.Privacy;
using PcChecker.Core.Power;
using PcChecker.Core.Processes;
using PcChecker.Core.RegistryHealth;
using PcChecker.Core.Reports;
using PcChecker.Core.Restore;
using PcChecker.Core.ScheduledTasks;
using PcChecker.Core.Security;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Sensors;
using PcChecker.Core.Sensors.PawnIo;
using PcChecker.Core.Services;
using PcChecker.Core.Settings;
using PcChecker.Core.SetupBackup;
using PcChecker.Core.Software;
using PcChecker.Core.Startup;
using PcChecker.Core.Storage;
using PcChecker.Core.TaskManager;
using PcChecker.Core.Updates;
using PcChecker.Core.Users;
using PcChecker.Core.WindowsFeatures;
using PcChecker.Core.WindowsUpdate;
using Wpf.Ui;

namespace PcChecker.App.Services;

/// <summary>
/// The single place that maps every interface to its implementation. <see cref="AppMode.Normal"/> registers the
/// Windows services; <see cref="AppMode.Demo"/> registers the Demo services with sample data.
/// Modules add their <see cref="IOptimizationActionProvider"/>s only on the lines marked <c>// providers:</c>.
/// </summary>
public static class ServiceRegistration
{
    public static void Register(IServiceCollection services, AppMode mode)
    {
        ArgumentNullException.ThrowIfNull(services);
        bool demo = mode == AppMode.Demo;

        // Always registered (the app pre-registers the ISettingsService and ILogService instances it created at startup).
        services.TryAddSingleton<ISettingsService, JsonSettingsService>();
        services.TryAddSingleton<ILogService>(sp => new SerilogLogService(sp.GetRequiredService<ISettingsService>().Current.Logging));
        services.TryAddSingleton(typeof(ILogger<>), typeof(ServiceLogger<>));
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton<IActionJournal, JsonActionJournal>();
        // One guard instance: services guard single changes through it, the check-up batches several behind one restore point.
        services.AddSingleton<ChangeGuard>();
        services.AddSingleton<IChangeGuard>(sp => sp.GetRequiredService<ChangeGuard>());
        services.AddSingleton<IChangeBatchRunner>(sp => sp.GetRequiredService<ChangeGuard>());
        // Reversible changes that say how to undo them get an Undo button on their success notification for a few seconds.
        services.AddSingleton<IChangeUndoTracker, ChangeUndoTracker>();
        services.AddSingleton<IReportService, HtmlReportService>();

        // Shared hardware monitor host: one LibreHardwareMonitor instance for sensors, fans, SMART and overclock readouts.
        if (demo)
        {
            services.AddSingleton<DemoSensorService>();
            services.AddSingleton<ISensorService>(sp => sp.GetRequiredService<DemoSensorService>());
            services.AddSingleton<IHardwareMonitorHost, DemoHardwareMonitorHost>();
        }
        else
        {
            services.AddSingleton<LibreHardwareSensorService>();
            services.AddSingleton<ISensorService>(sp => sp.GetRequiredService<LibreHardwareSensorService>());
            services.AddSingleton<IHardwareMonitorHost>(sp => sp.GetRequiredService<LibreHardwareSensorService>());
        }

        // Module services by mode.
        Add<IHardwareInventoryService, WindowsHardwareInventoryService, DemoHardwareInventoryService>(services, demo);
        Add<IHealthService, WindowsHealthService, DemoHealthService>(services, demo);
        Add<IDriverService, WindowsDriverService, DemoDriverService>(services, demo);
        Add<IDriverCleanupService, WindowsDriverCleanupService, DemoDriverCleanupService>(services, demo);
        Add<IDriverSourceService, WindowsDriverSourceService, DemoDriverSourceService>(services, demo);
        Add<IPcPlatformService, WindowsPcPlatformService, DemoPcPlatformService>(services, demo);
        Add<IWindowsUpdateService, WindowsUpdateService, DemoWindowsUpdateService>(services, demo);
        Add<IUpdateRollbackService, WindowsUpdateRollbackService, DemoUpdateRollbackService>(services, demo);
        Add<ISoftwareInventoryService, WindowsSoftwareInventoryService, DemoSoftwareInventoryService>(services, demo);
        Add<IUninstallService, WindowsUninstallService, DemoUninstallService>(services, demo);
        Add<ILeftoverService, WindowsLeftoverService, DemoLeftoverService>(services, demo);
        services.AddSingleton<LeftoverScanQueue>();
        Add<IPerformanceAnalyzer, WindowsPerformanceAnalyzer, DemoPerformanceAnalyzer>(services, demo);
        Add<IOptimizationService, WindowsOptimizationService, DemoOptimizationService>(services, demo);
        Add<ICleanupCommandService, WindowsCleanupCommandService, DemoCleanupCommandService>(services, demo);
        Add<ITempFileScanner, WindowsTempFileScanner, DemoTempFileScanner>(services, demo);
        Add<ITempFileCleaner, WindowsTempFileCleaner, DemoTempFileCleaner>(services, demo);
        Add<IDiskSpaceService, WindowsDiskSpaceService, DemoDiskSpaceService>(services, demo);
        Add<IWindowsFeatureService, WindowsFeatureService, DemoWindowsFeatureService>(services, demo);
        Add<IRestorePointService, WindowsRestorePointService, DemoRestorePointService>(services, demo);
        Add<IOverclockService, WindowsOverclockService, DemoOverclockService>(services, demo);
        Add<IPowerService, WindowsPowerService, DemoPowerService>(services, demo);
        Add<IHistoryService, WindowsHistoryService, DemoHistoryService>(services, demo);
        Add<ICrashService, WindowsCrashService, DemoCrashService>(services, demo);
        Add<IAppUpdateService, GitHubExeUpdateService, DemoAppUpdateService>(services, demo);
        Add<IAppInstallService, WindowsAppInstallService, DemoAppInstallService>(services, demo);
        Add<IPcAssistantService, ClaudeAssistantService, DemoPcAssistantService>(services, demo);
        Add<IFanControlService, LibreHardwareFanControlService, DemoFanControlService>(services, demo);
        Add<IPawnIoService, WindowsPawnIoService, DemoPawnIoService>(services, demo);
        Add<ILightingService, OpenRgbLightingService, DemoLightingService>(services, demo);
        Add<IRgbHardwareService, WindowsRgbHardwareService, DemoRgbHardwareService>(services, demo);
        Add<IDriveInventoryService, WindowsDriveInventoryService, DemoDriveInventoryService>(services, demo);
        Add<IStorageToolsService, WindowsStorageToolsService, DemoStorageToolsService>(services, demo);
        Add<IMemoryService, WindowsMemoryService, DemoMemoryService>(services, demo);
        Add<IBatteryService, WindowsBatteryService, DemoBatteryService>(services, demo);
        Add<IDisplayService, WindowsDisplayService, DemoDisplayService>(services, demo);
        Add<IAudioService, WindowsAudioService, DemoAudioService>(services, demo);
        Add<IInputService, WindowsInputService, DemoInputService>(services, demo);
        Add<ILatencyService, WindowsLatencyService, DemoLatencyService>(services, demo);
        Add<IGameRuntimeService, WindowsGameRuntimeService, DemoGameRuntimeService>(services, demo);
        Add<IGameLibraryService, WindowsGameLibraryService, DemoGameLibraryService>(services, demo);
        services.AddSingleton<GameFolderScanHandOff>().AddSingleton<GameProfileHandOff>();
        services.AddSingleton<DriverFocusHandOff>().AddSingleton<PcChecker.Core.GameRuntimes.RuntimeFamilyHandOff>().AddSingleton<DriverSearchCache>();
        services.AddTransient<CrashFixServices>();
        Add<INetworkTuningService, WindowsNetworkTuningService, DemoNetworkTuningService>(services, demo);
        Add<INetworkService, WindowsNetworkService, DemoNetworkService>(services, demo);
        Add<IStartupService, WindowsStartupService, DemoStartupService>(services, demo);
        Add<IProcessService, WindowsProcessService, DemoProcessService>(services, demo);
        Add<ILiveUsageService, WindowsLiveUsageService, DemoLiveUsageService>(services, demo);
        Add<IAppHistoryService, WindowsAppHistoryService, DemoAppHistoryService>(services, demo);
        Add<IServiceManagementService, WindowsServiceManagementService, DemoServiceManagementService>(services, demo);
        Add<IUserSessionService, WindowsUserSessionService, DemoUserSessionService>(services, demo);
        Add<ITaskManagerReplacementService, WindowsTaskManagerReplacementService, DemoTaskManagerReplacementService>(services, demo);
        Add<IRestartScheduler, WindowsRestartScheduler, DemoRestartScheduler>(services, demo);
        Add<IMousePollingMeter, RawInputPollingMeter, DemoMousePollingMeter>(services, demo);
        Add<IRegistryHealthService, WindowsRegistryHealthService, DemoRegistryHealthService>(services, demo);
        Add<IDebloatService, WindowsDebloatService, DemoDebloatService>(services, demo);
        Add<IContextMenuService, WindowsContextMenuService, DemoContextMenuService>(services, demo);
        Add<IHostsFileService, WindowsHostsFileService, DemoHostsFileService>(services, demo);
        Add<ISecurityStatusService, WindowsSecurityStatusService, DemoSecurityStatusService>(services, demo);
        Add<IPrivacyService, WindowsPrivacyService, DemoPrivacyService>(services, demo);
        Add<IBackupsService, WindowsBackupsService, DemoBackupsService>(services, demo);
        Add<IForegroundAppTracker, WindowsForegroundAppTracker, DemoForegroundAppTracker>(services, demo);
        Add<IFrameRateMonitor, WindowsFrameRateMonitor, DemoFrameRateMonitor>(services, demo);
        Add<IGameProfileService, WindowsGameProfileService, DemoGameProfileService>(services, demo);
        Add<IScheduledTaskService, WindowsScheduledTaskService, DemoScheduledTaskService>(services, demo);
        Add<IScheduledJobStore, JsonScheduledJobStore, DemoScheduledJobStore>(services, demo);
        Add<ITaskSchedulerService, WindowsTaskSchedulerService, DemoTaskSchedulerService>(services, demo);
        Add<IConnectionService, WindowsConnectionService, DemoConnectionService>(services, demo);
        Add<IBenchmarkService, WindowsBenchmarkService, DemoBenchmarkService>(services, demo);
        Add<IBenchmarkHistoryStore, JsonBenchmarkHistoryStore, DemoBenchmarkHistoryStore>(services, demo);
        Add<ISetupBackupStore, FileSetupBackupStore, DemoSetupBackupStore>(services, demo);
        services.AddSingleton<ISetupBackupService, SetupBackupService>();
        services.AddSingleton<ScheduledJobExecutor>();

        // The check-up only calls the module services above, so the same class serves both modes.
        services.AddSingleton<ICheckupService, CheckupService>();

        // Game ready reads through the module services above plus its own Windows/demo reader.
        Add<IGameReadySystem, WindowsGameReadySystem, DemoGameReadySystem>(services, demo);
        if (demo)
        {
            // Demo: Game ready gets its own copy of the sample desktop with the 240 Hz monitor left at 60 Hz (the slip it
            // exists to catch), shared by the service and its page so a fix and its undo show up. The Displays page keeps
            // the shared sample unchanged.
            var gameReadyDisplays = DemoDisplayService.WithGamingMonitorAt(60);
            services.AddSingleton<IGameReadyService>(sp => ActivatorUtilities.CreateInstance<GameReadyService>(sp, gameReadyDisplays));
            services.AddSingleton(sp => ActivatorUtilities.CreateInstance<GameReadyViewModel>(sp, gameReadyDisplays));
        }
        else
        {
            services.AddSingleton<IGameReadyService, GameReadyService>();
            services.AddSingleton<GameReadyViewModel>();
        }

        // Optimization action providers (IOptimizationService aggregates every registration below).
        // providers: Latency (Windows: LatencyActionsProvider, Demo: DemoLatencyActionsProvider)
        Add<IOptimizationActionProvider, LatencyActionsProvider, DemoLatencyActionsProvider>(services, demo);
        services.AddSingleton<IOptimizationActionProvider, MemoryActionsProvider>(); // providers: Memory
        services.AddSingleton<IOptimizationActionProvider, StartupActionsProvider>(); // providers: Startup
        // providers: Input
        services.AddSingleton<IOptimizationActionProvider, InputActionsProvider>();

        // UI services
        services.AddSingleton<IPageService, PageService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ISnackbarService, SnackbarService>();
        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<IOverlayService, OverlayService>();
        services.AddSingleton<GameProfileSwitcher>();
        services.AddSingleton<RestartPromptService>();
        services.AddSingleton<ScreenshotRunner>();
        services.AddSingleton<ScheduledRunner>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<AppUpdateFlow>();
        services.AddSingleton<PawnIoInstallFlow>();

        // Shell
        services.AddSingleton<QuickActionRunner>();
        services.AddSingleton<WelcomeService>();
        services.AddSingleton<QuickSearchViewModel>();
        services.AddSingleton<SimpleModeHintViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<OverlayWindow>();

        // Pages and view models (navigation order)
        services.AddSingleton<DashboardViewModel>().AddSingleton<DashboardPage>();
        services.AddSingleton<CheckupViewModel>().AddSingleton<CheckupPage>();
        services.AddSingleton<SensorsViewModel>().AddSingleton<SensorsPage>();
        services.AddSingleton<PowerViewModel>().AddSingleton<PowerPage>();
        services.AddSingleton<ProcessesViewModel>().AddSingleton<ProcessesPage>();
        services.AddSingleton<LiveUsageViewModel>().AddSingleton<LiveUsagePage>();
        services.AddSingleton<CompactUsageWindowService>();
        services.AddSingleton<AppHistoryViewModel>().AddSingleton<AppHistoryPage>();
        services.AddSingleton<ServicesViewModel>().AddSingleton<ServicesPage>();
        services.AddSingleton<UsersViewModel>().AddSingleton<UsersPage>();
        services.AddSingleton<HistoryViewModel>().AddSingleton<HistoryPage>();
        services.AddSingleton<CrashesViewModel>().AddSingleton<CrashesPage>();
        services.AddSingleton<OverlayViewModel>().AddSingleton<OverlayPage>();
        services.AddSingleton<HardwareViewModel>().AddSingleton<HardwarePage>();
        services.AddSingleton<HealthViewModel>().AddSingleton<HealthPage>();
        services.AddSingleton<StorageViewModel>().AddSingleton<StoragePage>();
        services.AddSingleton<MemoryViewModel>().AddSingleton<MemoryPage>();
        services.AddSingleton<BatteryViewModel>().AddSingleton<BatteryPage>();
        services.AddSingleton<DisplaysViewModel>().AddSingleton<DisplaysPage>();
        services.AddSingleton<AudioViewModel>().AddSingleton<AudioPage>();
        services.AddSingleton<InputViewModel>().AddSingleton<InputPage>();
        services.AddSingleton<FansViewModel>().AddSingleton<FansPage>();
        services.AddSingleton<LightingViewModel>().AddSingleton<LightingPage>();
        services.AddSingleton<DriversViewModel>().AddSingleton<DriversPage>();
        services.AddSingleton<DriverCleanupViewModel>().AddSingleton<DriverCleanupPage>();
        services.AddSingleton<WindowsUpdateViewModel>().AddSingleton<WindowsUpdatePage>();
        services.AddSingleton<SoftwareViewModel>().AddSingleton<SoftwarePage>();
        services.AddSingleton<LeftoversViewModel>().AddSingleton<LeftoversPage>();
        services.AddSingleton<StartupViewModel>().AddSingleton<StartupPage>();
        services.AddSingleton<ScheduledTasksViewModel>().AddSingleton<ScheduledTasksPage>();
        services.AddSingleton<WindowsFeaturesViewModel>().AddSingleton<WindowsFeaturesPage>();
        services.AddSingleton<DebloatViewModel>().AddSingleton<DebloatPage>();
        services.AddSingleton<ContextMenuViewModel>().AddSingleton<ContextMenuPage>();
        services.AddSingleton<CleanupViewModel>().AddSingleton<CleanupPage>();
        services.AddSingleton<DiskSpaceViewModel>().AddSingleton<DiskSpacePage>();
        services.AddSingleton<RegistryViewModel>().AddSingleton<RegistryPage>();
        services.AddSingleton<SecurityViewModel>().AddSingleton<SecurityPage>();
        services.AddSingleton<PrivacyViewModel>().AddSingleton<PrivacyPage>();
        services.AddSingleton<BackupsViewModel>().AddSingleton<BackupsPage>();
        services.AddSingleton<PerformanceViewModel>().AddSingleton<PerformancePage>();
        services.AddSingleton<OptimizeViewModel>().AddSingleton<OptimizePage>();
        services.AddSingleton<BenchmarkViewModel>().AddSingleton<BenchmarkPage>();
        services.AddSingleton<LatencyViewModel>().AddSingleton<LatencyPage>();
        services.AddSingleton<GamesViewModel>().AddSingleton<GamesPage>();
        services.AddSingleton<GameReadyPage>(); // GameReadyViewModel is registered with the Game ready services above
        services.AddSingleton<MissingDllsViewModel>().AddSingleton<MissingDllsPage>();
        services.AddSingleton<NetworkViewModel>().AddSingleton<NetworkPage>();
        services.AddSingleton<ConnectionsViewModel>().AddSingleton<ConnectionsPage>();
        services.AddSingleton<HostsViewModel>().AddSingleton<HostsPage>();
        services.AddSingleton<OverclockViewModel>().AddSingleton<OverclockPage>();
        services.AddSingleton<AssistantViewModel>().AddSingleton<AssistantPage>();
        services.AddSingleton<LogsViewModel>().AddSingleton<LogsPage>();
        services.AddSingleton<SettingsViewModel>().AddSingleton<SettingsPage>();
    }

    private static void Add<TService, TWindows, TDemo>(IServiceCollection services, bool demo)
        where TService : class
        where TWindows : class, TService
        where TDemo : class, TService
    {
        if (demo)
        {
            services.AddSingleton<TService, TDemo>();
        }
        else
        {
            services.AddSingleton<TService, TWindows>();
        }
    }

    /// <summary>Resolves <see cref="ILogger{T}"/> from the app's log service so every class gets the right category.</summary>
    private sealed class ServiceLogger<T> : ILogger<T>
    {
        private readonly ILogger<T> _inner;

        public ServiceLogger(ILogService logService) => _inner = logService.CreateLogger<T>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
