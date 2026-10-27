using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers.Sources;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Runs the offer chosen for a device (vendor package, Windows Update driver, tool or page) and keeps the update
/// source options in settings.
/// </summary>
public sealed partial class DriversViewModel
{
    private bool _loadingSourceSettings;

    [ObservableProperty]
    private bool _useWindowsUpdate;

    [ObservableProperty]
    private bool _useNvidia;

    [ObservableProperty]
    private bool _useAmd;

    [ObservableProperty]
    private bool _useIntel;

    [ObservableProperty]
    private bool _usePcMakerTools;

    /// <summary>0 Game Ready, 1 Studio.</summary>
    [ObservableProperty]
    private int _nvidiaBranchIndex;

    /// <summary>0 interactive, 1 silent, 2 silent clean install.</summary>
    [ObservableProperty]
    private int _nvidiaInstallModeIndex;

    /// <summary>0 ask each time, 1 never (open the page).</summary>
    [ObservableProperty]
    private int _vendorDownloadsIndex;

    /// <summary>Runs the chosen offer of a device.</summary>
    [RelayCommand]
    private async Task RunOfferAsync(DeviceOffersRow? device)
    {
        var chosen = device?.Chosen;
        if (device is null || chosen is null)
        {
            return;
        }

        switch (chosen.Offer.Action)
        {
            case DriverOfferAction.WindowsUpdate when chosen.UpdateRow is not null:
                await InstallWindowsUpdatesAsync([chosen.UpdateRow]);
                break;
            case DriverOfferAction.VendorPackage:
                await InstallVendorPackageAsync(device, chosen.Offer);
                break;
            case DriverOfferAction.OpenTool:
                var opened = _sources.OpenTool(chosen.Offer);
                if (!opened.Success)
                {
                    ErrorMessage = opened.Message;
                }

                break;
            case DriverOfferAction.InstallTool:
                await InstallToolAsync(device, chosen.Offer);
                break;
            default:
                OpenLink(chosen.Offer.Url ?? chosen.Offer.FallbackPageUrl);
                break;
        }
    }

    /// <summary>Opens an offer's release notes or product page.</summary>
    [RelayCommand]
    private void OpenOfferDetails(OfferOptionRow? offer) => OpenLink(offer?.Offer.DetailsUrl ?? offer?.Offer.FallbackPageUrl);

    private async Task InstallVendorPackageAsync(DeviceOffersRow device, DriverOffer offer)
    {
        OperationResult<VendorPackageInfo>? inspected = null;
        await RunBusyAsync(async ct => inspected = await _sources.InspectPackageAsync(offer, ct), Loc.T("Checking the download…"));
        if (inspected is null)
        {
            return;
        }

        if (!inspected.Success || inspected.Value is not { } info)
        {
            await OfferVendorPageAsync(offer, inspected.Message);
            return;
        }

        var settings = Settings.Current.DriverSources ?? new DriverSourceSettings();
        var mode = offer.Source == DriverSourceKind.Nvidia
            ? settings.NvidiaInstallMode switch
            {
                NvidiaInstallMode.Silent => Loc.T("The NVIDIA installer runs silently (-s -noreboot)."),
                NvidiaInstallMode.SilentClean => Loc.T("The NVIDIA installer runs silently as a clean install (-s -clean -noreboot); NVIDIA settings return to their defaults."),
                _ => Loc.T("The NVIDIA installer opens so you can choose its options."),
            }
            : Loc.T("The installer opens so you can follow its steps.");
        var size = info.SizeBytes is > 0 ? ByteSize.Format(info.SizeBytes.Value) : Loc.T("unknown size");
        var host = Uri.TryCreate(info.Url, UriKind.Absolute, out var uri) ? uri.Host : info.Url;
        var message = Loc.F("PC Checker will download {0} ({1}) for {2} from {3}.", info.FileName, size, device.DeviceName, host) + "\n\n"
            + Loc.F("Before anything runs, the file's digital signature and certificate chain are checked and it must be signed by {0}. Otherwise it is deleted without running.", info.ExpectedSigner) + "\n\n"
            + Loc.T("A restore point is created before the installer starts.") + " " + mode + " "
            + Loc.T("The screen may go black for a moment while a display driver changes; save your work first.");
        if (!await _dialogs.ConfirmAsync(Loc.F("Install {0} {1}?", offer.Title, offer.Version ?? string.Empty).Replace(" ?", "?", StringComparison.Ordinal), message, Loc.T("Download and install")))
        {
            return;
        }

        OperationResult? result = null;
        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = FormatProgress(p));
            result = await _sources.InstallPackageAsync(offer, progress, ct);
            RebootRequired |= result.RebootRequired;
            device.StateText = result.Success ? Loc.T("Installed from the maker") : result.Message.Split('\n')[0];
            ShowResult(result.Success, result.Message, offer.Title);
            if (result.Success)
            {
                await ScanAsyncCore(null, CancellationToken.None);
            }
        }, Loc.F("Installing {0}…", offer.Title));

        if (result is { Success: false } && offer.FallbackPageUrl is not null)
        {
            await OfferVendorPageAsync(offer, result.Message);
        }
    }

    private async Task InstallToolAsync(DeviceOffersRow device, DriverOffer offer)
    {
        if (offer.WingetId is null)
        {
            return;
        }

        var command = OemToolCatalog.WingetCommandText(offer.WingetId);
        var message = Loc.F("PC Checker will install {0} with the Windows Package Manager:", offer.Title) + "\n\n" + command + "\n\n"
            + Loc.T("This accepts the package's licence and the winget source terms on your behalf. A restore point is created first (per your settings).");
        if (!await _dialogs.ConfirmAsync(Loc.F("Install {0}?", offer.Title), message, Loc.T("Install")))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = FormatProgress(p));
            var result = await _sources.InstallToolAsync(offer, progress, ct);
            device.StateText = result.Success ? Loc.T("Installed. Search again to open it from here.") : result.Message;
            ShowResult(result.Success, result.Message, offer.Title);
        }, Loc.F("Installing {0}…", offer.Title), cancellable: false);
    }

    /// <summary>After a refused or failed download: offers the vendor's page so the user still has a way forward.</summary>
    private async Task OfferVendorPageAsync(DriverOffer offer, string reason)
    {
        var page = offer.FallbackPageUrl ?? offer.DetailsUrl;
        if (page is null)
        {
            ErrorMessage = reason;
            return;
        }

        if (await _dialogs.ConfirmAsync(Loc.T("Open the vendor page?"), reason + "\n\n" + Loc.T("You can download the driver from the maker's own page instead."), Loc.T("Open the vendor page")))
        {
            OpenLink(page);
        }
    }

    private void LoadSourceSettings()
    {
        _loadingSourceSettings = true;
        try
        {
            var s = Settings.Current.DriverSources ?? new DriverSourceSettings();
            UseWindowsUpdate = s.WindowsUpdate;
            UseNvidia = s.Nvidia;
            UseAmd = s.Amd;
            UseIntel = s.Intel;
            UsePcMakerTools = s.PcMakerTools;
            NvidiaBranchIndex = (int)s.NvidiaBranch;
            NvidiaInstallModeIndex = (int)s.NvidiaInstallMode;
            VendorDownloadsIndex = (int)s.VendorDownloads;
        }
        finally
        {
            _loadingSourceSettings = false;
        }
    }

    partial void OnUseWindowsUpdateChanged(bool value) => PersistSources(s => s.WindowsUpdate = value);
    partial void OnUseNvidiaChanged(bool value) => PersistSources(s => s.Nvidia = value);
    partial void OnUseAmdChanged(bool value) => PersistSources(s => s.Amd = value);
    partial void OnUseIntelChanged(bool value) => PersistSources(s => s.Intel = value);
    partial void OnUsePcMakerToolsChanged(bool value) => PersistSources(s => s.PcMakerTools = value);
    partial void OnNvidiaBranchIndexChanged(int value) => PersistSources(s => s.NvidiaBranch = Enum.IsDefined((NvidiaDriverBranch)value) ? (NvidiaDriverBranch)value : NvidiaDriverBranch.GameReady);
    partial void OnNvidiaInstallModeIndexChanged(int value) => PersistSources(s => s.NvidiaInstallMode = Enum.IsDefined((NvidiaInstallMode)value) ? (NvidiaInstallMode)value : NvidiaInstallMode.Interactive);
    partial void OnVendorDownloadsIndexChanged(int value) => PersistSources(s => s.VendorDownloads = Enum.IsDefined((VendorDownloadMode)value) ? (VendorDownloadMode)value : VendorDownloadMode.AskEachTime);

    private void PersistSources(Action<DriverSourceSettings> apply)
    {
        if (_loadingSourceSettings)
        {
            return;
        }

        Settings.Current.DriverSources ??= new DriverSourceSettings();
        apply(Settings.Current.DriverSources);
        _ = SaveSourceSettingsAsync();
    }

    private async Task SaveSourceSettingsAsync()
    {
        try
        {
            await Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Driver source settings could not be saved.");
            ErrorMessage = Loc.T("Settings could not be saved:") + " " + ex.Message;
        }
    }
}
