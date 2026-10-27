using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Converters;
using PcChecker.Core.Common;
using PcChecker.Core.Network;

namespace PcChecker.App.ViewModels;

/// <summary>Speed test and packet loss test: each runs inline in its card with live progress and its own Cancel.</summary>
public sealed partial class NetworkViewModel
{
    private CancellationTokenSource? _testCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTesting))]
    [NotifyCanExecuteChangedFor(nameof(CancelTestCommand))]
    private bool _isSpeedTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTesting))]
    [NotifyCanExecuteChangedFor(nameof(CancelTestCommand))]
    private bool _isPacketTesting;

    [ObservableProperty] private string? _speedPhase;
    [ObservableProperty] private string? _speedLive;
    [ObservableProperty] private double _speedProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpeedResult), nameof(ShowSpeedEmpty))]
    private SpeedTestResult? _speedResult;

    [ObservableProperty] private string _downloadText = "—";
    [ObservableProperty] private string _uploadText = "—";
    [ObservableProperty] private string _idlePingText = "—";
    [ObservableProperty] private string _loadedPingText = "—";
    [ObservableProperty] private string _speedJitterText = "—";
    [ObservableProperty] private string _gradeText = "?";
    [ObservableProperty] private Brush _gradeBrush = HealthStatusToBrushConverter.Unknown;
    [ObservableProperty] private string? _gradeExplanation;
    [ObservableProperty] private string? _dataUsedText;
    [ObservableProperty] private string? _speedServerText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpeedWarnings))]
    private string? _speedWarningsText;

    [ObservableProperty] private int _selectedPacketCount;
    [ObservableProperty] private string? _packetPhase;
    [ObservableProperty] private double _packetProgress;
    [ObservableProperty] private string? _packetDiagnosis;

    public IReadOnlyList<int> PacketCountOptions { get; } = [50, 100, 500];
    public ObservableCollection<PacketLossItem> PacketLoss { get; } = [];

    public bool IsTesting => IsSpeedTesting || IsPacketTesting;
    public bool HasSpeedResult => SpeedResult is not null;
    public bool ShowSpeedEmpty => SpeedResult is null && !IsSpeedTesting;
    public bool HasSpeedWarnings => !string.IsNullOrWhiteSpace(SpeedWarningsText);
    public bool HasPacketLoss => PacketLoss.Count > 0;
    public bool HasPacketDiagnosis => !string.IsNullOrWhiteSpace(PacketDiagnosis);

    /// <summary>Roughly how long the packet test takes: one ping every 200 ms per target, all targets in parallel.</summary>
    public string PacketDurationText => $"About {Math.Ceiling(SelectedPacketCount * PacketLossProbe.IntervalMs / 1000.0 + 1):0} seconds";

    public string SpeedDataText
    {
        get
        {
            var options = Settings.Current.Network ?? new NetworkSettings();
            return $"Uses up to {options.DownloadMegabytes + options.UploadMegabytes} MB of data ({options.DownloadMegabytes} MB down, {options.UploadMegabytes} MB up) and takes about 30 seconds.";
        }
    }

    partial void OnSelectedPacketCountChanged(int value) => OnPropertyChanged(nameof(PacketDurationText));

    partial void OnIsSpeedTestingChanged(bool value) => OnPropertyChanged(nameof(ShowSpeedEmpty));

    [RelayCommand]
    private async Task RunSpeedTestAsync()
    {
        if (!IsIdle)
        {
            return;
        }

        await RunSpeedTestCoreAsync();
    }

    [RelayCommand]
    private async Task RunPacketLossAsync()
    {
        if (!IsIdle)
        {
            return;
        }

        await RunPacketLossCoreAsync();
    }

    /// <summary>Header "Run all tests": Wi-Fi, then packet loss, then the speed test (one at a time so they do not disturb each other).</summary>
    [RelayCommand]
    private async Task RunAllTestsAsync()
    {
        if (!IsIdle)
        {
            return;
        }

        bool wifiDone = await RunBusyAsync(async ct =>
        {
            var progress = new Progress<ProgressUpdate>(p => BusyText = p.Stage);
            ApplyAdapters(await _network.GetAdaptersAsync(ct));
            ApplyWifi(await _network.AnalyzeWifiAsync(progress, ct));
        }, "Analyzing Wi-Fi…");

        // Cancelling (or a failure in) the Wi-Fi step stops the whole run.
        if (wifiDone && await RunPacketLossCoreAsync())
        {
            await RunSpeedTestCoreAsync();
        }
    }

    private bool CanCancelTest() => IsTesting;

    [RelayCommand(CanExecute = nameof(CanCancelTest))]
    private void CancelTest()
    {
        try
        {
            _testCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task<bool> RunSpeedTestCoreAsync()
    {
        using var cts = new CancellationTokenSource();
        _testCts = cts;
        IsSpeedTesting = true;
        SpeedPhase = "Starting…";
        SpeedLive = null;
        SpeedProgress = 0;
        ErrorMessage = null;
        var progress = new Progress<ProgressUpdate>(p =>
        {
            SpeedPhase = p.Stage;
            SpeedLive = p.Detail;
            if (p.Percent is { } pct)
            {
                SpeedProgress = pct;
            }
        });

        try
        {
            ApplySpeed(await _network.RunSpeedTestAsync(progress, cts.Token));
            return true;
        }
        catch (OperationCanceledException)
        {
            _notifications.Info("Speed test cancelled", "No result was saved.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Speed test failed.");
            ErrorMessage = "The speed test failed: " + ex.Message;
            return false;
        }
        finally
        {
            IsSpeedTesting = false;
            _testCts = null;
        }
    }

    private async Task<bool> RunPacketLossCoreAsync()
    {
        using var cts = new CancellationTokenSource();
        _testCts = cts;
        IsPacketTesting = true;
        PacketPhase = "Starting…";
        PacketProgress = 0;
        ErrorMessage = null;
        var progress = new Progress<ProgressUpdate>(p =>
        {
            PacketPhase = p.Detail is null ? p.Stage : $"{p.Stage}: {p.Detail}";
            if (p.Percent is { } pct)
            {
                PacketProgress = pct;
            }
        });

        try
        {
            var results = await _network.RunPacketLossTestAsync(SelectedPacketCount, progress, cts.Token);
            PacketLoss.Clear();
            foreach (var result in results)
            {
                PacketLoss.Add(new PacketLossItem(result));
            }

            PacketDiagnosis = PacketLossMath.Diagnose(results);
            OnPropertyChanged(nameof(HasPacketLoss));
            OnPropertyChanged(nameof(HasPacketDiagnosis));
            return true;
        }
        catch (OperationCanceledException)
        {
            _notifications.Info("Packet loss test cancelled", "No result was saved.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Packet loss test failed.");
            ErrorMessage = "The packet loss test failed: " + ex.Message;
            return false;
        }
        finally
        {
            IsPacketTesting = false;
            _testCts = null;
        }
    }

    private void ApplySpeed(SpeedTestResult result)
    {
        SpeedResult = result;
        DownloadText = NetworkUi.Mbps(result.DownloadMbps);
        UploadText = NetworkUi.Mbps(result.UploadMbps);
        IdlePingText = NetworkUi.Ms(result.IdleLatencyMs);
        LoadedPingText = NetworkUi.Ms(result.LoadedLatencyMs);
        SpeedJitterText = NetworkUi.Ms(result.JitterMs);
        GradeText = result.Grade;
        GradeBrush = result.Grade switch
        {
            "A+" or "A" => HealthStatusToBrushConverter.Good,
            "B" or "C" => HealthStatusToBrushConverter.Warning,
            "D" or "F" => HealthStatusToBrushConverter.Critical,
            _ => HealthStatusToBrushConverter.Unknown,
        };
        GradeExplanation = result.BufferbloatMs is { } extra
            ? $"Under load your latency rose by {extra:0} ms. {SpeedMath.DescribeGrade(result.Grade)}"
            : SpeedMath.DescribeGrade(result.Grade);
        DataUsedText = $"{SpeedMath.DataUsedText(result.BytesDownloaded, result.BytesUploaded)} used in {result.Duration.TotalSeconds:0} s";
        SpeedServerText = $"Measured against {result.Server}" + (result.PacketLossPercent > 0 ? $" · {result.PacketLossPercent:0.#} % of latency probes failed" : string.Empty);
        SpeedWarningsText = result.Warnings.Count == 0 ? null : string.Join("\n", result.Warnings);
    }
}
