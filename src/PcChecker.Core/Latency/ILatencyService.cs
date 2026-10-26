using PcChecker.Core.Common;

namespace PcChecker.Core.Latency;

/// <summary>DPC/ISR load, timer resolution, boot and power settings that affect latency, network ping/DNS, and findings.</summary>
public interface ILatencyService
{
    Task<LatencyReport> AnalyzeAsync(int sampleSeconds, bool includeNetwork, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    Task<IReadOnlyList<NetworkLatencyResult>> PingTestAsync(IEnumerable<(string Target, string Label)> targets, int count, CancellationToken cancellationToken);
    Task<double?> MeasureDnsAsync(string host, CancellationToken cancellationToken);
    LatencyReport? Last { get; }
}
