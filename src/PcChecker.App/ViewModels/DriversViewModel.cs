using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Drivers page: installed driver inventory with search and filters, Windows Update driver updates with download and
/// install, driver backup, .inf install, rollback and vendor links. Every change confirms first and runs guarded.
/// </summary>
public sealed partial class DriversViewModel : ViewModelBase
{
    private readonly IDriverService _drivers;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<DriversViewModel> _logger;
    private readonly List<DriverRow> _allDrivers = [];

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private bool _thirdPartyOnly;

    [ObservableProperty]
    private bool _problemsOnly;

    [ObservableProperty]
    private bool _oldOnly;

    [ObservableProperty]
    private bool _unsignedOnly;

    [ObservableProperty]
    private DriverRow? _selectedDriver;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrivers))]
    private int _totalDrivers;

    [ObservableProperty]
    private int _thirdPartyCount;

    [ObservableProperty]
    private int _problemCount;

    [ObservableProperty]
    private int _oldCount;

    [ObservableProperty]
    private int _unsignedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdates))]
    [NotifyPropertyChangedFor(nameof(UpdatesTabHeader))]
    private int _updatesAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warnings;

    [ObservableProperty]
    private string? _lastScanText;

    [ObservableProperty]
    private string? _lastCheckText;

    [ObservableProperty]
    private bool _rebootRequired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private string? _lastResultMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private bool _lastResultSuccess;

    [ObservableProperty]
    private bool _updatesChecked;

    [ObservableProperty]
    private bool _driverStoreLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledTabHeader))]
    private int _visibleDriverCount;

    public DriversViewModel(ISettingsService settings, IDriverService drivers, IDialogService dialogs, INotificationService notifications, ILogger<DriversViewModel> logger)
        : base(settings)
    {
        _drivers = drivers;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        Updates.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedUpdateCount));
        VendorLinks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVendorLinks));
    }

    public ObservableCollection<DriverRow> FilteredDrivers { get; } = [];
    public ObservableCollection<DriverUpdateRow> Updates { get; } = [];
    public ObservableCollection<VendorLink> VendorLinks { get; } = [];
    public ObservableCollection<DriverPackageRow> DriverStore { get; } = [];

    public bool HasUpdates => UpdatesAvailable > 0;
    public bool HasDrivers => TotalDrivers > 0;
    public bool HasVendorLinks => VendorLinks.Count > 0;
    public bool ShowSuccessResult => LastResultSuccess && !string.IsNullOrWhiteSpace(LastResultMessage);
    public bool ShowFailureResult => !LastResultSuccess && !string.IsNullOrWhiteSpace(LastResultMessage);
    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);
    public bool SupportsDriverStore => _drivers is IDriverStoreProvider;
    public int SelectedUpdateCount => Updates.Count(u => u.IsSelected);
    public string UpdatesTabHeader => UpdatesAvailable == 0 ? "Updates" : $"Updates ({UpdatesAvailable})";
    public string InstalledTabHeader => $"Installed drivers ({VisibleDriverCount})";

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await ScanAsync();
        await CheckForUpdatesAsync();
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => ScanAsync();

    partial void OnSearchTextChanged(string? value) => ApplyFilters();
    partial void OnThirdPartyOnlyChanged(bool value) => ApplyFilters();
    partial void OnProblemsOnlyChanged(bool value) => ApplyFilters();
    partial void OnOldOnlyChanged(bool value) => ApplyFilters();
    partial void OnUnsignedOnlyChanged(bool value) => ApplyFilters();

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Drivers page operation failed.");

    [RelayCommand]
    private Task ScanAsync() => RunBusyAsync(async ct =>
    {
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
        await ScanAsyncCore(progress, ct);
        if (IsAdvancedMode && SupportsDriverStore)
        {
            await ReadDriverStoreAsync(ct);
        }
    }, "Scanning installed drivers…");

    [RelayCommand]
    private Task CheckForUpdatesAsync() => RunBusyAsync(async ct =>
    {
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
        MergeWarnings(result.Warnings);
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
            foreach (var row in selected)
            {
                row.Apply(row.Update with { IsDownloaded = result.Success });
            }

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
            var toInstall = selected.Select(s => s.Update).ToList();
            var pending = toInstall.Where(u => !u.IsDownloaded).ToList();
            if (pending.Count > 0)
            {
                var download = await _drivers.DownloadAsync(pending, progress, ct);
                if (!download.Success)
                {
                    ShowResult(false, download.Message, "Download");
                    return;
                }
            }

            var result = await _drivers.InstallAsync(toInstall, progress, ct);
            int ok = 0;
            foreach (var (update, outcome) in result.Results)
            {
                var row = selected.FirstOrDefault(s => s.Update.Id == update.Id);
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

            UpdatesAvailable = Updates.Count;
            RebootRequired |= result.RebootRequired;
            var text = $"{ok} of {result.Results.Count} update(s) installed." + (result.RebootRequired ? " Restart Windows to finish." : string.Empty);
            if (ok < result.Results.Count)
            {
                var failures = result.Results.Where(r => !r.Result.Success).Select(r => r.Update.Title + ": " + r.Result.Message);
                text += "\n" + string.Join("\n", failures);
            }

            ShowResult(result.Success || ok > 0, text, "Install");
            if (ok > 0)
            {
                await ScanAsyncCore(null, ct);
            }
        }, "Installing driver updates…");
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose a folder for the driver backup");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var target = Path.Combine(folder, "PcChecker-Drivers-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture));
        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = FormatProgress(p));
            var result = await _drivers.BackupDriversAsync(target, progress, ct);
            ShowResult(result.Success, result.Message, "Driver backup");
        }, "Backing up drivers…");
    }

    [RelayCommand]
    private async Task InstallInfAsync()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a driver package (.inf)",
            Filter = "Driver setup information (*.inf)|*.inf",
            CheckFileExists = true,
        };
        if (picker.ShowDialog() != true)
        {
            return;
        }

        var path = picker.FileName;
        var message = $"PC Checker will add {Path.GetFileName(path)} to the Windows driver store and install it on any matching device.\n\n"
            + "Only install packages from sources you trust. A restore point is created first (per your settings).";
        if (!await _dialogs.ConfirmAsync("Install this driver package?", message, "Install"))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _drivers.InstallInfAsync(path, ct);
            RebootRequired |= result.RebootRequired;
            ShowResult(result.Success, result.Message, "Install .inf");
            if (result.Success)
            {
                await ScanAsyncCore(null, ct);
            }
        }, "Installing driver package…");
    }

    [RelayCommand]
    private async Task RollbackAsync(DriverRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!row.CanRollBack)
        {
            await _dialogs.ShowMessageAsync("Use Device Manager for this one",
                $"{row.DeviceName} uses a driver that ships with Windows, so there is no third-party package to remove.\n\n"
                + "Open Device Manager → right-click the device → Properties → Driver → Roll Back Driver.");
            return;
        }

        var message = $"PC Checker will remove the third-party driver package {row.Driver.InfName} used by {row.DeviceName}. "
            + "Windows will fall back to its own driver for the device, or look for one on Windows Update.\n\n"
            + "This cannot be undone from here; keep a driver backup first. A restore point is created per your settings.";
        if (!await _dialogs.ConfirmAsync("Remove this driver package?", message, "Remove", isDanger: true))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _drivers.RollbackAsync(row.Driver.DeviceId, ct);
            RebootRequired |= result.RebootRequired;
            ShowResult(result.Success, result.Message, "Roll back");
            if (result.Success)
            {
                await ScanAsyncCore(null, ct);
            }
        }, "Removing driver package…");
    }

    [RelayCommand]
    private Task LoadDriverStoreAsync() => RunBusyAsync(ReadDriverStoreAsync, "Reading the driver store…");

    [RelayCommand]
    private async Task ExportListAsync()
    {
        if (_allDrivers.Count == 0)
        {
            _notifications.Warning("Nothing to export", "Scan drivers first.");
            return;
        }

        var path = await _dialogs.SaveFileAsync("Export driver list", "drivers.csv", "CSV file|*.csv");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, DriverListExport.BuildCsv(_allDrivers.Select(r => r.Driver)), System.Text.Encoding.UTF8);
            _notifications.Success("Exported", $"{_allDrivers.Count} drivers written to {path}.");
        }
        catch (Exception ex)
        {
            ErrorMessage = "The list could not be exported: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenOptionalUpdates() => Open(DriverRules.OptionalUpdatesUri);

    [RelayCommand]
    private void OpenDeviceManager() => Open(DriverRules.DeviceManagerCommand);

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            Open(url);
        }
    }

    [RelayCommand]
    private async Task RestartNowAsync()
    {
        if (!await _dialogs.ConfirmAsync("Restart Windows?", "Windows will restart in 60 seconds so the new drivers can finish installing. Save your work first.\n\nYou can cancel the countdown with 'shutdown /a' from a command prompt.", "Restart", isDanger: true))
        {
            return;
        }

        var result = ShellLauncher.Open("shutdown.exe", "/r /t 60 /c \"PC Checker: restarting to finish driver installation.\"");
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }

    [RelayCommand]
    private void DismissReboot() => RebootRequired = false;

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = null;
        ThirdPartyOnly = false;
        ProblemsOnly = false;
        OldOnly = false;
        UnsignedOnly = false;
    }

    [RelayCommand]
    private void DismissResult() => LastResultMessage = null;

    private async Task ScanAsyncCore(IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        var result = await _drivers.ScanInstalledAsync(progress, ct);
        _allDrivers.Clear();
        _allDrivers.AddRange(result.Drivers.Select(d => new DriverRow(d)));
        var summary = DriverInfoBuilder.Summarize(result.Drivers);
        TotalDrivers = summary.Total;
        ThirdPartyCount = summary.ThirdParty;
        ProblemCount = summary.WithProblems;
        OldCount = summary.Old;
        UnsignedCount = summary.Unsigned;
        LastScanText = "Scanned " + result.Captured.ToString("t", CultureInfo.CurrentCulture);
        MergeWarnings(result.Warnings);
        ApplyFilters();
    }

    private async Task ReadDriverStoreAsync(CancellationToken ct)
    {
        if (_drivers is not IDriverStoreProvider provider)
        {
            return;
        }

        var store = await provider.GetDriverStoreAsync(ct);
        DriverStore.Clear();
        foreach (var p in store.Packages)
        {
            DriverStore.Add(new DriverPackageRow(p));
        }

        DriverStoreLoaded = true;
        MergeWarnings(store.Warnings);
    }

    private void ApplyFilters()
    {
        FilteredDrivers.Clear();
        foreach (var row in _allDrivers)
        {
            if (ThirdPartyOnly && !row.IsThirdParty) { continue; }
            if (ProblemsOnly && !row.HasProblem) { continue; }
            if (OldOnly && !row.IsOld) { continue; }
            if (UnsignedOnly && !row.IsUnsigned) { continue; }
            if (!row.Matches(SearchText)) { continue; }
            FilteredDrivers.Add(row);
        }

        VisibleDriverCount = FilteredDrivers.Count;
    }

    private void MergeWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        var existing = string.IsNullOrWhiteSpace(Warnings) ? Array.Empty<string>() : Warnings.Split('\n');
        Warnings = string.Join("\n", existing.Concat(warnings).Distinct());
    }

    private void ShowResult(bool success, string message, string title)
    {
        LastResultSuccess = success;
        LastResultMessage = message;
        if (success)
        {
            _notifications.Success(title, message.Split('\n')[0]);
        }
        else
        {
            _notifications.Error(title + " failed", message.Split('\n')[0]);
        }
    }

    private void Open(string target)
    {
        var result = ShellLauncher.Open(target);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }

    private static string FormatProgress(ProgressUpdate p)
    {
        var text = p.Stage;
        if (p.Percent is { } pct)
        {
            text += $" ({pct:0}%)";
        }

        return p.Detail is null ? text : text + " — " + p.Detail;
    }
}
