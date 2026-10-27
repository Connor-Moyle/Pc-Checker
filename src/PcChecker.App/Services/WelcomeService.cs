using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Dialogs;
using PcChecker.Core.Settings;
using PcChecker.Core.Shell;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace PcChecker.App.Services;

/// <summary>
/// The short welcome on the first start (and from Settings → About → Show welcome again): what PC Checker does, the
/// language, Advanced mode, and a choice between running a full scan and looking around first. The rules for when it
/// appears are in <see cref="WelcomeRules"/>.
/// </summary>
public sealed class WelcomeService
{
    private static readonly TimeSpan HostPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HostWaitLimit = TimeSpan.FromMinutes(2);

    private readonly IServiceProvider _services;
    private readonly IContentDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly ILogger<WelcomeService> _logger;
    private bool _showing;

    public WelcomeService(IServiceProvider services, IContentDialogService dialogs, ISettingsService settings, ILogger<WelcomeService> logger)
    {
        _services = services;
        _dialogs = dialogs;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Shows the welcome when this is a first start (never in screenshot or scheduled runs).</summary>
    public Task ShowOnFirstStartAsync() =>
        WelcomeRules.ShouldShow(_settings.Current.General.WelcomeSeen, AppOptions.Current) ? ShowAsync() : Task.CompletedTask;

    /// <summary>Shows the welcome now. It is marked as seen as soon as it appears, so it greets each user only once.</summary>
    public async Task ShowAsync()
    {
        if (_showing)
        {
            return;
        }

        _showing = true;
        try
        {
            // Another dialog (an update offer, a crash-recovery question) may already be open; wait for it to close.
            if (!await WaitForFreeDialogHostAsync())
            {
                return;
            }

            await MarkSeenAsync();
            var dialog = new ContentDialog
            {
                Title = Loc.T("Welcome to PC Checker"),
                Content = new WelcomeContent { DataContext = _services.GetRequiredService<SettingsViewModel>() },
                PrimaryButtonText = Loc.T("Run a full scan"),
                CloseButtonText = Loc.T("Look around first"),
                DefaultButton = ContentDialogButton.Primary,
                IsSecondaryButtonEnabled = false,
                DialogMaxWidth = 620,
            };

            var result = await _dialogs.ShowAsync(dialog, CancellationToken.None);
            _logger.LogInformation("Welcome closed with {Choice}.", result == ContentDialogResult.Primary ? "full scan" : "look around");
            if (result == ContentDialogResult.Primary)
            {
                await _services.GetRequiredService<QuickActionRunner>().RunAsync(QuickActionIds.FullScan, waitWhileBusy: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The welcome could not be shown.");
        }
        finally
        {
            _showing = false;
        }
    }

    private async Task<bool> WaitForFreeDialogHostAsync()
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            var host = _dialogs.GetDialogHost();
            if (host is null)
            {
                return false;
            }

            if (host.Content is null)
            {
                return true;
            }

            if (waited >= HostWaitLimit)
            {
                return false;
            }

            await Task.Delay(HostPollInterval);
            waited += HostPollInterval;
        }
    }

    private async Task MarkSeenAsync()
    {
        if (_settings.Current.General.WelcomeSeen == true)
        {
            return;
        }

        _settings.Current.General.WelcomeSeen = true;
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save that the welcome was shown.");
        }
    }
}
