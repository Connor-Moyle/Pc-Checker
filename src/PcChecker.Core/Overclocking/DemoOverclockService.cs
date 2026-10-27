using PcChecker.Core.Common;
using PcChecker.Core.Overclocking.GpuLoad;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// Demo overclocking for the sample gaming desktop (RTX 4070 + i7-13700K): NVAPI-style limits, Intel XTU not
/// installed, XMP off. Pure .NET: apply and reset only change in-memory state; the stress test (including the built-in
/// GPU load on the RTX 4070 or the integrated UHD 770) animates realistic progress.
/// </summary>
public sealed class DemoOverclockService : IOverclockService, IStressTestRunner
{
    private static readonly GpuLimits DemoLimits = new(-500, 300, -2000, 1500, 70, 115, 65, 90, true);

    private readonly Random _random = DemoData.CreateRandom(2070);
    private readonly DemoGpuLoadGenerator _gpuLoad = new();
    private OverclockProfile _current = OverclockProfile.Stock();
    private DateTimeOffset _appliedAt = DateTimeOffset.MinValue;

    public OverclockCapabilities? Capabilities { get; private set; }

    public async Task<OverclockCapabilities> DetectAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 700).ConfigureAwait(false);
        var xmp = XmpStateEvaluator.Evaluate([(4800, 6000), (4800, 6000)], "DDR5");
        var facts = new SupportFacts(GpuVendor.Nvidia, GpuControlMethod.NvApi, DemoLimits, false, "Intel", CpuControlMethod.None, xmp.State, true);
        Capabilities = new OverclockCapabilities(GpuVendor.Nvidia, DemoData.GpuName, GpuControlMethod.NvApi, DemoLimits, DemoData.CpuName, CpuControlMethod.None, null,
            xmp.State, xmp.ConfiguredMhz, xmp.MaxMhz,
            [
                "NVIDIA driver 32.0.15.6094 exposes clock offsets, power and temperature limits through NVAPI.",
                "Intel Extreme Tuning Utility is not installed; CPU ratio and voltage are changed in the BIOS.",
                xmp.Advice,
            ])
        {
            SupportMatrix = SupportMatrixBuilder.Build(facts),
        };
        return Capabilities;
    }

    public async Task<OverclockState> GetStateAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        bool load = (DateTimeOffset.Now - _appliedAt).TotalSeconds < 20;
        double gpuTemp = load ? DemoData.Jitter(_random, 64, 1.5) : DemoData.Jitter(_random, 46, 1.2);
        double gpuPower = load ? DemoData.Jitter(_random, 185 * _current.GpuPowerLimitPercent / 100.0, 6) : DemoData.Jitter(_random, 42, 3);
        int coreClock = (load ? 2730 : 2505) + _current.GpuCoreOffsetMhz + _random.Next(-15, 16);
        int memClock = 10501 + _current.GpuMemoryOffsetMhz;
        double cpuTemp = load ? DemoData.Jitter(_random, 72, 2) : DemoData.Jitter(_random, 58, 1.5);
        int cpuClock = (_current.CpuAllCoreRatio is { } r ? r * 100 : 5300) + _random.Next(-40, 41);
        return new OverclockState(_current.Clone(), coreClock, memClock, Math.Round(gpuTemp, 1), Math.Round(gpuPower, 1), Math.Round(cpuTemp, 1), cpuClock);
    }

    public async Task<OperationResult> ApplyAsync(OverclockProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var capabilities = Capabilities ?? await DetectAsync(cancellationToken).ConfigureAwait(false);
        await DemoData.DelayAsync(cancellationToken, 400, 900).ConfigureAwait(false);
        var ok = OverclockSafetyValidator.Validate(profile, capabilities, new Settings.OverclockSettings { ConsentAccepted = true, MaxGpuCoreOffsetMhz = 300, MaxGpuMemoryOffsetMhz = 1500 });
        if (!ok.Success)
        {
            return ok;
        }

        _current = profile.Clone();
        _appliedAt = DateTimeOffset.Now;
        return OperationResult.Ok($"Profile \"{profile.Name}\" applied to the {DemoData.GpuName} (demo: nothing was changed on this machine).");
    }

    public async Task<OperationResult> ResetAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 200, 500).ConfigureAwait(false);
        _current = OverclockProfile.Stock();
        _appliedAt = DateTimeOffset.MinValue;
        return OperationResult.Ok("The graphics card is back at stock clocks (demo).");
    }

    public async Task<IReadOnlyList<GpuAdapterChoice>> ListGpuAdaptersAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 100, 250).ConfigureAwait(false);
        return _gpuLoad.ListAdapters();
    }

    public StressPreflight CheckPreflight() => StressPreflight.Ok;

    public async Task<StressTestResult> RunStressTestAsync(StressTarget target, int seconds, double abortCpuTempC, double abortGpuTempC, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var outcome = await RunStressTestAsync(new StressTestRequest(target, seconds, abortCpuTempC, abortGpuTempC), null, progress, cancellationToken).ConfigureAwait(false);
        return outcome.Result;
    }

    /// <summary>Animated stress test: temperatures climb, the GPU load reports dispatch rates for the chosen intensity, no errors.</summary>
    public async Task<StressTestOutcome> RunStressTestAsync(StressTestRequest request, IProgress<StressLiveStatus>? live, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        int seconds = Math.Clamp(request.Seconds, 5, 3 * 60 * 60);
        int ticks = Math.Clamp(seconds, 5, 20);
        var adapter = GpuAdapterRanking.Pick(_gpuLoad.ListAdapters(), request.GpuAdapterIndex);
        var plan = GpuLoadPlanner.Create(request.Intensity, adapter?.DedicatedVideoMemoryBytes ?? DemoData.GpuMemoryBytes);
        double rate = 1000 / plan.TargetDispatchMs * plan.DutyCycle;
        double gpuCeiling = !request.WatchesGpu ? 50 : request.GpuMode == GpuStressMode.Guided ? 71 : request.Intensity switch
        {
            GpuLoadIntensity.Light => 63,
            GpuLoadIntensity.Medium => 69,
            _ => 74,
        };

        double cpu = 58, gpu = 46, dispatches = 0;
        long checks = 0;
        bool aborted = false;
        int elapsed = 0;
        var cpuClocks = new List<double>();
        var gpuClocks = new List<double>();
        for (int i = 1; i <= ticks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            int previous = elapsed;
            elapsed = (int)Math.Round(seconds * (double)i / ticks);
            cpu = Math.Min(request.LoadsCpu ? 84 : 62, cpu + 1.6 + _random.NextDouble());
            gpu = Math.Min(gpuCeiling, gpu + 1.4 + _random.NextDouble());
            int cpuClock = request.LoadsCpu ? 5300 - i * 4 : 5300 + _random.Next(-40, 41);
            int gpuClock = request.WatchesGpu ? 2730 + _current.GpuCoreOffsetMhz - i * 2 + _random.Next(-8, 9) : 2505;
            cpuClocks.Add(cpuClock);
            gpuClocks.Add(gpuClock);
            double liveRate = request.LoadsGpu ? Math.Round(DemoData.Jitter(_random, rate, rate * 0.03), 1) : 0;
            dispatches += liveRate * (elapsed - previous);
            checks += request.LoadsCpu ? 1340L * (elapsed - previous) * 4 : 0;

            var status = new StressLiveStatus(TimeSpan.FromSeconds(elapsed), 100.0 * i / ticks, cpu, gpu, cpuClock, gpuClock,
                request.WatchesGpu ? 99 : 3, liveRate, 0, checks, 0, request.LoadsGpu ? adapter?.Name : null);
            live?.Report(status);
            progress?.Report(new ProgressUpdate("Stress test running", status.Percent, StressText.Detail(request, status)));
            if (cpu >= request.AbortCpuTempC || gpu >= request.AbortGpuTempC)
            {
                aborted = true;
                break;
            }
        }

        bool reverted = false;
        if (aborted && !_current.IsStock)
        {
            progress?.Report(new ProgressUpdate("Reverting the overclock", null, "Temperature limit reached; returning to stock."));
            reverted = (await ResetAsync(CancellationToken.None).ConfigureAwait(false)).Success;
        }

        GpuLoadResult? gpuResult = request.LoadsGpu && adapter is not null
            ? new GpuLoadResult(true, (long)dispatches, 0, adapter.Name, null, TimeSpan.FromSeconds(elapsed)) { BufferBytes = plan.BufferBytes, Iterations = 2304 }
            : null;
        var facts = new StressFacts(request.Target, request.GpuMode, elapsed)
        {
            CpuChecks = checks,
            GpuDispatches = gpuResult?.Dispatches ?? 0,
            GpuAdapterName = gpuResult?.AdapterName,
            WatchdogAborted = aborted,
            MaxCpuTempC = Math.Round(cpu),
            MaxGpuTempC = Math.Round(gpu),
            AbortCpuTempC = request.AbortCpuTempC,
            AbortGpuTempC = request.AbortGpuTempC,
            AverageCpuClockMhz = StressText.Average(cpuClocks),
            AverageGpuClockMhz = StressText.Average(gpuClocks),
            OverclockReverted = reverted,
        };
        var outcome = StressOutcomes.From(request.Target, facts, gpuResult, facts.MaxCpuTempC, facts.MaxGpuTempC, false, aborted);
        string summary = outcome.Result.Summary + " (demo)";
        return outcome with { Result = outcome.Result with { Summary = summary }, Verdict = outcome.Verdict with { Summary = summary } };
    }

    public Task<OperationResult> CheckUncleanShutdownAndRevertAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Ok("The last session ended cleanly."));

    public Task MarkSessionCleanAsync() => Task.CompletedTask;
}
