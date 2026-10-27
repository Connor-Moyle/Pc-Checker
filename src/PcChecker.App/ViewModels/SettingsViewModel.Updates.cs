using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;
using PcChecker.Core.Updates;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>Updates section: channel, automatic checks, manual check, download and install.</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private UpdateChannel _updateChannel;

    [ObservableProperty]
    private bool _autoCheckForUpdates;

    [ObservableProperty]
    private bool _autoDownloadUpdates;

    [ObservableProperty]
    private string? _updateStatus;

    [ObservableProperty]
    private InfoBarSeverity _updateStatusSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    [NotifyPropertyChangedFor(nameof(CanInstallUpdate))]
    private bool _isUpdateDownloaded;

    [ObservableProperty]
    private string? _latestVersion;

    [ObservableProperty]
    private string? _releaseNotes;

    [ObservableProperty]
    private string? _lastCheckedText;

    [ObservableProperty]
    private double _downloadPercent;

    [ObservableProperty]
    private bool _isDownloading;

    public bool IsPortableBuild => !_updates.IsInstalledViaUpdater;

    public bool CanDownloadUpdate => IsUpdateAvailable && !IsPortableBuild && !IsUpdateDownloaded;

    public bool CanInstallUpdate => IsUpdateDownloaded && !IsPortableBuild;

    private void LoadUpdateSettings(AppSettings s)
    {
        UpdateChannel = s.Updates.Channel;
        AutoCheckForUpdates = s.Updates.AutoCheck;
        AutoDownloadUpdates = s.Updates.AutoDownload;
        UpdateMode = UpdatePromptRules.EffectiveMode(s.Updates);
        RefreshSkippedVersion(s);
        LastCheckedText = s.Updates.LastCheckedUtc is { } t
            ? "Last checked " + Converters.RelativeTimeConverter.Format(t.ToLocalTime(), DateTimeOffset.Now)
            : "Not checked yet";
        IsUpdateDownloaded = _updates.IsUpdateDownloaded;
    }

    partial void OnUpdateChannelChanged(UpdateChannel value)
    {
        if (_loading)
        {
            return;
        }

        Persist(s => s.Updates.Channel = value);
        UpdatePrompter.Reset();
        UpdateStatus = value == UpdateChannel.Beta
            ? "Beta channel selected: pre-release builds will be offered. Check for updates to see what is available."
            : "Stable channel selected. Check for updates to see what is available.";
        UpdateStatusSeverity = InfoBarSeverity.Informational;
    }

    partial void OnAutoCheckForUpdatesChanged(bool value) => Persist(s => s.Updates.AutoCheck = value);
    partial void OnAutoDownloadUpdatesChanged(bool value) => Persist(s => s.Updates.AutoDownload = value);

    public IReadOnlyList<UpdateMode> UpdateModes { get; } = Enum.GetValues<UpdateMode>();

    [ObservableProperty]
    private UpdateMode _updateMode;

    [ObservableProperty]
    private string? _skippedVersionText;

    [ObservableProperty]
    private bool _hasSkippedVersion;

    partial void OnUpdateModeChanged(UpdateMode value)
    {
        if (_loading)
        {
            return;
        }

        // The mode replaces the older "download in the background" switch.
        Persist(s =>
        {
            s.Updates.Mode = value;
            s.Updates.AutoDownload = false;
        });
    }

    private void RefreshSkippedVersion(AppSettings s)
    {
        HasSkippedVersion = !string.IsNullOrWhiteSpace(s.Updates.SkippedVersion);
        SkippedVersionText = HasSkippedVersion
            ? Localization.Loc.F("PC Checker {0} is not offered because you chose Skip this version.", s.Updates.SkippedVersion)
            : null;
    }

    [RelayCommand]
    private void ClearSkippedVersion()
    {
        Persist(s => s.Updates.SkippedVersion = null);
        HasSkippedVersion = false;
        SkippedVersionText = null;
        UpdatePrompter.Reset();
    }

    private void ShowUpdateResult(AppUpdateInfo? info)
    {
        if (info is null)
        {
            return;
        }

        LatestVersion = info.LatestVersion;
        ReleaseNotes = info.ReleaseNotes;
        IsUpdateAvailable = info.IsNewer;
        IsUpdateDownloaded = _updates.IsUpdateDownloaded;
        if (info.IsNewer)
        {
            UpdateStatus = $"Version {info.LatestVersion}{(info.IsPrerelease ? " (pre-release)" : string.Empty)} is available. You have {info.CurrentVersion}."
                + (IsPortableBuild ? " This is a portable build: download the new version from the releases page." : string.Empty);
            UpdateStatusSeverity = InfoBarSeverity.Success;
        }
        else
        {
            UpdateStatus = $"You are up to date ({info.CurrentVersion}).";
            UpdateStatusSeverity = InfoBarSeverity.Informational;
        }
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => RunBusyAsync(async ct =>
    {
        UpdateStatus = null;
        UpdatePrompter.Reset();
        var info = await _updates.CheckAsync(ct);
        Settings.Current.Updates.LastCheckedUtc = DateTimeOffset.UtcNow;
        await SaveNowAsync();
        LastCheckedText = "Last checked just now";
        UpdatePrompter.Publish(info);
        if (info is null)
        {
            UpdateStatus = "Could not check for updates right now (no connection, or GitHub did not answer). You can always download the latest version from the releases page.";
            UpdateStatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        ShowUpdateResult(info);
        if (info.IsNewer)
        {
            _notifications.Info("Update available", $"PC Checker {info.LatestVersion} is ready to download.");
        }
    }, "Checking for updates…");

    [RelayCommand]
    private Task DownloadUpdateAsync() => RunBusyAsync(async ct =>
    {
        IsDownloading = true;
        DownloadPercent = 0;
        try
        {
            var progress = new Progress<ProgressUpdate>(p => DispatcherHelper.PostToUi(() =>
            {
                DownloadPercent = p.Percent ?? DownloadPercent;
                BusyText = p.Detail is null ? $"{p.Stage} ({DownloadPercent:0}%)" : $"{p.Stage} ({DownloadPercent:0}%) — {p.Detail}";
            }));
            var result = await _updates.DownloadAsync(progress, ct);
            IsUpdateDownloaded = _updates.IsUpdateDownloaded;
            UpdateStatus = result.Message;
            UpdateStatusSeverity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            if (result.Success)
            {
                _notifications.Success("Update downloaded", "Press Install and restart when you are ready.");
            }
        }
        finally
        {
            IsDownloading = false;
        }
    }, "Downloading update…");

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        bool ok = await _dialogs.ConfirmAsync("Install update and restart?",
            $"PC Checker will close, install version {LatestVersion ?? "the downloaded update"} and open again. Your settings are kept. " +
            "Make sure nothing in the app is running (scan, test or download) before continuing.", "Install and restart");
        if (!ok)
        {
            return;
        }

        if (IsDemoMode)
        {
            _notifications.Info("Demo mode", "Nothing is installed in demo mode.");
            return;
        }

        var result = _updates.ApplyAndRestart();
        if (!result.Success)
        {
            IsUpdateDownloaded = _updates.IsUpdateDownloaded;
            UpdateStatus = result.Message;
            UpdateStatusSeverity = InfoBarSeverity.Error;
            _notifications.Error("Update not installed", result.Message);
        }
    }
}
