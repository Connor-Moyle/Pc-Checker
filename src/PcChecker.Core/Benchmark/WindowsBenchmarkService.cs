using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Memory;
using PcChecker.Core.Overclocking;
using PcChecker.Core.Overclocking.GpuLoad;
using PcChecker.Core.Sensors;
using PcChecker.Core.Storage;

namespace PcChecker.Core.Benchmark;

/// <summary>
/// Real benchmark: processor and memory workloads in managed code (<see cref="CpuBenchmarkRunner"/>,
/// <see cref="MemoryBenchmarkRunner"/>), the disk test through the Storage page's benchmark on the Windows drive, and
/// graphics throughput from the Overclock page's Direct3D 11 load generator. Every external call is guarded; a test
/// that cannot run comes back skipped or failed with the reason instead of throwing.
/// </summary>
public sealed class WindowsBenchmarkService : IBenchmarkService
{
    /// <summary>The graphics load runs this long before its dispatch rate counts (shader set-up and calibration).</summary>
    public static readonly TimeSpan GpuWarmUp = TimeSpan.FromSeconds(3);

    private readonly IStorageToolsService _storage;
    private readonly ISensorService _sensors;
    private readonly ILogger<WindowsBenchmarkService> _logger;
    private IGpuLoadGenerator? _gpu;

    public WindowsBenchmarkService(IStorageToolsService storage, ISensorService sensors, ILogger<WindowsBenchmarkService> logger)
    {
        _storage = storage;
        _sensors = sensors;
        _logger = logger;
    }

    public string? SystemDrive
    {
        get
        {
            try
            {
                return StorageCodes.NormalizeDriveLetter(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
            }
            catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
            {
                return null;
            }
        }
    }

    public int DiskTestFileMb => 256;

    private IGpuLoadGenerator Gpu => _gpu ??= new D3D11ComputeLoadGenerator(_logger);

    public Task<BenchmarkSystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        string? gpuName = null;
        try
        {
            gpuName = GpuAdapterRanking.Pick(Gpu.ListAdapters(), -1)?.Name;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Graphics adapters could not be listed for the benchmark header.");
        }

        long memory = MemoryNative.TryGetMemoryStatus(out var total, out _) ? total : 0;
        return new BenchmarkSystemInfo(Environment.MachineName, ReadCpuName(), Environment.ProcessorCount, memory, gpuName, SystemDrive);
    }, cancellationToken);

    public async Task<BenchmarkConditions> CheckConditionsAsync(CancellationToken cancellationToken)
    {
        var (onBattery, percent) = PowerStatusProbe.Read();
        double? busy = null;
        if (TryReadSystemTimes(out var idle0, out var kernel0, out var user0))
        {
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            if (TryReadSystemTimes(out var idle1, out var kernel1, out var user1))
            {
                busy = BenchmarkConditionRules.CpuBusyPercent(idle0, kernel0, user0, idle1, kernel1, user1);
            }
        }

        var snapshot = _sensors.Latest;
        var facts = new BenchmarkConditionFacts(onBattery, percent, busy, SensorPicker.MaxCpuTemp(snapshot), SensorPicker.MaxGpuTemp(snapshot));
        var conditions = BenchmarkConditionRules.Evaluate(facts);
        _logger.LogInformation("Benchmark conditions: battery {Battery}, CPU {Busy:0} % busy, CPU {Cpu} °C, GPU {Gpu} °C, {Warnings} warnings.",
            onBattery, busy, facts.CpuTempC, facts.GpuTempC, conditions.Warnings.Count);
        return conditions;
    }

    public async Task<BenchmarkTestResult> RunTestAsync(BenchmarkTestKind test, BenchmarkOptions options, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var budget = TimeSpan.FromSeconds(options.ClampedSeconds);
        string stage = BenchmarkCatalog.Name(test);
        var watch = Stopwatch.StartNew();
        _logger.LogInformation("Benchmark test {Test} started ({Seconds} s).", test, options.ClampedSeconds);
        try
        {
            var result = test switch
            {
                BenchmarkTestKind.CpuSingleCore => BenchmarkResultBuilder.FromCpu(
                    await CpuBenchmarkRunner.RunAsync(1, CpuWorkloadSize.Standard, budget, progress, stage, cancellationToken).ConfigureAwait(false), false, watch.Elapsed.TotalSeconds),
                BenchmarkTestKind.CpuMultiCore => BenchmarkResultBuilder.FromCpu(
                    await CpuBenchmarkRunner.RunAsync(Environment.ProcessorCount, CpuWorkloadSize.Standard, budget, progress, stage, cancellationToken).ConfigureAwait(false), true, watch.Elapsed.TotalSeconds),
                BenchmarkTestKind.MemoryBandwidth => BenchmarkResultBuilder.FromBandwidth(
                    await MemoryBenchmarkRunner.RunBandwidthAsync(MemoryBenchmarkPlan.BufferBytes(FreeMemory(), MemoryBenchmarkPlan.BandwidthPreferredBytes, MemoryBenchmarkPlan.BandwidthMinimumBytes),
                        MemoryBenchmarkPlan.BandwidthThreads(Environment.ProcessorCount), budget, progress, stage, cancellationToken).ConfigureAwait(false), watch.Elapsed.TotalSeconds),
                BenchmarkTestKind.MemoryLatency => BenchmarkResultBuilder.FromLatency(
                    await MemoryBenchmarkRunner.RunLatencyAsync(MemoryBenchmarkPlan.BufferBytes(FreeMemory(), MemoryBenchmarkPlan.LatencyPreferredBytes, MemoryBenchmarkPlan.LatencyMinimumBytes),
                        budget, progress, stage, cancellationToken).ConfigureAwait(false), watch.Elapsed.TotalSeconds),
                BenchmarkTestKind.Disk => await RunDiskAsync(progress, watch, cancellationToken).ConfigureAwait(false),
                BenchmarkTestKind.GpuCompute => await RunGpuAsync(budget, progress, stage, watch, cancellationToken).ConfigureAwait(false),
                _ => BenchmarkTestResult.Skip(test, "Unknown test."),
            };
            _logger.LogInformation("Benchmark test {Test}: {Status}, score {Score} in {Seconds:0.0} s. {Note}", test, result.Status, result.Score, watch.Elapsed.TotalSeconds, result.Note);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Benchmark test {Test} cancelled.", test);
            throw;
        }
        catch (OutOfMemoryException ex)
        {
            _logger.LogWarning(ex, "Benchmark test {Test} ran out of memory.", test);
            return BenchmarkTestResult.Fail(test, "There was not enough free memory for this test. Close some programs and try again.", watch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Benchmark test {Test} failed.", test);
            return BenchmarkTestResult.Fail(test, "The test stopped with an error: " + ex.Message, watch.Elapsed.TotalSeconds);
        }
    }

    private async Task<BenchmarkTestResult> RunDiskAsync(IProgress<ProgressUpdate>? progress, Stopwatch watch, CancellationToken cancellationToken)
    {
        var drive = SystemDrive;
        if (drive is null)
        {
            return BenchmarkTestResult.Skip(BenchmarkTestKind.Disk, "The Windows drive could not be found.");
        }

        // The Storage benchmark writes one temporary file (delete-on-close) and removes it, also when cancelled.
        var result = await _storage.BenchmarkAsync(drive, DiskTestFileMb, progress, cancellationToken).ConfigureAwait(false);
        return BenchmarkResultBuilder.FromDisk(result, watch.Elapsed.TotalSeconds);
    }

    private async Task<BenchmarkTestResult> RunGpuAsync(TimeSpan budget, IProgress<ProgressUpdate>? progress, string stage, Stopwatch watch, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate(stage, 0, "Looking for a DirectX 11 graphics card…"));
        var adapters = await Task.Run(() => Gpu.ListAdapters(), cancellationToken).ConfigureAwait(false);
        var choice = GpuAdapterRanking.Pick(adapters, -1);
        if (choice is null)
        {
            return BenchmarkTestResult.Skip(BenchmarkTestKind.GpuCompute, "No DirectX 11 graphics card was found (only the Microsoft software renderer is available).");
        }

        var duration = budget + GpuWarmUp;
        var samples = new List<double>();
        var live = new InlineProgress<GpuLoadProgress>(p =>
        {
            if (p.Elapsed >= GpuWarmUp && p.DispatchesPerSecond > 0)
            {
                lock (samples)
                {
                    samples.Add(p.DispatchesPerSecond);
                }
            }

            progress?.Report(new ProgressUpdate(stage, BenchmarkTiming.Percent(p.Elapsed, duration), $"{choice.Name}: {p.DispatchesPerSecond:0.0} dispatches/s"));
        });

        // Heavy keeps the card busy all the time (no idle gaps); result checking is off because only speed is measured.
        var options = new GpuLoadOptions(choice.Index, GpuLoadIntensity.Heavy, duration, VerifyResults: false);
        var result = await Gpu.RunAsync(options, live, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (result.NotStarted)
        {
            return BenchmarkTestResult.Skip(BenchmarkTestKind.GpuCompute, result.FailureReason ?? "The graphics test could not start.");
        }

        if (result.DriverReset)
        {
            return BenchmarkTestResult.Fail(BenchmarkTestKind.GpuCompute,
                (result.FailureReason ?? "The graphics driver reset.") + " If the card is overclocked, reset it on the Overclock page.", watch.Elapsed.TotalSeconds);
        }

        if (!result.Completed)
        {
            return BenchmarkTestResult.Fail(BenchmarkTestKind.GpuCompute, result.FailureReason ?? "The graphics test stopped early.", watch.Elapsed.TotalSeconds);
        }

        List<double> rates;
        lock (samples)
        {
            rates = samples.Count > 0 ? [.. samples] : [];
        }

        if (rates.Count == 0 && result.Elapsed.TotalSeconds > 0 && result.Dispatches > 0)
        {
            rates.Add(result.Dispatches / result.Elapsed.TotalSeconds);
        }

        return BenchmarkResultBuilder.FromGpu(rates, result.BufferBytes / GpuLoadPlanner.ElementBytes, result.Iterations, result.AdapterName, watch.Elapsed.TotalSeconds);
    }

    private static long? FreeMemory() => MemoryNative.TryGetMemoryStatus(out _, out var available) ? available : null;

    private string ReadCpuName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Unknown processor";
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
            {
                return string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "The processor name could not be read from the registry.");
        }

        return "Unknown processor";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    private bool TryReadSystemTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        idle = kernel = user = 0;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (!GetSystemTimes(out var i, out var k, out var u))
            {
                return false;
            }

            (idle, kernel, user) = (i.Value, k.Value, u.Value);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "GetSystemTimes is not available.");
            return false;
        }
    }
}
