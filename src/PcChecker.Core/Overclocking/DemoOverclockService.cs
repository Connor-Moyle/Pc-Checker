using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// Demo overclocking for the sample gaming desktop (RTX 4070 + i7-13700K): NVAPI-style limits, Intel XTU not
/// installed, XMP off. Pure .NET: apply and reset only change in-memory state; the stress test animates progress.
/// </summary>
public sealed class DemoOverclockService : IOverclockService
{
    private static readonly GpuLimits DemoLimits = new(-500, 300, -2000, 1500, 70, 115, 65, 90, true);

    private readonly Random _random = DemoData.CreateRandom(2070);
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

    public async Task<StressTestResult> RunStressTestAsync(StressTarget target, int seconds, double abortCpuTempC, double abortGpuTempC, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        int ticks = Math.Clamp(seconds, 5, 20);
        double cpu = 58, gpu = 46;
        bool aborted = false;
        int elapsed = 0;
        for (int i = 1; i <= ticks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            elapsed = (int)Math.Round(seconds * (double)i / ticks);
            cpu = Math.Min(target == StressTarget.Gpu ? 62 : 84, cpu + 1.6 + _random.NextDouble());
            gpu = Math.Min(target == StressTarget.Cpu ? 50 : 71, gpu + 1.4 + _random.NextDouble());
            progress?.Report(new ProgressUpdate("Stress test running", 100.0 * i / ticks, $"CPU {cpu:0} °C @ {5300 - i * 4} MHz · GPU {gpu:0} °C @ {2730 + _current.GpuCoreOffsetMhz} MHz · {i * 1340} checks, 0 errors"));
            if (cpu >= abortCpuTempC || gpu >= abortGpuTempC)
            {
                aborted = true;
                break;
            }
        }

        if (aborted && !_current.IsStock)
        {
            progress?.Report(new ProgressUpdate("Reverting the overclock", null, "Temperature limit reached; returning to stock."));
            await ResetAsync(CancellationToken.None).ConfigureAwait(false);
        }

        string summary = ThrottleHeuristic.Summarize(target, 0, aborted, false, cpu, gpu, abortCpuTempC, abortGpuTempC);
        return new StressTestResult(target, elapsed, Math.Round(cpu), Math.Round(gpu), false, 0, !aborted, summary + " (demo)", aborted);
    }

    public Task<OperationResult> CheckUncleanShutdownAndRevertAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Ok("The last session ended cleanly."));

    public Task MarkSessionCleanAsync() => Task.CompletedTask;
}
