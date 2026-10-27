using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcChecker.App.ViewModels;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Updates;

namespace PcChecker.App.Services;

/// <summary>
/// "Update now" from anywhere (the update prompt, the Dashboard banner, a tray notification): downloads the new
/// PcChecker.exe from GitHub, verifies its SHA-256, then asks once before closing, swapping and restarting.
/// Copies that cannot replace themselves open the releases page instead. Only one update runs at a time, and the
/// restart waits while a change, a long page task or a scheduled job is still running.
/// </summary>
public sealed class AppUpdateFlow
{
    private readonly IAppUpdateService _updates;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly ScheduledJobExecutor _jobs;
    private readonly ILogger<AppUpdateFlow> _logger;
    private int _running;

    public AppUpdateFlow(IAppUpdateService updates, INotificationService notifications, IDialogService dialogs, ScheduledJobExecutor jobs,
        ILogger<AppUpdateFlow> logger)
    {
        _updates = updates;
        _notifications = notifications;
        _dialogs = dialogs;
        _jobs = jobs;
        _logger = logger;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// Why restarting now would cut work short (a guarded change, a page's scan or install, a scheduled job), or null
    /// when nothing is running.
    /// </summary>
    public string? WorkStillRunning()
    {
        if (ChangeGuard.IsAnyChangeRunning)
        {
            return Localization.Loc.T("A change to Windows is still being made.");
        }

        if (ViewModelBase.IsAnyBusyWorkRunning)
        {
            return Localization.Loc.T("A page is still working on something (a scan, install or test).");
        }

        return _jobs.IsAnyRunning ? Localization.Loc.T("A scheduled task is still running.") : null;
    }

    /// <summary>
    /// Downloads, verifies and installs <paramref name="info"/>. The app asks before restarting when
    /// <paramref name="askBeforeRestart"/> is true or when it had to download first (the user may have started
    /// something meanwhile); with an update already downloaded, "Update now" restarts straight away. Either way the
    /// restart is put off while <see cref="WorkStillRunning"/> reports work.
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
            bool downloadedNow = false;
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

                downloadedNow = true;
            }

            if (askBeforeRestart || downloadedNow)
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

            if (WorkStillRunning() is { } busy)
            {
                _notifications.Info(Localization.Loc.T("Update ready"),
                    busy + " " + Localization.Loc.T("When it has finished, press Update now again to restart and install the update."));
                return;
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
