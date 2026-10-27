using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Drivers.Sources;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The update half of the Drivers page: searching every enabled source, the device list with one offer chosen per
/// device, selecting, downloading and installing Windows Update drivers, vendor links and the restart prompt that
/// follows an install.
/// </summary>
public sealed partial class DriversViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdates))]
    [NotifyPropertyChangedFor(nameof(UpdatesTabHeader))]
    private int _updatesAvailable;

    [ObservableProperty]
    private string? _lastCheckText;

    [ObservableProperty]
    private bool _rebootRequired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceOffers))]
    private bool _updatesChecked;

    [ObservableProperty]
    private string? _sourcesText;

    /// <summary>The Windows Update rows behind the Windows Update offers (download and install work on these).</summary>
    public ObservableCollection<DriverUpdateRow> Updates { get; } = [];

    /// <summary>Every device with offers, grouped as the search returned them.</summary>
    public ObservableCollection<DeviceOffersRow> DeviceOffers { get; } = [];

    public ObservableCollection<VendorLink> VendorLinks { get; } = [];

    public bool HasUpdates => UpdatesAvailable > 0;
    public bool HasDeviceOffers => DeviceOffers.Count > 0;
    public bool HasVendorLinks => VendorLinks.Count > 0;
    public int SelectedUpdateCount => DeviceOffers.Count(d => d.IsSelected && d.CanBatchInstall);
    public string UpdatesTabHeader => UpdatesAvailable == 0 ? Loc.T("Updates") : Loc.F("Updates ({0})", UpdatesAvailable);

    [RelayCommand]
    private Task CheckForUpdatesAsync() => RunUpdateCheckAsync(CancellationToken.None);

    /// <summary>The update search behind the button and demo-mode initialization; see <c>RunScanAsync</c> for the token handling.</summary>
    private Task RunUpdateCheckAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        var ct = linked.Token;
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
        var result = await _sources.SearchAsync(_allDrivers.Select(r => r.Driver).ToList(), progress, ct);
        ApplySearchResult(result);
    }, Loc.T("Searching for driver updates…"));

    private void ApplySearchResult(DriverSourcesResult result)
    {
        foreach (var row in DeviceOffers)
        {
            row.PropertyChanged -= OnDeviceRowChanged;
        }

        Updates.Clear();
        DeviceOffers.Clear();
        var wuRows = new Dictionary<string, DriverUpdateRow>(StringComparer.OrdinalIgnoreCase);
        DriverUpdateRow WindowsUpdateRow(DriverUpdateInfo update)
        {
            if (!wuRows.TryGetValue(update.Id, out var row))
            {
                row = new DriverUpdateRow(update);
                wuRows[update.Id] = row;
                Updates.Add(row);
            }

            return row;
        }

        foreach (var group in result.Devices)
        {
            var row = new DeviceOffersRow(group, WindowsUpdateRow);
            row.PropertyChanged += OnDeviceRowChanged;
            DeviceOffers.Add(row);
        }

        VendorLinks.Clear();
        foreach (var link in result.VendorLinks)
        {
            VendorLinks.Add(link);
        }

        UpdatesAvailable = DeviceOffers.Count(d => d.HasNewer);
        UpdatesChecked = true;
        OnPropertyChanged(nameof(HasDeviceOffers));
        LastCheckText = Loc.F("Checked {0}", result.Captured.ToString("t", CultureInfo.CurrentCulture));
        SourcesText = string.Join("  ·  ", result.Sources.Select(s => s.Name + ": " + s.Message));
        SetWarnings(WarningSource.UpdateCheck, result.Warnings);
        OnPropertyChanged(nameof(SelectedUpdateCount));
    }

    private void OnDeviceRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceOffersRow.IsSelected) or nameof(DeviceOffersRow.Chosen))
        {
            OnPropertyChanged(nameof(SelectedUpdateCount));
        }
    }

    [RelayCommand]
    private void SelectAllUpdates()
    {
        foreach (var d in DeviceOffers.Where(d => d.CanBatchInstall))
        {
            d.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearUpdateSelection()
    {
        foreach (var d in DeviceOffers)
        {
            d.IsSelected = false;
        }
    }

    /// <summary>The Windows Update rows of the ticked devices whose chosen source is Windows Update.</summary>
    private List<DriverUpdateRow> SelectedWindowsUpdates() =>
        DeviceOffers.Where(d => d.IsSelected && d.Chosen?.UpdateRow is not null).Select(d => d.Chosen!.UpdateRow!).Distinct().ToList();

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var selected = SelectedWindowsUpdates();
        if (selected.Count == 0)
        {
            _notifications.Warning(Loc.T("Nothing selected"), Loc.T("Tick the Windows Update drivers you want to download first."));
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = FormatProgress(p));
            var result = await _drivers.DownloadAsync(selected.Select(s => s.Update).ToList(), progress, ct);
            await MarkDownloadedAsync(selected, result.Success, ct);
            ShowResult(result.Success, result.Message, Loc.T("Download"));
        }, Loc.T("Downloading driver updates…"));
    }

    [RelayCommand]
    private Task InstallSelectedAsync()
    {
        var selected = SelectedWindowsUpdates();
        if (selected.Count == 0)
        {
            _notifications.Warning(Loc.T("Nothing selected"),
                Loc.T("Tick the devices whose chosen source is Windows Update. Vendor drivers and tools install from each device's own button."));
            return Task.CompletedTask;
        }

        return InstallWindowsUpdatesAsync(selected);
    }

    /// <summary>Confirms, downloads what is missing and installs the given Windows Update drivers.</summary>
    private async Task InstallWindowsUpdatesAsync(IReadOnlyList<DriverUpdateRow> selected)
    {
        bool rebootLikely = selected.Any(s => s.RebootRequired);
        var list = string.Join("\n", selected.Select(s => "• " + s.Title));
        var message = Loc.F("PC Checker will download (if needed) and install {0} driver update(s) from Windows Update:", selected.Count) + $"\n\n{list}\n\n"
            + Loc.T("A restore point is created first (per your settings) so you can go back if anything misbehaves. The screen may flicker while a display or audio driver installs.")
            + "\n\n" + (rebootLikely
                ? Loc.T("Windows will need a restart to finish at least one of these updates.")
                : Loc.T("Some updates may ask for a restart afterwards."));
        if (!await _dialogs.ConfirmAsync(Loc.T("Install driver updates?"), message, Loc.T("Install")))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = FormatProgress(p));
            var pending = selected.Where(s => !s.Update.IsDownloaded).ToList();
            if (pending.Count > 0)
            {
                var download = await _drivers.DownloadAsync(pending.Select(s => s.Update).ToList(), progress, ct);
                await MarkDownloadedAsync(pending, download.Success, ct);
                if (!download.Success)
                {
                    ShowResult(false, download.Message, Loc.T("Download"));
                    return;
                }
            }

            // Only what is actually on disk can be installed; anything that failed to download stays listed.
            var ready = selected.Where(s => s.Update.IsDownloaded).ToList();
            var skipped = selected.Except(ready).ToList();
            if (ready.Count == 0)
            {
                ShowResult(false, Loc.T("None of the selected updates finished downloading, so nothing was installed."), Loc.T("Install"));
                return;
            }

            var result = await _drivers.InstallAsync(ready.Select(s => s.Update).ToList(), progress, ct);
            int ok = ApplyInstallResults(result, ready);
            UpdatesAvailable = DeviceOffers.Count(d => d.HasNewer);
            RebootRequired |= result.RebootRequired;

            var text = Loc.F("{0} of {1} update(s) installed.", ok, result.Results.Count) + (result.RebootRequired ? " " + Loc.T("Restart Windows to finish.") : string.Empty);
            if (ok < result.Results.Count)
            {
                var failures = result.Results.Where(r => !r.Result.Success).Select(r => r.Update.Title + ": " + r.Result.Message);
                text += "\n" + string.Join("\n", failures);
            }

            if (skipped.Count > 0)
            {
                text += "\n" + Loc.T("Not installed (download failed):") + " " + string.Join(", ", skipped.Select(s => s.Title));
            }

            ShowResult(result.Success || ok > 0, text, Loc.T("Install"));
            if (ok > 0)
            {
                await ScanAsyncCore(null, ct);
            }
        }, Loc.T("Installing driver updates…"), cancellable: false);
    }

    [RelayCommand]
    private void OpenOptionalUpdates() => Open(DriverRules.OptionalUpdatesUri);

    [RelayCommand]
    private void DismissReboot() => RebootRequired = false;

    /// <summary>
    /// Updates each row's downloaded state after a download. A multi-update download can succeed only partly, so the
    /// service is asked per update when it can tell; otherwise the overall outcome is used.
    /// </summary>
    private async Task MarkDownloadedAsync(IReadOnlyList<DriverUpdateRow> rows, bool overallSuccess, CancellationToken ct)
    {
        IReadOnlySet<string>? downloaded = null;
        if (_drivers is IDriverUpdateStateProvider state)
        {
            try
            {
                downloaded = await state.GetDownloadedUpdateIdsAsync(rows.Select(r => r.Update.Id), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Downloaded state could not be read; using the overall download result.");
            }
        }

        foreach (var row in rows)
        {
            bool isDownloaded = downloaded?.Contains(row.Update.Id) ?? overallSuccess;
            row.Apply(row.Update with { IsDownloaded = isDownloaded });
            SetDeviceState(row, isDownloaded ? Loc.T("Downloaded") : Loc.T("Download failed"));
            if (!isDownloaded)
            {
                row.SetState(Loc.T("Download failed"));
            }
        }
    }

    /// <summary>Applies per-update install outcomes, removes the installed offers and returns the success count.</summary>
    private int ApplyInstallResults(DriverInstallResult result, IReadOnlyList<DriverUpdateRow> rows)
    {
        int ok = 0;
        foreach (var (update, outcome) in result.Results)
        {
            var row = rows.FirstOrDefault(s => s.Update.Id == update.Id);
            if (row is null)
            {
                continue;
            }

            if (outcome.Success)
            {
                ok++;
                row.Apply(row.Update with { IsInstalled = true, IsDownloaded = true });
            }
            else
            {
                row.SetState(Loc.T("Failed:") + " " + outcome.Message);
                SetDeviceState(row, Loc.T("Failed:") + " " + outcome.Message);
            }
        }

        var installed = result.Results.Where(r => r.Result.Success).Select(r => r.Update.Id).ToHashSet();
        foreach (var row in Updates.Where(u => installed.Contains(u.Update.Id)).ToList())
        {
            Updates.Remove(row);
        }

        foreach (var device in DeviceOffers.ToList())
        {
            foreach (var offer in device.Offers.Where(o => o.UpdateRow is not null && installed.Contains(o.UpdateRow.Update.Id)).ToList())
            {
                device.Remove(offer);
                device.StateText = Loc.T("Installed from Windows Update");
                device.IsSelected = false;
            }

            if (device.Offers.Count == 0)
            {
                device.PropertyChanged -= OnDeviceRowChanged;
                DeviceOffers.Remove(device);
            }
        }

        OnPropertyChanged(nameof(HasDeviceOffers));
        OnPropertyChanged(nameof(SelectedUpdateCount));
        return ok;
    }

    private void SetDeviceState(DriverUpdateRow row, string text)
    {
        foreach (var device in DeviceOffers.Where(d => d.Offers.Any(o => o.UpdateRow == row)))
        {
            device.StateText = text;
        }
    }
}
