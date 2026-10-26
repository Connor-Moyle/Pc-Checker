// TODO(overclock): overclocking module — NvAPIWrapper, XTU/Ryzen Master CLI, stress test, watchdog.
using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking;

/// <summary>Windows implementation of <see cref="IOverclockService"/>.</summary>
public sealed class WindowsOverclockService : IOverclockService
{
    private const string NotImplemented = "Overclocking is not implemented yet.";

    public OverclockCapabilities? Capabilities { get; private set; }

    public Task<OverclockCapabilities> DetectAsync(CancellationToken cancellationToken)
    {
        Capabilities = OverclockCapabilities.None(NotImplemented);
        return Task.FromResult(Capabilities);
    }

    public Task<OverclockState> GetStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new OverclockState(OverclockProfile.Stock(), null, null, null, null, null, null));

    public Task<OperationResult> ApplyAsync(OverclockProfile profile, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> ResetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<StressTestResult> RunStressTestAsync(StressTarget target, int seconds, double abortCpuTempC, double abortGpuTempC,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(new StressTestResult(target, 0, null, null, false, 0, false, NotImplemented, false));

    public Task<OperationResult> CheckUncleanShutdownAndRevertAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Ok("No overclock session to recover."));

    public Task MarkSessionCleanAsync() => Task.CompletedTask;
}
