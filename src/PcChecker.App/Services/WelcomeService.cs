using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.ViewModels;
using PcChecker.App.Views.Dialogs;
using PcChecker.Core.Sensors.PawnIo;
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

    /// <summary>
    /// Shows the welcome when this is a first start (never in screenshot or scheduled runs). On later starts it offers
    /// the PawnIO sensor driver instead when it is missing, as <see cref="PawnIoOfferRules"/> allows.
    /// </summary>
    public Task ShowOnFirstStartAsync() =>
        WelcomeRules.ShouldShow(_settings.Current.General.WelcomeSeen, AppOptions.Current) ? ShowAsync() : OfferPawnIoAsync();

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

            // The PawnIO box starts ticked only on the first welcome and only when the start-up offer was not turned down;
            // reopened from Settings it starts unticked, so rereading the tips never installs a driver by itself.
            bool firstWelcome = _settings.Current.General.WelcomeSeen != true;
            await MarkSeenAsync();
            var settingsVm = _services.GetRequiredService<SettingsViewModel>();
            bool startTicked = firstWelcome && !_settings.Current.General.PawnIoOfferDismissed;
            settingsVm.InstallPawnIoFromWelcome = startTicked;
            await settingsVm.RefreshPawnIoAsync();
            var dialog = new ContentDialog
            {
                Title = Loc.T("Welcome to PC Checker"),
                Content = new WelcomeContent { DataContext = settingsVm },
                PrimaryButtonText = Loc.T("Run a full scan"),
                CloseButtonText = Loc.T("Look around first"),
                DefaultButton = ContentDialogButton.Primary,
                IsSecondaryButtonEnabled = false,
                DialogMaxWidth = 620,
            };

            // Esc also closes with None, like "Look around first"; only a button press counts as the PawnIO choice.
            bool buttonPressed = false;
            dialog.ButtonClicked += (_, _) => buttonPressed = true;
            var result = await _dialogs.ShowAsync(dialog, CancellationToken.None);
            _logger.LogInformation("Welcome closed with {Choice}.", result == ContentDialogResult.Primary ? "full scan" : buttonPressed ? "look around" : "Esc");
            if (buttonPressed)
            {
                await ApplyWelcomePawnIoChoiceAsync(settingsVm, startTicked);
            }

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

    /// <summary>
    /// The welcome's PawnIO choice when it was closed with one of its buttons: ticked installs it now (the tick is the
    /// consent; the install is journaled), unticking it means no start-up offer later. Closing it with Esc changes nothing.
    /// Settings keeps the install button either way.
    /// </summary>
    private async Task ApplyWelcomePawnIoChoiceAsync(SettingsViewModel settingsVm, bool startTicked)
    {
        if (!settingsVm.ShowPawnIoOnWelcome)
        {
            return;
        }

        if (settingsVm.InstallPawnIoFromWelcome)
        {
            _logger.LogInformation("Installing PawnIO as chosen on the welcome.");
            await settingsVm.InstallPawnIoConfirmedAsync();
        }
        else if (startTicked)
        {
            _settings.Current.General.PawnIoOfferDismissed = true;
            await SaveQuietlyAsync();
        }
    }

    /// <summary>The start-up offer: Install (or Repair) PawnIO, Don't ask again, or Not now (asks again in a week).</summary>
    private async Task OfferPawnIoAsync()
    {
        try
        {
            var flow = _services.GetRequiredService<PawnIoInstallFlow>();
            var status = await Task.Run(flow.GetStatus);
            var general = _settings.Current.General;
            if (!PawnIoOfferRules.ShouldOfferAtStart(status, general.PawnIoOfferDismissed, general.PawnIoOfferedUtc, DateTimeOffset.UtcNow, AppOptions.Current)
                || !await WaitForFreeDialogHostAsync())
            {
                return;
            }

            bool repair = PawnIoOfferRules.IsRepair(status);
            string message = repair
                ? Loc.T("PawnIO is installed but not running, so the CPU and motherboard sensors (CPU and case fans) cannot be read. Repair reinstalls the official signed driver; a restart may still be needed.")
                : Loc.T("PC Checker can read your CPU and motherboard sensors, including the CPU and case fans plugged into the motherboard, with the free, signed PawnIO driver. Without it only graphics card fans and a few temperatures appear.");
            message += "\n\n" + Loc.T("You can install or remove it any time from Settings → Monitoring.");
            var choice = await _services.GetRequiredService<IDialogService>().ChooseAsync(Loc.T("Read all your fans and sensors?"), message,
                repair ? Loc.T("Repair PawnIO") : Loc.T("Install PawnIO"), Loc.T("Don't ask again"), Loc.T("Not now"));
            switch (choice)
            {
                case DialogChoice.Primary:
                    _logger.LogInformation("Installing PawnIO from the start-up offer.");
                    await _services.GetRequiredService<SettingsViewModel>().InstallPawnIoConfirmedAsync();
                    break;
                case DialogChoice.Secondary:
                    general.PawnIoOfferDismissed = true;
                    await SaveQuietlyAsync();
                    break;
                default:
                    general.PawnIoOfferedUtc = DateTimeOffset.UtcNow;
                    await SaveQuietlyAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The PawnIO offer could not be shown.");
        }
    }

    private async Task SaveQuietlyAsync()
    {
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the PawnIO choice.");
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
