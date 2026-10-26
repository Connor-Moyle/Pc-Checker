using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Windows;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.App;

/// <summary>
/// Application bootstrap: single instance, command line, settings, logging, DI container (via
/// <see cref="ServiceRegistration"/>), theme, global error handling and the screenshot runner hook.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\PcChecker.SingleInstance";

    private Mutex? _instanceMutex;
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    public static IServiceProvider Services => ((App)Current)._services ?? throw new InvalidOperationException("Services are not built yet.");

    /// <summary>The parsed command line for this run.</summary>
    public static CommandLineOptions Options => AppOptions.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = AppOptions.Current;

        // Screenshot runs are short-lived and may run next to a normal instance (CI); they skip the single-instance check.
        if (!options.ScreenshotsRequested)
        {
            _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("PC Checker is already running. Check the taskbar or system tray.", "PC Checker", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Demo mode keeps its own settings file so sample runs and screenshots never touch the user's real settings.
        var settings = options.IsDemo
            ? new JsonSettingsService(Path.Combine(JsonSettingsService.DefaultDirectory(), "demo"))
            : new JsonSettingsService();
        settings.LoadAsync().GetAwaiter().GetResult();

        var logService = new SerilogLogService(settings.Current.Logging);
        settings.Changed += (_, s) => logService.Reconfigure(s.Logging);

        _services = BuildServices(settings, logService, options.Mode);
        _logger = logService.CreateLogger<App>();
        _logger.LogInformation("PC Checker starting in {Mode} mode. Settings: {Path}", options.Mode, settings.SettingsFilePath);
        if (options.UnknownArguments.Count > 0)
        {
            _logger.LogWarning("Ignoring unknown command line arguments: {Args}", string.Join(" ", options.UnknownArguments));
        }

        SettingsViewModel.ApplyTheme(settings.Current.General.Theme);

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        if ((settings.Current.General.StartMinimized || options.StartMinimized) && !options.ScreenshotsRequested)
        {
            window.WindowState = WindowState.Minimized;
        }

        window.Show();
        FanLifecycleHooks.OnStartup(_services);

        if (options.ScreenshotsDirectory is { } directory)
        {
            _ = RunScreenshotsAsync(window, directory);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("PC Checker exiting with code {Code}.", e.ApplicationExitCode);
        if (_services is not null)
        {
            FanLifecycleHooks.OnExit(_services);
        }

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

    private static ServiceProvider BuildServices(ISettingsService settings, ILogService logService, AppMode mode)
    {
        var services = new ServiceCollection();

        // Infrastructure created before the container exists.
        services.AddSingleton(settings);
        services.AddSingleton(logService);

        // Everything else: the one place that picks Windows* or Demo* implementations.
        ServiceRegistration.Register(services, mode);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = false });
    }

    private async Task RunScreenshotsAsync(MainWindow window, string directory)
    {
        int code;
        try
        {
            // Let the window finish its first layout pass before driving navigation.
            await Task.Delay(300);
            var runner = _services!.GetRequiredService<ScreenshotRunner>();
            code = await runner.RunAsync(window, directory, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Screenshot runner could not start.");
            code = 1;
        }

        Shutdown(code);
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
        if (AppOptions.Current.ScreenshotsRequested)
        {
            // Never block an unattended run on a dialog.
            return;
        }

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
}
