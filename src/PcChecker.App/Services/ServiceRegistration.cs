using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Pages;
using PcChecker.App.Views.Windows;
using PcChecker.Claude;
using PcChecker.Core.Assistant;
using PcChecker.Core.Audio;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.Displays;
using PcChecker.Core.Drivers;
using PcChecker.Core.Fans;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Input;
using PcChecker.Core.Latency;
using PcChecker.Core.Lighting;
using PcChecker.Core.Logging;
using PcChecker.Core.Memory;
using PcChecker.Core.Optimization;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Performance;
using PcChecker.Core.Power;
using PcChecker.Core.Reports;
using PcChecker.Core.Restore;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using PcChecker.Core.Software;
using PcChecker.Core.Startup;
using PcChecker.Core.Storage;
using PcChecker.Core.Updates;
using PcChecker.Core.WindowsFeatures;
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
        services.AddSingleton<IChangeGuard, ChangeGuard>();
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
        Add<ISoftwareInventoryService, WindowsSoftwareInventoryService, DemoSoftwareInventoryService>(services, demo);
        Add<IUninstallService, WindowsUninstallService, DemoUninstallService>(services, demo);
        Add<IPerformanceAnalyzer, WindowsPerformanceAnalyzer, DemoPerformanceAnalyzer>(services, demo);
        Add<IOptimizationService, WindowsOptimizationService, DemoOptimizationService>(services, demo);
        Add<ITempFileScanner, WindowsTempFileScanner, DemoTempFileScanner>(services, demo);
        Add<ITempFileCleaner, WindowsTempFileCleaner, DemoTempFileCleaner>(services, demo);
        Add<IWindowsFeatureService, WindowsFeatureService, DemoWindowsFeatureService>(services, demo);
        Add<IRestorePointService, WindowsRestorePointService, DemoRestorePointService>(services, demo);
        Add<IOverclockService, WindowsOverclockService, DemoOverclockService>(services, demo);
        Add<IPowerService, WindowsPowerService, DemoPowerService>(services, demo);
        Add<IAppUpdateService, VelopackUpdateService, DemoAppUpdateService>(services, demo);
        Add<IPcAssistantService, ClaudeAssistantService, DemoPcAssistantService>(services, demo);
        Add<IFanControlService, LibreHardwareFanControlService, DemoFanControlService>(services, demo);
        Add<ILightingService, OpenRgbLightingService, DemoLightingService>(services, demo);
        Add<IStorageToolsService, WindowsStorageToolsService, DemoStorageToolsService>(services, demo);
        Add<IMemoryService, WindowsMemoryService, DemoMemoryService>(services, demo);
        Add<IDisplayService, WindowsDisplayService, DemoDisplayService>(services, demo);
        Add<IAudioService, WindowsAudioService, DemoAudioService>(services, demo);
        Add<IInputService, WindowsInputService, DemoInputService>(services, demo);
        Add<ILatencyService, WindowsLatencyService, DemoLatencyService>(services, demo);
        Add<IStartupService, WindowsStartupService, DemoStartupService>(services, demo);
        Add<IMousePollingMeter, RawInputPollingMeter, DemoMousePollingMeter>(services, demo);

        // Optimization action providers (IOptimizationService aggregates every registration below).
        // providers: Latency (Windows: LatencyActionsProvider, Demo: DemoLatencyActionsProvider)
        Add<IOptimizationActionProvider, LatencyActionsProvider, DemoLatencyActionsProvider>(services, demo);
        services.AddSingleton<IOptimizationActionProvider, MemoryActionsProvider>(); // providers: Memory
        // providers: Startup   → services.AddSingleton<IOptimizationActionProvider, StartupActionsProvider>();
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
        services.AddSingleton<ScreenshotRunner>();

        // Shell
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<OverlayWindow>();

        // Pages and view models (navigation order)
        services.AddSingleton<DashboardViewModel>().AddSingleton<DashboardPage>();
        services.AddSingleton<SensorsViewModel>().AddSingleton<SensorsPage>();
        services.AddSingleton<PowerViewModel>().AddSingleton<PowerPage>();
        services.AddSingleton<OverlayViewModel>().AddSingleton<OverlayPage>();
        services.AddSingleton<HardwareViewModel>().AddSingleton<HardwarePage>();
        services.AddSingleton<HealthViewModel>().AddSingleton<HealthPage>();
        services.AddSingleton<StorageViewModel>().AddSingleton<StoragePage>();
        services.AddSingleton<MemoryViewModel>().AddSingleton<MemoryPage>();
        services.AddSingleton<DisplaysViewModel>().AddSingleton<DisplaysPage>();
        services.AddSingleton<AudioViewModel>().AddSingleton<AudioPage>();
        services.AddSingleton<InputViewModel>().AddSingleton<InputPage>();
        services.AddSingleton<FansViewModel>().AddSingleton<FansPage>();
        services.AddSingleton<LightingViewModel>().AddSingleton<LightingPage>();
        services.AddSingleton<DriversViewModel>().AddSingleton<DriversPage>();
        services.AddSingleton<SoftwareViewModel>().AddSingleton<SoftwarePage>();
        services.AddSingleton<StartupViewModel>().AddSingleton<StartupPage>();
        services.AddSingleton<WindowsFeaturesViewModel>().AddSingleton<WindowsFeaturesPage>();
        services.AddSingleton<CleanupViewModel>().AddSingleton<CleanupPage>();
        services.AddSingleton<PerformanceViewModel>().AddSingleton<PerformancePage>();
        services.AddSingleton<OptimizeViewModel>().AddSingleton<OptimizePage>();
        services.AddSingleton<LatencyViewModel>().AddSingleton<LatencyPage>();
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
