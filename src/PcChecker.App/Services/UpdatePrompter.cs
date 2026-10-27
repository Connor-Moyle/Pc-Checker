using Microsoft.Extensions.Logging;
using PcChecker.Core.Settings;
using PcChecker.Core.Updates;

namespace PcChecker.App.Services;

/// <summary>
/// Runs the automatic update check shortly after startup and tells the user about the result with a snackbar; when
/// auto-download is on and the app was installed by the updater, it downloads the update and offers a restart.
/// The check itself runs at most once per process (<see cref="CheckOnceAsync"/>), so the Dashboard banner and the
/// prompter share one network request.
/// </summary>
public sealed class UpdatePrompter
{
    private static readonly object Gate = new();
    private static Task<AppUpdateInfo?>? _check;

    private readonly IAppUpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly ILogger<UpdatePrompter> _logger;

    public UpdatePrompter(IAppUpdateService updates, ISettingsService settings, INotificationService notifications,
        IDialogService dialogs, ILogger<UpdatePrompter> logger)
    {
        _updates = updates;
        _settings = settings;
        _notifications = notifications;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>The newest result of the startup check (null until it completes, or when it failed).</summary>
    public static AppUpdateInfo? LastResult { get; private set; }

    /// <summary>Raised on a background thread whenever <see cref="LastResult"/> changes.</summary>
    public static event EventHandler? ResultChanged;

    /// <summary>
    /// Starts the update check if it has not run yet and returns the shared task. Never throws: failures resolve to null.
    /// </summary>
    public static Task<AppUpdateInfo?> CheckOnceAsync(IAppUpdateService updates, CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            _check ??= RunCheckAsync(updates, cancellationToken);
            return _check;
        }
    }

    /// <summary>Records a result from a manual check (Settings page) so the Dashboard banner follows it.</summary>
    public static void Publish(AppUpdateInfo? info)
    {
        LastResult = info;
        ResultChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Forgets the cached check so the next <see cref="CheckOnceAsync"/> runs again (after a manual check or channel change).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _check = null;
        }
    }

    /// <summary>The startup flow. <paramref name="interactive"/> is false for unattended (screenshot) runs: no dialogs or snackbars.</summary>
    public async Task RunAsync(bool interactive, CancellationToken cancellationToken)
    {
        if (!_settings.Current.Updates.AutoCheck)
        {
            return;
        }

        try
        {
            // Let the window finish its first layout before the network is touched.
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
            var info = await CheckOnceAsync(_updates, cancellationToken);
            await SaveLastCheckedAsync();
            if (info is null || !info.IsNewer)
            {
                return;
            }

            if (!interactive)
            {
                return;
            }

            if (_settings.Current.Updates.AutoDownload && _updates.IsInstalledViaUpdater)
            {
                var download = await _updates.DownloadAsync(null, cancellationToken);
                if (download.Success)
                {
                    bool restart = await _dialogs.ConfirmAsync("Update ready to install",
                        $"PC Checker {info.LatestVersion} has been downloaded. Restart now to install it? Your settings are kept.",
                        "Restart now");
                    if (restart)
                    {
                        var applied = _updates.ApplyAndRestart();
                        if (!applied.Success)
                        {
                            _notifications.Error("Update not installed", applied.Message);
                        }
                    }

                    return;
                }

                _logger.LogWarning("Automatic update download failed: {Message}", download.Message);
            }

            _notifications.Info("Update available",
                $"PC Checker {info.LatestVersion} is available (you have {info.CurrentVersion}). Open Settings to download it.");
        }
        catch (OperationCanceledException)
        {
            // App is closing.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The startup update check failed.");
        }
    }

    private static async Task<AppUpdateInfo?> RunCheckAsync(IAppUpdateService updates, CancellationToken cancellationToken)
    {
        AppUpdateInfo? info = null;
        try
        {
            info = await updates.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The service already reports failures as null; anything else is swallowed here so callers never fault.
        }

        Publish(info);
        return info;
    }

    private async Task SaveLastCheckedAsync()
    {
        try
        {
            _settings.Current.Updates.LastCheckedUtc = DateTimeOffset.UtcNow;
            await _settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not store the update check time.");
        }
    }
}
