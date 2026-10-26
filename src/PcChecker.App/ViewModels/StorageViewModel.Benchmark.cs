using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Storage;

namespace PcChecker.App.ViewModels;

/// <summary>Benchmark part of the Storage page.</summary>
public sealed partial class StorageViewModel
{
    private CancellationTokenSource? _benchmarkCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyToolRunning))]
    [NotifyCanExecuteChangedFor(nameof(RunBenchmarkCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelBenchmarkCommand))]
    private bool _isBenchmarking;

    [ObservableProperty]
    private int _selectedTestFileMb = 1024;

    [ObservableProperty]
    private string _benchmarkStage = string.Empty;

    [ObservableProperty]
    private string? _benchmarkDetail;

    [ObservableProperty]
    private double _benchmarkPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBenchmarkResult))]
    private DiskBenchmarkResult? _benchmarkResult;

    [ObservableProperty]
    private string? _benchmarkSummary;

    [ObservableProperty]
    private string _benchmarkExpectedClass = string.Empty;

    [ObservableProperty]
    private string _seqReadText = "—";

    [ObservableProperty]
    private string _seqWriteText = "—";

    [ObservableProperty]
    private string _rand4kReadText = "—";

    [ObservableProperty]
    private string _rand4kWriteText = "—";

    [ObservableProperty]
    private string _rand4kReadIopsText = string.Empty;

    [ObservableProperty]
    private string _rand4kWriteIopsText = string.Empty;

    public ObservableCollection<BenchmarkRow> BenchmarkRows { get; } = [];

    public IReadOnlyList<int> TestFileSizes { get; } = [256, 1024, 4096];

    public bool HasBenchmarkResult => BenchmarkResult is not null;

    private bool CanRunBenchmark() => SelectedVolume is not null && !IsAnyToolRunning && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunBenchmark))]
    private async Task RunBenchmark()
    {
        var volume = SelectedVolume;
        if (volume is null)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync($"Benchmark {volume.Letter}?",
            $"PC Checker will write a temporary {SelectedTestFileMb} MB file to {volume.Letter}, read it back, do 2,000 random 4 KB reads and writes, then delete the file.\n\n" +
            "Close other programs first for accurate numbers. The amount written is tiny compared with an SSD's rated lifetime.",
            "Run benchmark");
        if (!ok)
        {
            return;
        }

        await RunBenchmarkCoreAsync(volume, CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(IsBenchmarking))]
    private void CancelBenchmark()
    {
        try
        {
            _benchmarkCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunBenchmarkCoreAsync(VolumeOption volume, CancellationToken outerToken)
    {
        if (IsBenchmarking)
        {
            return;
        }

        _benchmarkCts?.Dispose();
        _benchmarkCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        var token = _benchmarkCts.Token;

        ClearBenchmarkResult();
        ErrorMessage = null;
        IsBenchmarking = true;
        RaiseToolStateChanged();
        BenchmarkStage = "Preparing…";

        var progress = new Progress<ProgressUpdate>(p =>
        {
            BenchmarkStage = p.Stage;
            BenchmarkDetail = p.Detail;
            if (p.Percent is { } percent)
            {
                BenchmarkPercent = percent;
            }
        });

        try
        {
            _logger.LogInformation("Starting benchmark of {Drive} ({Size} MB).", volume.Letter, SelectedTestFileMb);
            var result = await _storage.BenchmarkAsync(volume.Letter, SelectedTestFileMb, progress, token);
            ApplyBenchmarkResult(volume, result);
        }
        catch (OperationCanceledException)
        {
            BenchmarkStage = "Cancelled";
            BenchmarkDetail = "The test file was removed.";
            _notifications.Info("Benchmark", "The benchmark was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Benchmark failed.");
            ErrorMessage = "The benchmark could not run: " + ex.Message;
        }
        finally
        {
            IsBenchmarking = false;
            RaiseToolStateChanged();
            _benchmarkCts.Dispose();
            _benchmarkCts = null;
        }
    }

    private void ApplyBenchmarkResult(VolumeOption volume, DiskBenchmarkResult result)
    {
        if (result.SeqReadMBps <= 0 && result.SeqWriteMBps <= 0)
        {
            // The service explains why in the summary (not enough space, drive not ready…).
            ErrorMessage = result.Summary;
            BenchmarkStage = "Not run";
            BenchmarkDetail = null;
            return;
        }

        var expected = BenchmarkReference.Expected(volume.MediaType, volume.BusType);
        BenchmarkResult = result;
        BenchmarkSummary = result.Summary;
        BenchmarkExpectedClass = expected.Name;
        SeqReadText = result.SeqReadMBps.ToString(result.SeqReadMBps >= 100 ? "N0" : "0.0");
        SeqWriteText = result.SeqWriteMBps.ToString(result.SeqWriteMBps >= 100 ? "N0" : "0.0");
        Rand4kReadText = result.Rand4kReadMBps.ToString(result.Rand4kReadMBps >= 100 ? "N0" : "0.0");
        Rand4kWriteText = result.Rand4kWriteMBps.ToString(result.Rand4kWriteMBps >= 100 ? "N0" : "0.0");
        Rand4kReadIopsText = $"{result.Rand4kReadIops:N0} IOPS";
        Rand4kWriteIopsText = $"{result.Rand4kWriteIops:N0} IOPS";
        BenchmarkRows.Clear();
        foreach (var row in BenchmarkReference.Compare(result, expected))
        {
            BenchmarkRows.Add(new BenchmarkRow(row));
        }

        BenchmarkStage = "Done";
        BenchmarkDetail = $"{ByteSize.Format(result.TestFileBytes)} test file, deleted afterwards.";
        BenchmarkPercent = 100;
        _notifications.Success("Benchmark", $"{volume.Letter}: {result.SeqReadMBps:N0} MB/s read, {result.SeqWriteMBps:N0} MB/s write.");
    }

    private void ClearBenchmarkResult()
    {
        BenchmarkResult = null;
        BenchmarkSummary = null;
        BenchmarkExpectedClass = string.Empty;
        BenchmarkStage = string.Empty;
        BenchmarkDetail = null;
        BenchmarkPercent = 0;
        SeqReadText = SeqWriteText = Rand4kReadText = Rand4kWriteText = "—";
        Rand4kReadIopsText = Rand4kWriteIopsText = string.Empty;
        BenchmarkRows.Clear();
    }
}
