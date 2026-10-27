using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Storage;

namespace PcChecker.App.ViewModels;

/// <summary>Surface scan part of the Storage page.</summary>
public sealed partial class StorageViewModel
{
    private CancellationTokenSource? _scanCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyToolRunning))]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private ScanMode _selectedScanMode = ScanMode.Quick;

    [ObservableProperty]
    private int _selectedBlockSizeMb = SurfaceScanOptions.DefaultBlockSizeMb;

    [ObservableProperty]
    private string _scanEstimateText = string.Empty;

    [ObservableProperty]
    private double _scanPercent;

    [ObservableProperty]
    private string _scanSpeedText = "—";

    [ObservableProperty]
    private long _scanBadBlocks;

    [ObservableProperty]
    private long _scanSlowBlocks;

    [ObservableProperty]
    private string _scanElapsedText = "—";

    [ObservableProperty]
    private string _scanEtaText = "—";

    [ObservableProperty]
    private string _scanBlocksText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanMap))]
    private IReadOnlyList<BlockState>? _scanMap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanResult))]
    [NotifyPropertyChangedFor(nameof(ScanResultIsBad))]
    [NotifyPropertyChangedFor(nameof(ScanResultIsGood))]
    private SurfaceScanResult? _scanResult;

    [ObservableProperty]
    private string? _scanSummary;

    [ObservableProperty]
    private IReadOnlyList<string> _scanGuidance = [];

    [ObservableProperty]
    private string _scanStatsText = string.Empty;

    public IReadOnlyList<ScanMode> ScanModes { get; } = Enum.GetValues<ScanMode>();

    public IReadOnlyList<int> BlockSizes { get; } = [1, 4, 8, 16, 32, 64];

    public bool HasScanMap => ScanMap is { Count: > 0 };

    public bool HasScanResult => ScanResult is not null;

    public bool ScanResultIsBad => ScanResult is { BadBlocks: > 0 };

    public bool ScanResultIsGood => ScanResult is { BadBlocks: 0 };

    public string ScanModeHelp => SelectedScanMode == ScanMode.Quick
        ? "Quick: reads the first and last 2 % of the drive plus 200 random samples. Minutes, not hours; catches most failing drives."
        : "Full: reads every block from start to end. Thorough, but a 4 TB hard disk takes several hours. The drive stays usable meanwhile.";

    partial void OnSelectedScanModeChanged(ScanMode value)
    {
        OnPropertyChanged(nameof(ScanModeHelp));
        UpdateScanEstimate();
    }

    partial void OnSelectedBlockSizeMbChanged(int value) => UpdateScanEstimate();

    private bool CanStartScan() => SelectedDevice is { CanUseTools: true } && !IsAnyToolRunning && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScan()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        if (SelectedScanMode == ScanMode.Full)
        {
            var estimate = EstimateScan(device, SelectedScanMode);
            bool ok = await _dialogs.ConfirmAsync("Run a full surface scan?",
                $"PC Checker will read every block of {device.Model} ({device.SizeText}). Nothing is written to the drive.\n\n" +
                $"Estimated time: about {SurfaceScanMath.FormatDuration(estimate)}. You can keep using the PC, but the drive will be slower, and you can cancel at any time.",
                "Start full scan");
            if (!ok)
            {
                return;
            }
        }

        await RunScanCoreAsync(device, CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan()
    {
        try
        {
            _scanCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunScanCoreAsync(StorageDeviceItem device, CancellationToken outerToken)
    {
        if (IsScanning)
        {
            return;
        }

        _scanCts?.Dispose();
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        var token = _scanCts.Token;
        var options = new SurfaceScanOptions(SelectedScanMode, SelectedBlockSizeMb);
        var stopwatch = Stopwatch.StartNew();

        ClearScanResult();
        ErrorMessage = null;
        IsScanning = true;
        RaiseToolStateChanged();
        // A fixed number of Untested cells; never one state per block (a 16 TB drive at 1 MB blocks would be 16M entries).
        ScanMap = BlockMapBuilder.CreateEmptyCells(ScanPlan.BlockCount(device.Device.SizeBytes, options.BlockSizeBytes));
        _ = TickElapsedAsync(stopwatch);

        var progress = new Progress<SurfaceScanProgress>(p =>
        {
            ScanPercent = p.Percent;
            ScanSpeedText = p.CurrentMBps > 0 ? $"{p.CurrentMBps:0} MB/s" : "—";
            ScanBadBlocks = p.BadBlocks;
            ScanSlowBlocks = p.SlowBlocks;
            ScanBlocksText = $"{p.BlocksDone:N0} / {p.BlocksTotal:N0}";
            ScanMap = p.Map;
            var eta = SurfaceScanMath.Eta(p.BlocksDone, p.BlocksTotal, stopwatch.Elapsed);
            ScanEtaText = eta is null ? "—" : SurfaceScanMath.FormatDuration(eta.Value);
        });

        try
        {
            _logger.LogInformation("Starting {Mode} surface scan of disk {Number}.", options.Mode, device.Number);
            SurfaceScanResult result = _storage is ISurfaceScanner scanner
                ? await scanner.SurfaceScanAsync(device.Number, options, progress, token)
                : await _storage.SurfaceScanAsync(device.Number, options.EffectiveBlockSizeMb, progress, token);

            ApplyScanResult(device, result);
        }
        catch (OperationCanceledException)
        {
            ScanSummary = "Scan cancelled. Nothing was changed on the drive.";
            Notify(() => _notifications.Info("Surface scan", "The scan was cancelled."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Surface scan failed.");
            ErrorMessage = "The surface scan could not run: " + ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            IsScanning = false;
            RaiseToolStateChanged();
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    private void ApplyScanResult(StorageDeviceItem device, SurfaceScanResult result)
    {
        ScanResult = result;
        ScanSummary = result.Summary;
        ScanGuidance = SurfaceScanMath.Guidance(result.BadBlocks, result.SlowBlocks, device.IsSystemDisk);
        ScanBadBlocks = result.BadBlocks;
        ScanSlowBlocks = result.SlowBlocks;
        if (result.Map.Count > 0)
        {
            ScanMap = BlockMapBuilder.Downsample(result.Map, 2000);
        }

        ScanPercent = result.Completed ? 100 : ScanPercent;
        ScanElapsedText = SurfaceScanMath.FormatDuration(result.Elapsed);
        ScanEtaText = result.Completed ? "Done" : "Stopped";
        ScanStatsText = result.BytesScanned > 0
            ? $"{ByteSize.Format(result.BytesScanned)} read · average {result.AvgMBps:0} MB/s · slowest block {result.MinMBps:0} MB/s · {SurfaceScanMath.FormatDuration(result.Elapsed)}"
            : "Nothing could be read from the drive.";

        if (result.BadBlocks > 0)
        {
            Notify(() => _notifications.Warning("Surface scan", $"{result.BadBlocks} unreadable block(s) found on {device.Model}. Back up this drive."));
        }
        else if (result.Completed)
        {
            Notify(() => _notifications.Success("Surface scan", $"{device.Model}: no unreadable blocks."));
        }
    }

    private void ClearScanResult()
    {
        ScanResult = null;
        ScanSummary = null;
        ScanGuidance = [];
        ScanStatsText = string.Empty;
        ScanPercent = 0;
        ScanSpeedText = "—";
        ScanBadBlocks = 0;
        ScanSlowBlocks = 0;
        ScanElapsedText = "—";
        ScanEtaText = "—";
        ScanBlocksText = "—";
        ScanMap = null;
    }

    private async Task TickElapsedAsync(Stopwatch stopwatch)
    {
        while (IsScanning)
        {
            ScanElapsedText = SurfaceScanMath.FormatDuration(stopwatch.Elapsed);
            await Task.Delay(1000);
        }
    }

    private void UpdateScanEstimate()
    {
        var device = SelectedDevice;
        if (device is null)
        {
            ScanEstimateText = "Select a drive to see how long a scan would take.";
            return;
        }

        if (!device.CanUseTools)
        {
            ScanEstimateText = "Windows hides drives inside a storage pool, so they cannot be scanned on their own. Scan the storage space instead.";
            return;
        }

        var estimate = EstimateScan(device, SelectedScanMode);
        string time = estimate < TimeSpan.FromMinutes(1) ? "under a minute" : "about " + SurfaceScanMath.FormatDuration(estimate);
        ScanEstimateText = SelectedScanMode == ScanMode.Quick
            ? $"Quick scan of {device.Model}: {time}."
            : $"Full scan of {device.Model} ({device.SizeText}): {time} at typical speed.";
    }

    private TimeSpan EstimateScan(StorageDeviceItem device, ScanMode mode)
    {
        var options = new SurfaceScanOptions(mode, SelectedBlockSizeMb);
        long totalBlocks = ScanPlan.BlockCount(device.Device.SizeBytes, options.BlockSizeBytes);
        long planned = ScanPlan.PlannedCount(totalBlocks, options);
        return ScanPlan.Estimate(planned, options.BlockSizeBytes, ScanPlan.TypicalMBps(device.MediaType, device.BusType));
    }
}
