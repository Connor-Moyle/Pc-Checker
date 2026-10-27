using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Settings;
using PcChecker.Core.Updates;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Settings page: every control is bound to <see cref="AppSettings"/> and saved a moment after it changes (debounced),
/// so there is no Save button to forget. Theme changes apply instantly. Split across partial files by section.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly IAppUpdateService _updates;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly ILogService _logs;
    private readonly INavigationService _navigation;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IScheduledTaskService _scheduledTasks;
    private readonly IScheduledJobStore _jobStore;
    private readonly ScheduledRunner _scheduledRunner;
    private readonly object _saveGate = new();
    private CancellationTokenSource? _saveDebounce;
    private bool _savePending;
    private bool _reloadDeferred;
    private bool _loading;

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private bool _advancedMode;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _checkHealthOnStartup;

    [ObservableProperty]
    private bool _createRestorePointBefore;

    [ObservableProperty]
    private bool _createRestorePointAfter;

    [ObservableProperty]
    private bool _bypassRestorePointFrequencyLimit;

    [ObservableProperty]
    private string? _saveStatus;

    public SettingsViewModel(ISettingsService settings, IAppUpdateService updates, INotificationService notifications,
        IDialogService dialogs, ILogService logs, INavigationService navigation, ILogger<SettingsViewModel> logger,
        IScheduledTaskService scheduledTasks, IScheduledJobStore jobStore, ScheduledRunner scheduledRunner)
        : base(settings)
    {
        _scheduledTasks = scheduledTasks;
        _jobStore = jobStore;
        _scheduledRunner = scheduledRunner;
        _updates = updates;
        _notifications = notifications;
        _dialogs = dialogs;
        _logs = logs;
        _navigation = navigation;
        _logger = logger;
        HookSchedules();
        LoadFromSettings();
        settings.Changed += (_, _) => DispatcherHelper.PostToUi(OnSettingsChanged);
        UpdatePrompter.ResultChanged += (_, _) => DispatcherHelper.PostToUi(() => ShowUpdateResult(UpdatePrompter.LastResult));
    }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    public IReadOnlyList<UpdateChannel> UpdateChannels { get; } = Enum.GetValues<UpdateChannel>();

    public string AppVersion => "PC Checker " + _updates.CurrentVersion;

    public string CurrentVersion => _updates.CurrentVersion;

    public string SettingsFilePath => Settings.SettingsFilePath;

    public string LogDirectory => _logs.LogDirectory;

    public bool IsDemoMode => AppOptions.Mode == AppMode.Demo;

    public string RepositoryUrl => UpdateLinks.Repository;

    public string ReleasesUrl => UpdateLinks.ReleasesPage;

    /// <summary>Third-party components and their licences, mirrored in docs/THIRD-PARTY-NOTICES.md.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Licenses { get; } =
    [
        new("PC Checker", "MIT"),
        new("LibreHardwareMonitor", "MPL-2.0"),
        new("WPF-UI", "MIT"),
        new("NvAPIWrapper", "LGPL-3.0"),
        new("Serilog", "Apache-2.0"),
        new("CommunityToolkit.Mvvm", "MIT"),
        new("Anthropic SDK", "MIT"),
        new("OpenRGB.NET", "MIT"),
        new("NAudio", "MIT"),
        new("Vortice.Windows (Direct3D 11, DXGI, D3DCompiler)", "MIT"),
        new("H.NotifyIcon", "MIT"),
        new("System.Diagnostics.PerformanceCounter", "MIT"),
        new("System.Management", "MIT"),
        new("System.Security.Cryptography.ProtectedData", "MIT"),
        new("Microsoft.Extensions.* (DependencyInjection, Hosting, Logging)", "MIT"),
    ];

    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        ShowUpdateResult(UpdatePrompter.LastResult);
        return RefreshScheduleStatusAsync(cancellationToken);
    }

    /// <summary>
    /// Reloads the form after any settings save. While one of this page's own edits is still waiting to be saved, the
    /// reload is deferred: that save raises <see cref="ISettingsService.Changed"/> again once it lands, so nothing is lost,
    /// and the user's in-progress text is not overwritten mid-edit.
    /// </summary>
    private void OnSettingsChanged()
    {
        lock (_saveGate)
        {
            if (_savePending)
            {
                _reloadDeferred = true;
                return;
            }

            _reloadDeferred = false;
        }

        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var s = Settings.Current;
            Theme = s.General.Theme;
            AdvancedMode = s.General.AdvancedMode;
            StartMinimized = s.General.StartMinimized;
            MinimizeToTray = s.General.MinimizeToTray;
            CheckHealthOnStartup = s.General.CheckHealthOnStartup;
            CreateRestorePointBefore = s.RestorePoints.CreateBeforeChanges;
            CreateRestorePointAfter = s.RestorePoints.CreateAfterChanges;
            BypassRestorePointFrequencyLimit = s.RestorePoints.BypassFrequencyLimit;
            LoadUpdateSettings(s);
            LoadSectionSettings(s);
            LoadTraySettings(s);
            LoadScheduleSettings(s);
            LoadLanguageSettings(s);
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnThemeChanged(AppTheme value)
    {
        if (_loading)
        {
            return;
        }

        ApplyTheme(value);
        Persist(s => s.General.Theme = value);
    }

    partial void OnAdvancedModeChanged(bool value) => Persist(s => s.General.AdvancedMode = value);
    partial void OnStartMinimizedChanged(bool value) => Persist(s => s.General.StartMinimized = value);
    partial void OnMinimizeToTrayChanged(bool value)
    {
        Persist(s => s.General.MinimizeToTray = value);
        OnPropertyChanged(nameof(TrayIconKeptForTrayOptions));
    }
    partial void OnCheckHealthOnStartupChanged(bool value) => Persist(s => s.General.CheckHealthOnStartup = value);
    partial void OnCreateRestorePointBeforeChanged(bool value) => Persist(s => s.RestorePoints.CreateBeforeChanges = value);
    partial void OnCreateRestorePointAfterChanged(bool value) => Persist(s => s.RestorePoints.CreateAfterChanges = value);
    partial void OnBypassRestorePointFrequencyLimitChanged(bool value) => Persist(s => s.RestorePoints.BypassFrequencyLimit = value);

    /// <summary>Applies a change to the live settings and saves after a short pause (so typing does not write on every key).</summary>
    private void Persist(Action<AppSettings> apply)
    {
        if (_loading)
        {
            return;
        }

        apply(Settings.Current);
        CancellationToken token;
        lock (_saveGate)
        {
            _saveDebounce?.Cancel();
            _saveDebounce?.Dispose();
            _saveDebounce = new CancellationTokenSource();
            token = _saveDebounce.Token;
            _savePending = true;
        }

        SaveStatus = "Saving…";
        _ = SaveLaterAsync(token);
    }

    private async Task SaveLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDelay, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_saveGate)
        {
            if (_saveDebounce is null || _saveDebounce.Token != token)
            {
                return; // superseded by a newer edit, which will save instead
            }

            _savePending = false;
        }

        await SaveNowAsync();
    }

    private async Task SaveNowAsync()
    {
        try
        {
            await Settings.SaveAsync();
            DispatcherHelper.PostToUi(() => SaveStatus = "Saved " + DateTime.Now.ToString("HH:mm:ss"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save settings.");
            DispatcherHelper.PostToUi(() =>
            {
                SaveStatus = null;
                ErrorMessage = "Settings could not be saved: " + ex.Message;

                // A failed save raises no Changed event, so catch up on any reload that was deferred for it.
                bool reload;
                lock (_saveGate)
                {
                    reload = _reloadDeferred && !_savePending;
                }

                if (reload)
                {
                    OnSettingsChanged();
                }
            });
        }
    }

    /// <summary>Applies the chosen theme to the running app.</summary>
    public static void ApplyTheme(AppTheme theme)
    {
        bool systemIsDark = ApplicationThemeManager.GetSystemTheme() != SystemTheme.Light;
        var applied = ThemeResolver.IsDark(theme, systemIsDark) ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(applied, WindowBackdropType.Mica, true);
    }

    [RelayCommand]
    private void OpenSettingsFolder() => Open(Settings.SettingsDirectory);

    /// <summary>Writes the licence texts embedded in PcChecker.exe to a folder and opens it.</summary>
    [RelayCommand]
    private void ShowLicenseTexts()
    {
        try
        {
            var folder = System.IO.Path.Combine(Settings.SettingsDirectory, "licenses");
            System.IO.Directory.CreateDirectory(folder);
            var assembly = typeof(SettingsViewModel).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("licenses/", StringComparison.Ordinal)))
            {
                using var source = assembly.GetManifestResourceStream(name);
                if (source is null)
                {
                    continue;
                }

                using var target = System.IO.File.Create(System.IO.Path.Combine(folder, name["licenses/".Length..]));
                source.CopyTo(target);
            }

            Open(folder);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "Could not write the licence texts: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenLogsFolder() => Open(_logs.LogDirectory);

    [RelayCommand]
    private void OpenRepository() => Open(UpdateLinks.Repository);

    [RelayCommand]
    private void OpenReleasesPage() => Open(UpdateLinks.ReleasesPage);

    [RelayCommand]
    private void OpenAssistantPage() => _navigation.Navigate(typeof(AssistantPage));

    [RelayCommand]
    private void OpenSensorsPage() => _navigation.Navigate(typeof(SensorsPage));

    [RelayCommand]
    private Task ExportDiagnosticsAsync() => RunBusyAsync(async ct =>
    {
        var suggested = $"pc-checker-logs-{DateTime.Now:yyyyMMdd-HHmm}.zip";
        var path = await _dialogs.SaveFileAsync("Save diagnostics", suggested, "Zip archive (*.zip)|*.zip");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var written = await _logs.ExportZipAsync(path, ct);
        _notifications.Success("Diagnostics exported", "Logs and the action journal were zipped to " + written);
    }, "Zipping logs…");

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        bool ok = await _dialogs.ConfirmAsync("Reset all settings?",
            "Every option goes back to its default: theme, thresholds, overlay layout, fan, lighting and overclock profiles. " +
            "PC Checker's scheduled tasks and \"Start with Windows\" are removed. Your Claude API key is kept. This cannot be undone.", "Reset settings", isDanger: true);
        if (!ok)
        {
            return;
        }

        var defaults = AppSettings.CreateDefault();
        var current = Settings.Current;
        var apiKey = current.Claude.ProtectedApiKey;
        current.General = defaults.General;
        current.Updates = defaults.Updates;
        current.RestorePoints = defaults.RestorePoints;
        current.Monitoring = defaults.Monitoring;
        current.Overlay = defaults.Overlay;
        current.Logging = defaults.Logging;
        current.Claude = defaults.Claude;
        current.Claude.ProtectedApiKey = apiKey;
        current.Cleanup = defaults.Cleanup;
        current.Overclock = defaults.Overclock;
        current.Fans = defaults.Fans;
        current.Lighting = defaults.Lighting;
        current.Displays = defaults.Displays;
        current.Input = defaults.Input;
        current.Memory = defaults.Memory;
        current.Latency = defaults.Latency;
        current.Startup = defaults.Startup;
        await ResetTrayAndSchedulesAsync(current, defaults);
        await SaveNowAsync();
        LoadFromSettings();
        ApplyTheme(current.General.Theme);
        _notifications.Success("Settings reset", "Everything is back to the defaults.");
    }

    private void Open(string target)
    {
        var result = ShellLauncher.Open(target);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }
}
