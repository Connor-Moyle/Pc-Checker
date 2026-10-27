using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Overclocking.GpuLoad;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// Stress test half of <see cref="WindowsOverclockService"/>: CPU burn (<see cref="CpuStressRunner"/>), the built-in
/// GPU load (<see cref="D3D11ComputeLoadGenerator"/>) or the guided GPU test, all under a once-a-second temperature
/// watchdog. A temperature limit or a graphics-driver reset stops the test and, when an overclock is applied, resets
/// it to defaults through the same guarded path as the Reset button.
/// </summary>
public sealed partial class WindowsOverclockService : IStressTestRunner
{
    private IGpuLoadGenerator? _gpuLoad;

    private IGpuLoadGenerator GpuLoad => _gpuLoad ??= new D3D11ComputeLoadGenerator(_logger);

    public Task<IReadOnlyList<GpuAdapterChoice>> ListGpuAdaptersAsync(CancellationToken cancellationToken) =>
        Task.Run(() => GpuLoad.ListAdapters(), cancellationToken);

    public StressPreflight CheckPreflight()
    {
        var (onBattery, percent) = PowerStatusProbe.Read();
        return StressSafety.Evaluate(onBattery, percent);
    }

    public async Task<StressTestResult> RunStressTestAsync(StressTarget target, int seconds, double abortCpuTempC, double abortGpuTempC,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var outcome = await RunStressTestAsync(new StressTestRequest(target, seconds, abortCpuTempC, abortGpuTempC), null, progress, cancellationToken).ConfigureAwait(false);
        return outcome.Result;
    }

    public async Task<StressTestOutcome> RunStressTestAsync(StressTestRequest request, IProgress<StressLiveStatus>? live, IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        int seconds = Math.Clamp(request.Seconds, 5, 3 * 60 * 60);
        var preflight = CheckPreflight();
        if (!preflight.CanRun)
        {
            return StressOutcomes.NotStarted(request, preflight.Message ?? "The stress test cannot start right now.");
        }

        using var burnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runner = request.LoadsCpu ? new CpuStressRunner() : null;
        var burnTask = runner?.RunAsync(burnCts.Token);

        GpuLoadProgress? gpuLive = null;
        string? adapterName = null;
        double? maxCpu = null, maxGpu = null;
        var cpuClocks = new List<double>();
        var cpuLoads = new List<double?>();
        var gpuClocks = new List<double>();
        var gpuLoads = new List<double?>();
        bool aborted = false;
        int elapsed = 0;
        var started = DateTime.UtcNow;
        GpuLoadResult? gpuResult = null;
        Task<GpuLoadResult>? pendingGpu = null;
        StressRunStats stats = new(0, 0, 0);

        // Everything after the CPU burn has started sits inside this try, so a cancel at any point (also while the
        // adapters are listed) stops the burn and waits for it and the GPU load before returning.
        try
        {
            if (request.LoadsGpu)
            {
                var adapters = await ListGpuAdaptersAsync(cancellationToken).ConfigureAwait(false);
                adapterName = GpuAdapterRanking.Pick(adapters, request.GpuAdapterIndex)?.Name;
                progress?.Report(new ProgressUpdate("Starting the GPU load", 0, $"Preparing {adapterName ?? "the graphics card"}…"));
                var options = new GpuLoadOptions(request.GpuAdapterIndex, request.Intensity, TimeSpan.FromSeconds(seconds + 30), request.VerifyGpuResults);
                pendingGpu = GpuLoad.RunAsync(options, new InlineProgress<GpuLoadProgress>(p => Volatile.Write(ref gpuLive, p)), burnCts.Token);
            }

            started = DateTime.UtcNow;

            while (elapsed < seconds)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                elapsed = (int)Math.Round((DateTime.UtcNow - started).TotalSeconds);

                var snapshot = _sensors.Latest;
                var readout = SensorPicker.Pick(snapshot);
                double? cpuTemp = SensorPicker.MaxCpuTemp(snapshot);
                double? gpuTemp = SensorPicker.MaxGpuTemp(snapshot);
                maxCpu = Max(maxCpu, cpuTemp);
                maxGpu = Max(maxGpu, gpuTemp);
                if (readout.CpuClockMhz is { } cc)
                {
                    cpuClocks.Add(cc);
                    cpuLoads.Add(request.LoadsCpu ? null : readout.CpuLoadPercent);
                }

                if (readout.GpuCoreClockMhz is { } gc)
                {
                    gpuClocks.Add(gc);
                    gpuLoads.Add(readout.GpuLoadPercent);
                }

                var gl = Volatile.Read(ref gpuLive);
                var status = new StressLiveStatus(TimeSpan.FromSeconds(elapsed), Math.Min(100, 100.0 * elapsed / seconds), cpuTemp, gpuTemp,
                    readout.CpuClockMhz, readout.GpuCoreClockMhz, readout.GpuLoadPercent, gl?.DispatchesPerSecond ?? 0, gl?.ErrorsSoFar ?? 0,
                    runner?.Iterations ?? 0, runner?.Errors ?? 0, adapterName);
                live?.Report(status);
                progress?.Report(new ProgressUpdate("Stress test running", status.Percent, StressText.Detail(request, status)));

                // Each sensor is held to its own limit: the CPU control temperature (Tjmax-aware on Ryzen 7000/9000),
                // the GPU core against the GPU limit, hot spot and memory against AMD's 110/105 °C.
                double cpuStop = TemperatureRoles.EffectiveCpuStopC((int)Math.Round(request.AbortCpuTempC), SensorPicker.CpuName(snapshot));
                var gpuBreach = SensorPicker.GpuLimitBreach(snapshot, request.AbortGpuTempC);
                if ((cpuTemp is { } ct && ct >= cpuStop) || gpuBreach is not null)
                {
                    aborted = true;
                    _logger.LogWarning("Stress test aborted by the watchdog: CPU {Cpu} °C (limit {CpuStop} °C), GPU {Gpu}.", cpuTemp, cpuStop, gpuBreach ?? "below its limits");
                    break;
                }

                if (pendingGpu is { IsCompleted: true })
                {
                    gpuResult = await pendingGpu.ConfigureAwait(false);
                    pendingGpu = null;
                    if (!gpuResult.Completed && (request.Target == StressTarget.Gpu || gpuResult.DriverReset))
                    {
                        // Nothing left to test (GPU only), or the driver reset: stop now rather than keep loading a recovering card.
                        break;
                    }
                }
            }
        }
        finally
        {
            burnCts.Cancel();
            if (pendingGpu is not null)
            {
                try
                {
                    gpuResult = await pendingGpu.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The GPU load did not shut down cleanly.");
                }
            }

            if (burnTask is not null)
            {
                stats = await burnTask.ConfigureAwait(false);
            }
        }

        bool driverReset = gpuResult?.DriverReset == true;

        bool reverted = false;
        if ((aborted || driverReset) && !_applied.IsStock)
        {
            progress?.Report(new ProgressUpdate("Reverting the overclock", null,
                driverReset ? "The graphics driver reset; returning to stock." : "Temperature limit reached; returning to stock."));
            var revert = await ResetAsync(CancellationToken.None).ConfigureAwait(false);
            reverted = revert.Success;
            _logger.LogInformation("Watchdog revert: {Message}", revert.Message);
        }

        bool throttle = (request.LoadsCpu && ThrottleHeuristic.Suspect(cpuClocks, cpuLoads))
            || (request.WatchesGpu && ThrottleHeuristic.Suspect(gpuClocks, gpuLoads));
        var facts = new StressFacts(request.Target, request.GpuMode, elapsed)
        {
            CpuErrors = stats.Errors,
            CpuChecks = stats.Iterations,
            GpuErrors = gpuResult?.Errors ?? 0,
            GpuDispatches = gpuResult?.Dispatches ?? 0,
            DriverReset = driverReset,
            GpuFailure = gpuResult is { Completed: false } ? gpuResult.FailureReason : null,
            GpuNotStarted = gpuResult?.NotStarted == true,
            GpuAdapterName = gpuResult?.AdapterName ?? adapterName,
            WatchdogAborted = aborted,
            ThrottleSuspected = throttle,
            NoTemperatureData = ThrottleHeuristic.LacksTemperatureData(request.Target, maxCpu, maxGpu),
            MaxCpuTempC = maxCpu,
            MaxGpuTempC = maxGpu,
            AbortCpuTempC = request.AbortCpuTempC,
            AbortGpuTempC = request.AbortGpuTempC,
            AverageCpuClockMhz = StressText.Average(cpuClocks),
            AverageGpuClockMhz = StressText.Average(gpuClocks),
            OverclockReverted = reverted,
        };
        return StressOutcomes.From(request.Target, facts, gpuResult, maxCpu, maxGpu, throttle, aborted);
    }
}
