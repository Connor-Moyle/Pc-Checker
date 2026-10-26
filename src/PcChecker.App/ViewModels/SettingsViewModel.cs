using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;
using PcChecker.Core.Updates;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>Basic settings: theme, advanced mode, restore point policy and update check. Extended by the settings module.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppUpdateService _updates;
    private readonly INotificationService _notifications;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _loading;

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private bool _advancedMode;

    [ObservableProperty]
    private bool _createRestorePointBefore;

    [ObservableProperty]
    private bool _createRestorePointAfter;

    [ObservableProperty]
    private bool _bypassRestorePointFrequencyLimit;

    [ObservableProperty]
    private bool _autoCheckForUpdates;

    [ObservableProperty]
    private bool _checkHealthOnStartup;

    [ObservableProperty]
    private string? _updateStatus;

    public SettingsViewModel(ISettingsService settings, IAppUpdateService updates, INotificationService notifications, ILogger<SettingsViewModel> logger)
        : base(settings)
    {
        _updates = updates;
        _notifications = notifications;
        _logger = logger;
        LoadFromSettings();
    }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    public string AppVersion => "PC Checker " + _updates.CurrentVersion;

    public string SettingsFilePath => Settings.SettingsFilePath;

    private void LoadFromSettings()
    {
        _loading = true;
        var s = Settings.Current;
        Theme = s.General.Theme;
        AdvancedMode = s.General.AdvancedMode;
        CheckHealthOnStartup = s.General.CheckHealthOnStartup;
        CreateRestorePointBefore = s.RestorePoints.CreateBeforeChanges;
        CreateRestorePointAfter = s.RestorePoints.CreateAfterChanges;
        BypassRestorePointFrequencyLimit = s.RestorePoints.BypassFrequencyLimit;
        AutoCheckForUpdates = s.Updates.AutoCheck;
        _loading = false;
    }

    partial void OnThemeChanged(AppTheme value)
    {
        if (_loading)
        {
            return;
        }

        Settings.Current.General.Theme = value;
        ApplyTheme(value);
        _ = SaveAsync();
    }

    partial void OnAdvancedModeChanged(bool value) => Persist(s => s.General.AdvancedMode = value);
    partial void OnCheckHealthOnStartupChanged(bool value) => Persist(s => s.General.CheckHealthOnStartup = value);
    partial void OnCreateRestorePointBeforeChanged(bool value) => Persist(s => s.RestorePoints.CreateBeforeChanges = value);
    partial void OnCreateRestorePointAfterChanged(bool value) => Persist(s => s.RestorePoints.CreateAfterChanges = value);
    partial void OnBypassRestorePointFrequencyLimitChanged(bool value) => Persist(s => s.RestorePoints.BypassFrequencyLimit = value);
    partial void OnAutoCheckForUpdatesChanged(bool value) => Persist(s => s.Updates.AutoCheck = value);

    private void Persist(Action<AppSettings> apply)
    {
        if (_loading)
        {
            return;
        }

        apply(Settings.Current);
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            await Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save settings.");
            ErrorMessage = "Settings could not be saved: " + ex.Message;
        }
    }

    /// <summary>Applies the chosen theme to the running app.</summary>
    public static void ApplyTheme(AppTheme theme)
    {
        var applied = theme switch
        {
            AppTheme.Light => ApplicationTheme.Light,
            AppTheme.Dark => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(applied, WindowBackdropType.Mica, true);
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => RunBusyAsync(async ct =>
    {
        UpdateStatus = null;
        var info = await _updates.CheckAsync(ct);
        Settings.Current.Updates.LastCheckedUtc = DateTimeOffset.UtcNow;
        await SaveAsync();
        if (info is null)
        {
            UpdateStatus = "Could not check for updates right now. You can always download the latest version from the releases page.";
            return;
        }

        UpdateStatus = info.IsNewer
            ? $"Version {info.LatestVersion} is available (you have {info.CurrentVersion})."
            : $"You are up to date ({info.CurrentVersion}).";
        if (info.IsNewer)
        {
            _notifications.Info("Update available", $"PC Checker {info.LatestVersion} is ready to download.");
        }
    }, "Checking for updates…");

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", Settings.SettingsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
