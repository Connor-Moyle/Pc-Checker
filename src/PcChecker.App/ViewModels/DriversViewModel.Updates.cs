using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The Windows Update half of the Drivers page: checking for driver updates, selecting, downloading and installing
/// them, vendor links and the restart prompt that follows an install.
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
    private bool _updatesChecked;

    public ObservableCollection<DriverUpdateRow> Updates { get; } = [];
    public ObservableCollection<VendorLink> VendorLinks { get; } = [];

    public bool HasUpdates => UpdatesAvailable > 0;
    public bool HasVendorLinks => VendorLinks.Count > 0;
    public int SelectedUpdateCount => Updates.Count(u => u.IsSelected);
    public string UpdatesTabHeader => UpdatesAvailable == 0 ? "Updates" : $"Updates ({UpdatesAvailable})";

    [RelayCommand]
    private Task CheckForUpdatesAsync() => RunUpdateCheckAsync(CancellationToken.None);

    /// <summary>The update check behind the button and demo-mode initialization; see <c>RunScanAsync</c> for the token handling.</summary>
    private Task RunUpdateCheckAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        var ct = linked.Token;
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
        var result = await _drivers.CheckForUpdatesAsync(progress, ct);
        Updates.Clear();
        foreach (var u in result.Updates)
        {
            var row = new DriverUpdateRow(u);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DriverUpdateRow.IsSelected))
                {
                    OnPropertyChanged(nameof(SelectedUpdateCount));
                }
            };
            Updates.Add(row);
        }

        VendorLinks.Clear();
        foreach (var link in result.VendorLinks)
        {
            VendorLinks.Add(link);
        }

        UpdatesAvailable = Updates.Count;
        UpdatesChecked = true;
        LastCheckText = "Checked " + result.Captured.ToString("t", CultureInfo.CurrentCulture);
        SetWarnings(WarningSource.UpdateCheck, result.Warnings);
        OnPropertyChanged(nameof(SelectedUpdateCount));
    }, "Checking Windows Update for driver updates…");

    [RelayCommand]
    private void SelectAllUpdates()
    {
        foreach (var u in Updates)
        {
            u.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearUpdateSelection()
    {
        foreach (var u in Updates)
        {
            u.IsSelected = false;
        }
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var selected = Updates.Where(u => u.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _notifications.Warning("Nothing selected", "Tick the updates you want to download first.");
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = FormatProgress(p));
            var result = await _drivers.DownloadAsync(selected.Select(s => s.Update).ToList(), progress, ct);
            await MarkDownloadedAsync(selected, result.Success, ct);
            ShowResult(result.Success, result.Message, "Download");
        }, "Downloading driver updates…");
    }

    [RelayCommand]
    private async Task InstallSelectedAsync()
    {
        var selected = Updates.Where(u => u.IsSelected).ToList();
        if (selected.Count == 0)
        {
            _notifications.Warning("Nothing selected", "Tick the updates you want to install first.");
            return;
        }

        bool rebootLikely = selected.Any(s => s.RebootRequired);
        var list = string.Join("\n", selected.Select(s => "• " + s.Title));
        var message = $"PC Checker will download (if needed) and install {selected.Count} driver update(s) from Windows Update:\n\n{list}\n\n"
            + "A restore point is created first (per your settings) so you can go back if anything misbehaves. "
            + "The screen may flicker while a display or audio driver installs."
            + (rebootLikely ? "\n\nWindows will need a restart to finish at least one of these updates." : "\n\nSome updates may ask for a restart afterwards.");
        if (!await _dialogs.ConfirmAsync("Install driver updates?", message, "Install"))
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
                    ShowResult(false, download.Message, "Download");
                    return;
                }
            }

            // Only what is actually on disk can be installed; anything that failed to download stays listed.
            var ready = selected.Where(s => s.Update.IsDownloaded).ToList();
            var skipped = selected.Except(ready).ToList();
            if (ready.Count == 0)
            {
                ShowResult(false, "None of the selected updates finished downloading, so nothing was installed.", "Install");
                return;
            }

            var result = await _drivers.InstallAsync(ready.Select(s => s.Update).ToList(), progress, ct);
            int ok = ApplyInstallResults(result, ready);
            UpdatesAvailable = Updates.Count;
            RebootRequired |= result.RebootRequired;

            var text = $"{ok} of {result.Results.Count} update(s) installed." + (result.RebootRequired ? " Restart Windows to finish." : string.Empty);
            if (ok < result.Results.Count)
            {
                var failures = result.Results.Where(r => !r.Result.Success).Select(r => r.Update.Title + ": " + r.Result.Message);
                text += "\n" + string.Join("\n", failures);
            }

            if (skipped.Count > 0)
            {
                text += "\nNot installed (download failed): " + string.Join(", ", skipped.Select(s => s.Title));
            }

            ShowResult(result.Success || ok > 0, text, "Install");
            if (ok > 0)
            {
                await ScanAsyncCore(null, ct);
            }
        }, "Installing driver updates…", cancellable: false);
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
            if (!isDownloaded)
            {
                row.SetState("Download failed");
            }
        }
    }

    /// <summary>Applies per-update install outcomes to the rows and removes the installed ones. Returns the success count.</summary>
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
                row.IsSelected = false;
            }
            else
            {
                row.SetState("Failed: " + outcome.Message);
            }
        }

        var installed = result.Results.Where(r => r.Result.Success).Select(r => r.Update.Id).ToHashSet();
        foreach (var row in Updates.Where(u => installed.Contains(u.Update.Id)).ToList())
        {
            Updates.Remove(row);
        }

        return ok;
    }
}
