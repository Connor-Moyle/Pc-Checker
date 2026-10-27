using System.IO;
using System.Windows;
using PcChecker.App.Localization;
using PcChecker.Core.Localization;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.Tray;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Windows;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Scheduling;
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
    private bool _headless;

    public static IServiceProvider Services => ((App)Current)._services ?? throw new InvalidOperationException("Services are not built yet.");

    /// <summary>The parsed command line for this run.</summary>
    public static CommandLineOptions Options => AppOptions.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = AppOptions.Current;

        // Started by the self-updater: let the previous copy finish closing before taking the single-instance lock.
        if (options.WaitForProcessId is { } previous)
        {
            WaitForPreviousInstance(previous);
        }

        // Task Scheduler started a maintenance job: no window, hand it to a running copy or run it headless and exit.
        if (options.ScheduledJob is { } scheduledJob)
        {
            StartHeadless(scheduledJob);
            return;
        }

        // Screenshot runs are short-lived and may run next to a normal instance (CI); they skip the single-instance check.
        if (!options.ScreenshotsRequested)
        {
            _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                // The running copy may be hidden in the tray: ask it to come to the front instead of showing a message.
                if (SendToRunningInstance(new PipeRequest(PipeCommand.Show, null)) != ScheduledJobPipe.ReplyOk)
                {
                    MessageBox.Show("PC Checker is already running. Check the taskbar or system tray.", "PC Checker", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                Shutdown();
                return;
            }
        }

        BuildContainer(options);
        var settings = _services.GetRequiredService<ISettingsService>();

        SettingsViewModel.ApplyTheme(settings.Current.General.Theme);
        ApplyLanguage(options.Language ?? settings.Current.General.Language, options.ScreenshotsRequested && options.Language is null);

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        bool startMinimized = (settings.Current.General.StartMinimized || options.StartMinimized) && !options.ScreenshotsRequested;

        // "Start minimized" with "Minimize to the tray" starts in the tray, the same place the minimize button sends it.
        bool startInTray = (options.StartInTray || (startMinimized && settings.Current.General.MinimizeToTray)) && !options.ScreenshotsRequested;
        bool trayReady = !options.ScreenshotsRequested && InitializeTray(window, startInTray);
        if (!startInTray || !trayReady)
        {
            if (startMinimized)
            {
                window.WindowState = WindowState.Minimized;
            }

            window.Show();
        }

        StartupTasks.Begin(_services, _logger);
        FanLifecycleHooks.OnStartup(_services);
        _ = OverclockStartupGuard.CheckAsync(_services, _logger);

        try
        {
            // Registers the overlay toggle hotkey and shows the overlay when it was left enabled.
            _services.GetRequiredService<IOverlayService>().Initialize();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The overlay could not be initialized.");
        }

        StartGameProfiles();

        if (options.ScreenshotsDirectory is { } directory)
        {
            _ = RunScreenshotsAsync(window, directory);
        }
    }

    /// <summary>Settings, logging, global error handlers and the DI container (shared by normal and headless runs).</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_services), nameof(_logger))]
    private void BuildContainer(CommandLineOptions options)
    {
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
        var appVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        logService.Export.SettingsFilePath = settings.SettingsFilePath;
        logService.Export.AppVersion = appVersion;
        logService.Export.ExtraNote = $"Mode: {options.Mode}";

        _services = BuildServices(settings, logService, options.Mode);
        logService.Export.JournalPath = _services.GetRequiredService<IActionJournal>().JournalPath;
        _logger = logService.CreateLogger<App>();
        _services.GetRequiredService<PcChecker.Core.Updates.IAppUpdateService>().RestartRequested +=
            (_, _) => Dispatcher.BeginInvoke(() => Shutdown());
        _logger.LogInformation("Application started v{Version} in {Mode} mode. Settings: {Path}", appVersion, options.Mode, settings.SettingsFilePath);
        if (options.UnknownArguments.Count > 0)
        {
            _logger.LogWarning("Ignoring unknown command line arguments: {Args}", string.Join(" ", options.UnknownArguments));
        }
    }

    /// <summary>Creates the tray icon (when wanted), hooks minimize/close to tray and opens the job pipe. False on failure.</summary>
    private bool InitializeTray(MainWindow window, bool startInTray)
    {
        try
        {
            var services = _services ?? throw new InvalidOperationException("Services are not built yet.");
            var tray = services.GetRequiredService<TrayIconService>();
            tray.Initialize(window, startInTray);
            var runner = services.GetRequiredService<ScheduledRunner>();
            runner.StartListening(tray.ShowMainWindow);
            if (!AppOptions.Current.IsDemo)
            {
                runner.CheckMovedTasksInBackground();
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The tray icon could not be set up; the window opens normally.");
            return false;
        }
    }

    /// <summary>
    /// <c>--scheduled &lt;job&gt;</c>: if PC Checker is already open, hand the job to it over the pipe and exit; otherwise
    /// run the job without a window, show a short tray notification and exit with 0 (success) or 1 (failure).
    /// </summary>
    private void StartHeadless(ScheduledJobKind job)
    {
        _headless = true;
        if (Mutex.TryOpenExisting(MutexName, out var running))
        {
            running.Dispose();
            var reply = SendToRunningInstance(new PipeRequest(PipeCommand.Run, job));
            if (reply is ScheduledJobPipe.ReplyOk or ScheduledJobPipe.ReplyBusy)
            {
                Shutdown(0);
                return;
            }
        }

        // One headless run per job at a time (a slow run overlapping the next trigger simply skips it).
        _instanceMutex = new Mutex(true, @"Local\PcChecker.Scheduled." + ScheduledJobIds.Id(job), out bool createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        BuildContainer(AppOptions.Current);
        _logger.LogInformation("Headless scheduled run: {Job}.", job);
        _ = RunHeadlessAsync(job);
    }

    private async Task RunHeadlessAsync(ScheduledJobKind job)
    {
        int code = await ScheduledRunner.RunHeadlessAsync(_services!, job, _logger);
        Shutdown(code);
    }

    private static string? SendToRunningInstance(PipeRequest request)
    {
        try
        {
            return Task.Run(() => ScheduledJobPipe.SendAsync(request, TimeSpan.FromSeconds(3), CancellationToken.None)).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the interface language for this run (Settings, or the Windows display language) and starts translating
    /// the interface. Screenshot runs stay in English so the README images match, unless <c>--language</c> is given.
    /// </summary>
    private void ApplyLanguage(string? setting, bool screenshots)
    {
        try
        {
            var code = screenshots ? "en" : LanguageCatalog.Resolve(setting, System.Globalization.CultureInfo.CurrentUICulture);
            Loc.Load(code);
            UiTranslator.Register();
            _logger?.LogInformation("Interface language: {Language} ({Count} translated strings).", code, Loc.Current.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The interface language could not be loaded; using English.");
        }
    }

    private void StartGameProfiles()
    {
        try
        {
            _services!.GetRequiredService<GameProfileSwitcher>().Start();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Game profile switching could not be started.");
        }
    }

    private static void WaitForPreviousInstance(int processId)
    {
        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(processId);
            previous.WaitForExit(30_000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("PC Checker exiting with code {Code}.", e.ApplicationExitCode);
        // A headless scheduled run never touched fans or clocks, so it leaves their crash-recovery state alone.
        if (_services is not null && !_headless)
        {
            // Game profiles first, so fans and the power plan are put back before the fan hooks run.
            _services.GetService<GameProfileSwitcher>()?.Stop();
            FanLifecycleHooks.OnExit(_services);
        }

        if (!_headless)
        {
            OverclockStartupGuard.MarkClean(_services);
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
        if (logService is SerilogLogService serilog)
        {
            services.AddSingleton(serilog.LoggerFactory);
        }

        // Everything else: the one place that picks Windows* or Demo* implementations.
        ServiceRegistration.Register(services, mode);

        // Shared UI helper that confirms, runs and reports optimization actions for several pages.
        services.AddSingleton<OptimizationActionRunner>();

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
        if (AppOptions.Current.ScreenshotsRequested || _headless)
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
