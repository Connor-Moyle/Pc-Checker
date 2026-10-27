using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers;
using PcChecker.Core.Drivers.Platform;
using PcChecker.Core.Drivers.Sources;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Drivers page: installed driver inventory with search and filters, driver backup, .inf install, rollback and the
/// advanced driver-store listing. The update search across sources and the Windows Update download and install live
/// in <c>DriversViewModel.Updates.cs</c>; vendor packages, PC maker tools and the source options in
/// <c>DriversViewModel.Sources.cs</c>. Every change confirms first and runs guarded.
/// </summary>
public sealed partial class DriversViewModel : ViewModelBase
{
    private readonly IDriverService _drivers;
    private readonly IDriverSourceService _sources;
    private readonly IPcPlatformService _pcPlatform;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<DriversViewModel> _logger;
    private readonly List<DriverRow> _allDrivers = [];
    private readonly Dictionary<WarningSource, IReadOnlyList<string>> _warningsBySource = [];

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
    private bool _showDisconnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisconnected))]
    [NotifyPropertyChangedFor(nameof(DisconnectedText))]
    private int _disconnectedCount;

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
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warnings;

    [ObservableProperty]
    private string? _lastScanText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private string? _lastResultMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuccessResult))]
    [NotifyPropertyChangedFor(nameof(ShowFailureResult))]
    private bool _lastResultSuccess;

    [ObservableProperty]
    private bool _driverStoreLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledTabHeader))]
    private int _visibleDriverCount;

    public DriversViewModel(ISettingsService settings, IDriverService drivers, IDriverSourceService sources, IDialogService dialogs,
        INotificationService notifications, INavigationService navigation, ILogger<DriversViewModel> logger, IPcPlatformService pcPlatform)
        : base(settings)
    {
        _drivers = drivers;
        _sources = sources;
        _pcPlatform = pcPlatform;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
        DeviceOffers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedUpdateCount));
            OnPropertyChanged(nameof(HasDeviceOffers));
        };
        VendorLinks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVendorLinks));
        LoadSourceSettings();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsAdvancedMode))
            {
                OnAdvancedModeChanged();
            }
        };
    }

    public ObservableCollection<DriverRow> FilteredDrivers { get; } = [];
    public ObservableCollection<DriverPackageRow> DriverStore { get; } = [];

    public bool HasDrivers => TotalDrivers > 0;
    public bool HasDisconnected => DisconnectedCount > 0;
    public string DisconnectedText => DisconnectedCount == 1 ? "Show 1 disconnected device" : $"Show {DisconnectedCount} disconnected devices";
    public bool ShowSuccessResult => LastResultSuccess && !string.IsNullOrWhiteSpace(LastResultMessage);
    public bool ShowFailureResult => !LastResultSuccess && !string.IsNullOrWhiteSpace(LastResultMessage);
    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);
    public bool SupportsDriverStore => _drivers is IDriverStoreProvider;
    public string InstalledTabHeader => $"Installed drivers ({VisibleDriverCount})";

    /// <summary>
    /// Runs the installed-driver scan. The Windows Update search is an online call that can take a minute or more,
    /// so in normal mode it waits for the user; demo mode runs it too so the Updates tab is populated.
    /// </summary>
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RunScanAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // The "Your PC" card reads the board, BIOS and chipset from this PC (and saved maker answers) without going online.
        await LoadPlatformAsync(online: false, force: false, cancellationToken);
        if (AppOptions.Current.IsDemo)
        {
            await RunUpdateCheckAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunScanAsync(cancellationToken);

    partial void OnSearchTextChanged(string? value) => ApplyFilters();
    partial void OnThirdPartyOnlyChanged(bool value) => ApplyFilters();
    partial void OnProblemsOnlyChanged(bool value) => ApplyFilters();
    partial void OnOldOnlyChanged(bool value) => ApplyFilters();
    partial void OnUnsignedOnlyChanged(bool value) => ApplyFilters();

    partial void OnShowDisconnectedChanged(bool value)
    {
        UpdateSummary();
        ApplyFilters();
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Drivers page operation failed.");

    [RelayCommand]
    private Task ScanAsync() => RunScanAsync(CancellationToken.None);

    /// <summary>
    /// The scan behind the Scan button, Refresh and page initialization. The busy overlay's Cancel button and the
    /// caller's token (navigation away, screenshot runner) both stop the work.
    /// </summary>
    private Task RunScanAsync(CancellationToken outer) => RunBusyAsync(async busyToken =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(busyToken, outer);
        var ct = linked.Token;
        var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage + (p.Detail is null ? string.Empty : " — " + p.Detail));
        await ScanAsyncCore(progress, ct);
        if (IsAdvancedMode && SupportsDriverStore)
        {
            await ReadDriverStoreAsync(ct);
        }
    }, "Scanning installed drivers…");

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
        var path = DriverFilePicker.PickInf();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

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
        }, "Installing driver package…", cancellable: false);
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
            // Nothing to remove from here: offer the Device Manager route instead of a dead-end message.
            var open = await _dialogs.ConfirmAsync("Use Device Manager for this one",
                $"{row.DeviceName} uses a driver that ships with Windows, so there is no third-party package to remove.\n\n"
                + "Device Manager can still roll back to the previous version: right-click the device → Properties → Driver → Roll Back Driver.",
                "Open Device Manager");
            if (open)
            {
                OpenDeviceManager();
            }

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
        }, "Removing driver package…", cancellable: false);
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
    private void OpenDeviceManager() => Open(DriverRules.DeviceManagerCommand);

    /// <summary>Opens the Old drivers tab, which finds superseded and unused driver packages.</summary>
    [RelayCommand]
    private void OpenOldDrivers() => _navigation.Navigate(typeof(DriverCleanupPage));

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            Open(url);
        }
    }

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
        UpdateSummary();
        LastScanText = "Scanned " + result.Captured.ToString("t", CultureInfo.CurrentCulture);
        SetWarnings(WarningSource.Scan, result.Warnings);
        ApplyFilters();
    }

    private void UpdateSummary()
    {
        var summary = DriverInfoBuilder.Summarize(_allDrivers.Select(r => r.Driver), includeDisconnected: ShowDisconnected);
        TotalDrivers = summary.Total;
        ThirdPartyCount = summary.ThirdParty;
        ProblemCount = summary.WithProblems;
        OldCount = summary.Old;
        UnsignedCount = summary.Unsigned;
        DisconnectedCount = summary.Disconnected;
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
        SetWarnings(WarningSource.DriverStore, store.Warnings);
    }

    /// <summary>Turning advanced mode on after a scan reveals the driver-store section; fill it so it is not empty.</summary>
    private void OnAdvancedModeChanged()
    {
        if (IsAdvancedMode && HasDrivers && SupportsDriverStore && !DriverStoreLoaded && !IsBusy)
        {
            _ = LoadDriverStoreAsync();
        }
    }

    private void ApplyFilters()
    {
        FilteredDrivers.Clear();
        foreach (var row in _allDrivers)
        {
            if (!ShowDisconnected && !row.IsPresent) { continue; }
            if (ThirdPartyOnly && !row.IsThirdParty) { continue; }
            if (ProblemsOnly && !row.HasProblem) { continue; }
            if (OldOnly && !row.IsOld) { continue; }
            if (UnsignedOnly && !row.IsUnsigned) { continue; }
            if (!row.Matches(SearchText)) { continue; }
            FilteredDrivers.Add(row);
        }

        VisibleDriverCount = FilteredDrivers.Count;
    }

    /// <summary>
    /// Replaces the warnings of one source (scan, update check, driver store) and rebuilds the InfoBar text, so a
    /// warning from an earlier failed run disappears once the same operation succeeds, while warnings from the other
    /// sources stay visible.
    /// </summary>
    private void SetWarnings(WarningSource source, IReadOnlyList<string> warnings)
    {
        _warningsBySource[source] = warnings;
        var all = _warningsBySource.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value).Where(w => !string.IsNullOrWhiteSpace(w)).Distinct().ToList();
        Warnings = all.Count == 0 ? null : string.Join("\n", all);
    }

    private enum WarningSource
    {
        Scan,
        UpdateCheck,
        DriverStore,
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
