using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking;

/// <summary>GPU/CPU tuning, stress testing and crash recovery.</summary>
public interface IOverclockService
{
    Task<OverclockCapabilities> DetectAsync(CancellationToken cancellationToken);
    OverclockCapabilities? Capabilities { get; }
    Task<OverclockState> GetStateAsync(CancellationToken cancellationToken);
    Task<OperationResult> ApplyAsync(OverclockProfile profile, CancellationToken cancellationToken);
    Task<OperationResult> ResetAsync(CancellationToken cancellationToken);
    Task<StressTestResult> RunStressTestAsync(StressTarget target, int seconds, double abortCpuTempC, double abortGpuTempC,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<OperationResult> CheckUncleanShutdownAndRevertAsync(CancellationToken cancellationToken);
    Task MarkSessionCleanAsync();
}
