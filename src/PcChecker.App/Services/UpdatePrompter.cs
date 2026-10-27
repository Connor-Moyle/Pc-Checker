using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Settings;
using PcChecker.Core.Updates;

namespace PcChecker.App.Services;

/// <summary>
/// Runs the automatic update check after startup and every few hours, and offers the update the way Settings asks
/// (dialog with Update now / Later / Skip this version by default).
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
    private readonly IServiceProvider _services;

    public UpdatePrompter(IAppUpdateService updates, ISettingsService settings, INotificationService notifications,
        IDialogService dialogs, IServiceProvider services, ILogger<UpdatePrompter> logger)
    {
        _services = services;
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

    /// <summary>
    /// The automatic check: shortly after startup and then every few hours while PC Checker runs (also from the tray).
    /// What happens with a newer release follows Settings → Updates: ask (Update now / Later / Skip this version),
    /// download automatically and ask before restarting, or only notify. <paramref name="interactive"/> is false for
    /// unattended (screenshot) runs: no dialogs or notifications.
    /// </summary>
    public async Task RunAsync(bool interactive, CancellationToken cancellationToken)
    {
        try
        {
            // Let the window finish its first layout before the network is touched.
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
            bool first = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_settings.Current.Updates.AutoCheck)
                {
                    if (!first)
                    {
                        Reset();
                    }

                    var info = await CheckOnceAsync(_updates, cancellationToken);
                    await SaveLastCheckedAsync();
                    if (interactive)
                    {
                        await HandleAsync(info, cancellationToken);
                    }
                }

                first = false;
                if (!interactive)
                {
                    return;
                }

                await Task.Delay(UpdatePromptRules.RecheckInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // App is closing.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The automatic update check failed.");
        }
    }

    private readonly HashSet<string> _offered = new(StringComparer.OrdinalIgnoreCase);

    private async Task HandleAsync(AppUpdateInfo? info, CancellationToken cancellationToken)
    {
        bool handled = info is not null && _offered.Contains(info.LatestVersion);
        var action = UpdatePromptRules.Decide(info, _settings.Current.Updates, _updates.IsInstalledViaUpdater, handled);
        if (action == UpdatePromptAction.None || info is null)
        {
            return;
        }

        _offered.Add(info.LatestVersion);
        var flow = _services.GetRequiredService<AppUpdateFlow>();
        switch (action)
        {
            case UpdatePromptAction.Ask when IsWindowVisible():
                await AskAsync(info, flow, cancellationToken);
                break;
            case UpdatePromptAction.Ask:
                // Hidden in the tray: a notification; clicking it brings the window back and asks.
                NotifyFromTray(info, () => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => AskAsync(info, flow, cancellationToken)));
                break;
            case UpdatePromptAction.DownloadThenAskToRestart:
                await flow.UpdateNowAsync(info, askBeforeRestart: true, cancellationToken);
                break;
            case UpdatePromptAction.Notify:
                Notify(info, Localization.Loc.T("Open Settings → Updates, or press Update now on the Dashboard."));
                break;
            case UpdatePromptAction.NotifyDownloadPage:
                Notify(info, Localization.Loc.T("This copy cannot update itself. Download the new PcChecker.exe from the releases page."));
                break;
        }
    }

    private async Task AskAsync(AppUpdateInfo info, AppUpdateFlow flow, CancellationToken cancellationToken)
    {
        var highlights = UpdatePromptRules.Highlights(info.ReleaseNotes);
        var message = Localization.Loc.F("PC Checker {0} is available. You have {1}.", info.LatestVersion, info.CurrentVersion)
            + (highlights.Length > 0 ? Environment.NewLine + Environment.NewLine + Localization.Loc.T("What's new:") + Environment.NewLine + highlights : string.Empty)
            + Environment.NewLine + Environment.NewLine
            + Localization.Loc.T("Update now downloads it from GitHub, checks it and restarts PC Checker. Your settings are kept.");
        var choice = await _dialogs.AskAsync(Localization.Loc.T("Update available"), message, Localization.Loc.T("Update now"), Localization.Loc.T("Skip this version"), Localization.Loc.T("Later"));
        switch (choice)
        {
            case DialogChoice.Primary:
                await flow.UpdateNowAsync(info, askBeforeRestart: false, cancellationToken);
                break;
            case DialogChoice.Secondary:
                _settings.Current.Updates.SkippedVersion = info.LatestVersion;
                await SaveSettingsAsync();
                _notifications.Info(Localization.Loc.T("Version skipped"), Localization.Loc.F("PC Checker will not offer {0} again. Newer versions are still offered.", info.LatestVersion));
                break;
        }
    }

    private void Notify(AppUpdateInfo info, string hint)
    {
        var title = Localization.Loc.T("Update available");
        var text = Localization.Loc.F("PC Checker {0} is available (you have {1}).", info.LatestVersion, info.CurrentVersion) + " " + Localization.Loc.T(hint);
        if (IsWindowVisible())
        {
            _notifications.Info(title, text);
        }
        else
        {
            NotifyFromTray(info, null);
        }
    }

    private void NotifyFromTray(AppUpdateInfo info, Action? onClick)
    {
        var tray = _services.GetService<Tray.TrayIconService>();
        var text = Localization.Loc.F("PC Checker {0} is available. Click to update.", info.LatestVersion);
        if (tray is null)
        {
            _notifications.Info(Localization.Loc.T("Update available"), text);
            return;
        }

        tray.Notify(Localization.Loc.T("Update available"), text, NotificationKind.Info, () =>
        {
            tray.ShowMainWindow();
            onClick?.Invoke();
        });
    }

    private static bool IsWindowVisible()
    {
        var window = System.Windows.Application.Current?.MainWindow;
        return window is not null && window.Dispatcher.Invoke(() => window.IsVisible && window.WindowState != System.Windows.WindowState.Minimized);
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the skipped update version.");
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
