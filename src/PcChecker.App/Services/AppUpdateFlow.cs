using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Updates;

namespace PcChecker.App.Services;

/// <summary>
/// "Update now" from anywhere (the update prompt, the Dashboard banner, a tray notification): downloads the new
/// PcChecker.exe from GitHub, verifies its SHA-256, then asks once before closing, swapping and restarting.
/// Copies that cannot replace themselves open the releases page instead. Only one update runs at a time.
/// </summary>
public sealed class AppUpdateFlow
{
    private readonly IAppUpdateService _updates;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly ILogger<AppUpdateFlow> _logger;
    private int _running;

    public AppUpdateFlow(IAppUpdateService updates, INotificationService notifications, IDialogService dialogs, ILogger<AppUpdateFlow> logger)
    {
        _updates = updates;
        _notifications = notifications;
        _dialogs = dialogs;
        _logger = logger;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// Downloads, verifies and installs <paramref name="info"/>. When <paramref name="askBeforeRestart"/> is false the app
    /// restarts as soon as the download is verified (the user already chose "Update now").
    /// </summary>
    public async Task UpdateNowAsync(AppUpdateInfo info, bool askBeforeRestart, CancellationToken cancellationToken = default)
    {
        if (!_updates.IsInstalledViaUpdater)
        {
            OpenReleasesPage();
            return;
        }

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            _notifications.Info(Localization.Loc.T("Update in progress"), Localization.Loc.T("PC Checker is already downloading the update."));
            return;
        }

        try
        {
            if (!_updates.IsUpdateDownloaded)
            {
                _notifications.Info(Localization.Loc.T("Downloading update"), Localization.Loc.F("PC Checker {0} is downloading. You can keep working; it restarts when you say so.", info.LatestVersion));
                int lastReported = -1;
                var progress = new Progress<ProgressUpdate>(p =>
                {
                    // A notification every 25 % is enough; the Settings page shows the exact progress.
                    int step = (int)((p.Percent ?? 0) / 25);
                    if (step > lastReported && step is > 0 and < 4)
                    {
                        lastReported = step;
                        _logger.LogInformation("Update download {Percent:0}%", p.Percent);
                    }
                });
                var download = await _updates.DownloadAsync(progress, cancellationToken);
                if (!download.Success)
                {
                    _notifications.Error(Localization.Loc.T("Update not downloaded"), download.Message);
                    return;
                }
            }

            if (askBeforeRestart)
            {
                bool restart = await _dialogs.ConfirmAsync(Localization.Loc.T("Update ready to install"),
                    Localization.Loc.F("PC Checker {0} has been downloaded and checked. Restart now to install it? Your settings are kept.", info.LatestVersion),
                    Localization.Loc.T("Restart now"));
                if (!restart)
                {
                    _notifications.Info(Localization.Loc.T("Update ready"), Localization.Loc.T("It installs the next time you choose Install in Settings → Updates, or press Update now again."));
                    return;
                }
            }

            var applied = _updates.ApplyAndRestart();
            if (!applied.Success)
            {
                _notifications.Error(Localization.Loc.T("Update not installed"), applied.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The update could not be installed.");
            _notifications.Error(Localization.Loc.T("Update not installed"), ex.Message);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateLinks.LatestReleasePage) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser registered: nothing else to do.
        }
    }
}
