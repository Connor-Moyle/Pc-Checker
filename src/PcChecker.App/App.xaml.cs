using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Pages;
using PcChecker.App.Views.Windows;
using PcChecker.Claude;
using PcChecker.Core.Assistant;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Logging;
using PcChecker.Core.Optimization;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Performance;
using PcChecker.Core.Power;
using PcChecker.Core.Reports;
using PcChecker.Core.Restore;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using PcChecker.Core.Software;
using PcChecker.Core.Updates;
using PcChecker.Core.WindowsFeatures;
using Wpf.Ui;

namespace PcChecker.App;

/// <summary>Application bootstrap: single instance, settings, logging, DI container, theme and global error handling.</summary>
public partial class App : Application
{
    private const string MutexName = @"Local\PcChecker.SingleInstance";

    private Mutex? _instanceMutex;
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    public static IServiceProvider Services => ((App)Current)._services ?? throw new InvalidOperationException("Services are not built yet.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("PC Checker is already running. Check the taskbar or system tray.", "PC Checker", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var settings = new JsonSettingsService();
        settings.LoadAsync().GetAwaiter().GetResult();

        var logService = new SerilogLogService(settings.Current.Logging);
        settings.Changed += (_, s) => logService.Reconfigure(s.Logging);

        _services = BuildServices(settings, logService);
        _logger = logService.CreateLogger<App>();
        _logger.LogInformation("PC Checker starting. Settings: {Path}", settings.SettingsFilePath);

        SettingsViewModel.ApplyTheme(settings.Current.General.Theme);

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        if (settings.Current.General.StartMinimized)
        {
            window.WindowState = WindowState.Minimized;
        }

        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("PC Checker exiting with code {Code}.", e.ApplicationExitCode);
        try
        {
            _services?.GetService<ISensorService>()?.Stop();
        }
        catch
        {
            // Shutdown must not fail because of a sensor driver.
        }

        _services?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider BuildServices(ISettingsService settings, ILogService logService)
    {
        var services = new ServiceCollection();

        // Infrastructure
        services.AddSingleton(settings);
        services.AddSingleton(logService);
        services.AddSingleton(typeof(ILogger<>), typeof(ServiceLogger<>));
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton<IActionJournal, JsonActionJournal>();

        // Core services
        services.AddSingleton<IHardwareInventoryService, WindowsHardwareInventoryService>();
        services.AddSingleton<ISensorService, LibreHardwareSensorService>();
        services.AddSingleton<IHealthService, WindowsHealthService>();
        services.AddSingleton<IDriverService, WindowsDriverService>();
        services.AddSingleton<ISoftwareInventoryService, WindowsSoftwareInventoryService>();
        services.AddSingleton<IUninstallService, WindowsUninstallService>();
        services.AddSingleton<IPerformanceAnalyzer, WindowsPerformanceAnalyzer>();
        services.AddSingleton<IOptimizationService, WindowsOptimizationService>();
        services.AddSingleton<ITempFileScanner, WindowsTempFileScanner>();
        services.AddSingleton<ITempFileCleaner, WindowsTempFileCleaner>();
        services.AddSingleton<IWindowsFeatureService, WindowsFeatureService>();
        services.AddSingleton<IRestorePointService, WindowsRestorePointService>();
        services.AddSingleton<IChangeGuard, ChangeGuard>();
        services.AddSingleton<IOverclockService, WindowsOverclockService>();
        services.AddSingleton<IPowerService, WindowsPowerService>();
        services.AddSingleton<IAppUpdateService, VelopackUpdateService>();
        services.AddSingleton<IReportService, HtmlReportService>();
        services.AddSingleton<IPcAssistantService, ClaudeAssistantService>();

        // UI services
        services.AddSingleton<IPageService, PageService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ISnackbarService, SnackbarService>();
        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IHotkeyService, HotkeyService>();
        services.AddSingleton<IOverlayService, OverlayService>();

        // Shell
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<OverlayWindow>();

        // Pages and view models
        services.AddSingleton<DashboardViewModel>().AddSingleton<DashboardPage>();
        services.AddSingleton<HardwareViewModel>().AddSingleton<HardwarePage>();
        services.AddSingleton<HealthViewModel>().AddSingleton<HealthPage>();
        services.AddSingleton<SensorsViewModel>().AddSingleton<SensorsPage>();
        services.AddSingleton<PowerViewModel>().AddSingleton<PowerPage>();
        services.AddSingleton<DriversViewModel>().AddSingleton<DriversPage>();
        services.AddSingleton<SoftwareViewModel>().AddSingleton<SoftwarePage>();
        services.AddSingleton<PerformanceViewModel>().AddSingleton<PerformancePage>();
        services.AddSingleton<OptimizeViewModel>().AddSingleton<OptimizePage>();
        services.AddSingleton<CleanupViewModel>().AddSingleton<CleanupPage>();
        services.AddSingleton<WindowsFeaturesViewModel>().AddSingleton<WindowsFeaturesPage>();
        services.AddSingleton<OverclockViewModel>().AddSingleton<OverclockPage>();
        services.AddSingleton<OverlayViewModel>().AddSingleton<OverlayPage>();
        services.AddSingleton<LogsViewModel>().AddSingleton<LogsPage>();
        services.AddSingleton<AssistantViewModel>().AddSingleton<AssistantPage>();
        services.AddSingleton<SettingsViewModel>().AddSingleton<SettingsPage>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = false });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled UI exception.");
        ShowFriendlyError(e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _logger?.LogCritical(ex, "Unhandled exception (terminating: {Terminating}).", e.IsTerminating);
            if (e.IsTerminating)
            {
                ShowFriendlyError(ex);
            }
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }

    private void ShowFriendlyError(Exception exception)
    {
        try
        {
            var logDir = _services?.GetService<ILogService>()?.LogDirectory;
            var text = "PC Checker ran into a problem it could not recover from on its own.\n\n"
                + exception.Message
                + (logDir is null ? string.Empty : $"\n\nDetails were written to the log folder:\n{logDir}");
            MessageBox.Show(text, "PC Checker — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // Nothing else we can do.
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
