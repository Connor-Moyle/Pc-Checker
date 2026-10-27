using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Accessibility;
using PcChecker.Core.Common;
using PcChecker.Core.Lighting;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>
/// The Lighting page's hardware detection: one combined list of every RGB device (controllable through OpenRGB or
/// only found on the PC), the vendor RGB apps that hold devices, and the optional fixes (close an app for this
/// session, install or start OpenRGB).
/// </summary>
public sealed partial class LightingViewModel
{
    private readonly IRgbHardwareService _hardware;
    private RgbScanResult _scan = RgbScanResult.Empty;
    private IReadOnlyList<RgbDevice> _lastDevices = [];
    private bool _scanned;

    [ObservableProperty]
    private string _deviceSummary = Loc.T("Looking for RGB hardware…");

    [ObservableProperty]
    private bool _canInstallOpenRgb;

    [ObservableProperty]
    private string? _scanProblems;

    /// <summary>Every RGB device: OpenRGB's (controllable) first, then the ones only the detection pass found.</summary>
    public ObservableCollection<RgbDeviceRow> AllDevices { get; } = [];

    /// <summary>Vendor RGB apps running now.</summary>
    public ObservableCollection<RgbConflictRow> Conflicts { get; } = [];

    public bool HasAllDevices => AllDevices.Count > 0;
    public bool HasConflicts => Conflicts.Count > 0;
    public bool HasScanProblems => !string.IsNullOrWhiteSpace(ScanProblems);

    /// <summary>The OpenRGB start button shows while OpenRGB is installed and not connected.</summary>
    public bool ShowStartOpenRgb => !IsConnected && IsOpenRgbInstalled;

    /// <summary>The install button shows while OpenRGB is not installed and winget is available.</summary>
    public bool ShowInstallOpenRgb => !IsConnected && !IsOpenRgbInstalled && CanInstallOpenRgb;

    public string KnownDevicesText => Loc.F("PC Checker recognises {0} USB lighting controllers and {1} graphics cards by their IDs (the list OpenRGB supports).",
        RgbKnownDevices.UsbCount, RgbKnownDevices.GpuCount);

    /// <summary>Detection pass plus a fresh OpenRGB device list. Read-only; F5 runs it too.</summary>
    [RelayCommand]
    private Task RescanAsync() => RunBusyAsync(async ct =>
    {
        if (!_lighting.IsConnected && Settings.Current.Lighting.AutoConnect)
        {
            await _lighting.ConnectAsync(ct);
        }

        PullStatus();
        await LoadDevicesAsync(ct);
        await ScanHardwareAsync(ct);
    }, Loc.T("Looking for RGB hardware…"));

    [RelayCommand]
    private async Task CloseConflictAsync(RgbConflictRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Loc.F("Close {0} for this session?", row.Product), RgbConflictRules.CloseConfirmation(row.Conflict), Loc.T("Close it")))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _hardware.CloseForSessionAsync(row.Conflict, ct);
            Report(result);
            if (result.Success)
            {
                _notifications.Success(Loc.T("Lighting"), result.Message);
                await ScanHardwareAsync(ct);
            }
        }, Loc.F("Closing {0}…", row.Product));
    }

    [RelayCommand]
    private async Task InstallOpenRgbAsync()
    {
        if (!await _dialogs.ConfirmAsync(Loc.T("Install OpenRGB?"),
                Loc.T("OpenRGB is a free, open-source program that controls RGB lighting from many brands. PC Checker uses it to set colours and effects.")
                + "\n\n" + Loc.F("It is installed for all users with the Windows Package Manager (winget install {0}), into Program Files.", OpenRgbInstallRules.WingetPackageId)
                + "\n\n" + Loc.T("The install is recorded in the Action Journal; remove OpenRGB any time from Settings → Apps → Installed apps."),
                Loc.T("Install OpenRGB")))
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage);
            var result = await _hardware.InstallOpenRgbAsync(progress, ct);
            Report(result);
            if (result.Success)
            {
                _notifications.Success(Loc.T("OpenRGB installed"), result.Message);
                // A connection attempt looks for the new OpenRGB.exe, so the page then offers Start OpenRGB.
                await _lighting.ConnectAsync(ct);
            }

            PullStatus();
            RebuildAllDevices();
        }, Loc.T("Installing OpenRGB…"), cancellable: false);
    }

    /// <summary>Runs the detection pass and rebuilds the combined list. Called from busy work (initial load, Rescan).</summary>
    private async Task ScanHardwareAsync(CancellationToken ct)
    {
        try
        {
            _scan = await _hardware.ScanAsync(ct);
            _scanned = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RGB hardware scan failed.");
            _scan = RgbScanResult.Empty with { Problems = [ex.Message] };
            _scanned = true;
        }

        bool canInstall = false;
        try
        {
            canInstall = !_lighting.IsOpenRgbInstalled && await _hardware.CanInstallOpenRgbAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "winget check failed.");
        }

        await DispatcherHelper.RunOnUiAsync(() =>
        {
            CanInstallOpenRgb = canInstall;
            Conflicts.Clear();
            foreach (var conflict in _scan.Conflicts)
            {
                Conflicts.Add(new RgbConflictRow(conflict));
            }

            ScanProblems = _scan.Problems.Count == 0 ? null : string.Join(" ", _scan.Problems);
            OnPropertyChanged(nameof(HasConflicts));
            OnPropertyChanged(nameof(HasScanProblems));
            RebuildAllDevices();
        });
    }

    /// <summary>Merges the OpenRGB device list with the detection pass into the combined list. UI thread.</summary>
    private void RebuildAllDevices()
    {
        var entries = RgbDeviceListBuilder.Build(_lastDevices, _scan, _lighting.Availability);
        AllDevices.Clear();
        foreach (var entry in entries)
        {
            AllDevices.Add(new RgbDeviceRow(entry));
        }

        DeviceSummary = _scanned || entries.Count > 0 ? RgbDeviceListBuilder.Summary(entries) : Loc.T("Looking for RGB hardware…");
        OnPropertyChanged(nameof(HasAllDevices));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowStartOpenRgb));
        OnPropertyChanged(nameof(ShowInstallOpenRgb));
    }

    partial void OnCanInstallOpenRgbChanged(bool value) => OnPropertyChanged(nameof(ShowInstallOpenRgb));

    partial void OnScanProblemsChanged(string? value) => OnPropertyChanged(nameof(HasScanProblems));
}

/// <summary>One row of the combined device list.</summary>
public sealed class RgbDeviceRow
{
    public RgbDeviceRow(RgbDeviceEntry entry)
    {
        Entry = entry;
    }

    public RgbDeviceEntry Entry { get; }
    public string Name => Entry.Name;
    public string Status => Entry.Status;
    public string Reason => Entry.Reason;
    public string Subtitle => string.Join(" · ", new[] { Entry.Vendor == "Unknown" ? null : Entry.Vendor, Entry.Type, Entry.Detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public bool IsControllable => Entry.IsControllable;

    /// <summary>Screen-reader text for the row: name, then status.</summary>
    public string AccessibleName => $"{Entry.Name}: {Entry.Status}";

    public SymbolRegular Icon => LightingDeviceViewModel.IconFor(Entry.Type);

    /// <summary>Badge colour: green ready, blue needs setup, yellow needs OpenRGB, orange held by another app.</summary>
    public Brush StatusBrush => ThemeColors.Accent(Entry.State switch
    {
        RgbDeviceState.Controllable => AccentRole.Green,
        RgbDeviceState.NeedsSetup => AccentRole.Blue,
        RgbDeviceState.Blocked => AccentRole.Orange,
        RgbDeviceState.Unreadable => AccentRole.Grey,
        _ => Entry.Confidence == RgbFindingConfidence.Possible ? AccentRole.Grey : AccentRole.Yellow,
    });
}

/// <summary>A running vendor RGB app, with its close action.</summary>
public sealed class RgbConflictRow
{
    public RgbConflictRow(RgbConflict conflict)
    {
        Conflict = conflict;
    }

    public RgbConflict Conflict { get; }
    public string Product => Conflict.App.Product;
    public string Why => Conflict.App.Why;
    public string Running => string.Join(", ", Conflict.Processes.Select(p => p.Name + ".exe").Concat(Conflict.Services.Select(s => s + " (service)")).Distinct());
    public bool CanClose => Conflict.CanClose;
    public string CloseLabel => Loc.F("Close {0} for this session", Product);
}
