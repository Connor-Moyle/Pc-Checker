using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking;

/// <summary>Demo overclocking for an RTX 4070 + i7-13700K: NVAPI-style limits, XTU not installed, XMP off.</summary>
public sealed class DemoOverclockService : IOverclockService
{
    private OverclockProfile _current = OverclockProfile.Stock();

    public OverclockCapabilities? Capabilities { get; private set; }

    public async Task<OverclockCapabilities> DetectAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        Capabilities = new OverclockCapabilities(GpuVendor.Nvidia, DemoData.GpuName, GpuControlMethod.NvApi,
            new GpuLimits(-500, 300, -2000, 1500, 70, 115, 65, 90, true), DemoData.CpuName, CpuControlMethod.None, null, "Disabled", 4800, 6000,
            ["Intel Extreme Tuning Utility is not installed; CPU tuning is done in the BIOS.", "XMP is off: the memory runs at 4800 instead of 6000 MT/s."])
        {
            SupportMatrix =
            [
                ("GPU core/memory offset", "NVIDIA", SupportLevel.Full, "Applied through NVAPI performance states."),
                ("GPU power and temperature limit", "NVIDIA", SupportLevel.Full, "Applied through NVAPI power policies."),
                ("GPU fan speed", "NVIDIA", SupportLevel.Full, "Through the shared hardware monitor."),
                ("CPU ratio and voltage", "Intel", SupportLevel.Unsupported, "Install Intel XTU or change these in the BIOS."),
                ("Memory XMP/EXPO", "Intel", SupportLevel.MonitorOnly, "Enable XMP in the BIOS; the app only reports the state."),
            ],
        };
        return Capabilities;
    }

    public async Task<OverclockState> GetStateAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 200).ConfigureAwait(false);
        return new OverclockState(_current.Clone(), 2505 + _current.GpuCoreOffsetMhz, 10501 + _current.GpuMemoryOffsetMhz, 46, 42, 58, 5300);
    }

    public async Task<OperationResult> ApplyAsync(OverclockProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _current = profile.Clone();
        return OperationResult.Ok($"Profile \"{profile.Name}\" applied to the GPU (demo).");
    }

    public async Task<OperationResult> ResetAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _current = OverclockProfile.Stock();
        return OperationResult.Ok("GPU returned to stock clocks (demo).");
    }

    public async Task<StressTestResult> RunStressTestAsync(StressTarget target, int seconds, double abortCpuTempC, double abortGpuTempC, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        int total = Math.Clamp(seconds, 1, 15);
        for (int i = 0; i < total; i++)
        {
            progress?.Report(new ProgressUpdate("Stress test running", 100.0 * i / total, $"CPU {72 + i}°C, GPU {62 + i}°C"));
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        return new StressTestResult(target, seconds, 72 + total, 62 + total, false, 0, true, "Stable: no errors and temperatures stayed under the limits (demo).", false);
    }

    public Task<OperationResult> CheckUncleanShutdownAndRevertAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Ok("The last session ended cleanly."));

    public Task MarkSessionCleanAsync() => Task.CompletedTask;
}
