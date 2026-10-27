using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Sensors.PawnIo;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Sensor driver (PawnIO): its status, install or repair from Settings, whether to offer it at start-up, and the
/// ticked choice on the first-start welcome.
/// </summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PawnIoStatusText), nameof(PawnIoNeedsAttention), nameof(PawnIoIsRunning), nameof(PawnIoInstallText),
        nameof(ShowPawnIoOnWelcome))]
    private PawnIoStatus _pawnIoStatus = new(PawnIoState.Unknown, null, null, null);

    /// <summary>
    /// The welcome's "Install PawnIO" choice. <see cref="WelcomeService"/> ticks it only on the first welcome when the
    /// start-up offer was not turned down; unticking it also stops the start-up offer.
    /// </summary>
    [ObservableProperty]
    private bool _installPawnIoFromWelcome = true;

    [ObservableProperty]
    private bool _offerPawnIoAtStart = true;

    public string PawnIoStatusText => PawnIoStatus.State switch
    {
        PawnIoState.Running when !string.IsNullOrWhiteSpace(PawnIoStatus.Version) => Loc.F("Installed and running (version {0}). CPU and motherboard sensors, including the CPU and case fans, can be read.", PawnIoStatus.Version),
        PawnIoState.Running => Loc.T("Installed and running. CPU and motherboard sensors, including the CPU and case fans, can be read."),
        PawnIoState.InstalledNotRunning => Loc.T("Installed but not running. Restart the PC; if it still does not start, repair it here and check that Memory integrity or antivirus software is not blocking it."),
        PawnIoState.NotInstalled => Loc.T("Not installed. Without it only graphics card fans and a few temperatures can be read."),
        PawnIoState.NotSupported => Loc.T("Not needed on this system."),
        _ => Loc.T("Checking…"),
    };

    public bool PawnIoNeedsAttention => PawnIoStatus.NeedsAttention;

    public bool PawnIoIsRunning => PawnIoStatus.IsRunning;

    public string PawnIoInstallText => PawnIoOfferRules.IsRepair(PawnIoStatus) ? Loc.T("Repair PawnIO") : Loc.T("Install PawnIO");

    public bool ShowPawnIoOnWelcome => PawnIoOfferRules.ShowOnWelcome(PawnIoStatus, AppOptions.Current);

    /// <summary>Reads the PawnIO status off the UI thread (it opens a device and reads the registry).</summary>
    public async Task RefreshPawnIoAsync()
    {
        var status = await Task.Run(_pawnIo.GetStatus);
        PawnIoStatus = status;
    }

    partial void OnOfferPawnIoAtStartChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        Persist(s =>
        {
            s.General.PawnIoOfferDismissed = !value;
            if (value)
            {
                s.General.PawnIoOfferedUtc = null;
            }
        });
    }

    [RelayCommand]
    private Task RefreshPawnIo() => RefreshPawnIoAsync();

    [RelayCommand]
    private async Task InstallPawnIoAsync()
    {
        if (!await _pawnIo.ConfirmInstallAsync())
        {
            return;
        }

        await InstallPawnIoConfirmedAsync();
    }

    /// <summary>Installs (or repairs) PawnIO after the user already agreed, then reloads the sensors.</summary>
    public Task InstallPawnIoConfirmedAsync() => RunBusyAsync(async ct =>
    {
        var progress = new Progress<ProgressUpdate>(p => DispatcherHelper.PostToUi(() => BusyText = p.Detail is null ? p.Stage : $"{p.Stage} — {p.Detail}"));
        await _pawnIo.InstallAndReloadAsync(progress, ct);
        await RefreshPawnIoAsync();
    }, Loc.T("Installing PawnIO…"));

    /// <summary>Opens Windows Settings → Apps → Installed apps, where PawnIO is removed like any other program.</summary>
    [RelayCommand]
    private void OpenInstalledApps()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _notifications.Warning(Loc.T("Could not open Windows Settings"), ex.Message);
        }
    }
}
