// TODO(latency): latency module — performance counters, NtQueryTimerResolution, bcdedit/powercfg parsing, ping/DNS, provider.
using PcChecker.Core.Common;

namespace PcChecker.Core.Latency;

/// <summary>Windows implementation of <see cref="ILatencyService"/>.</summary>
public sealed class WindowsLatencyService : ILatencyService
{
    public LatencyReport? Last { get; private set; }

    public Task<LatencyReport> AnalyzeAsync(int sampleSeconds, bool includeNetwork, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        Last = LatencyReport.Empty("Latency analysis is not implemented yet.");
        return Task.FromResult(Last);
    }

    public Task<IReadOnlyList<NetworkLatencyResult>> PingTestAsync(IEnumerable<(string Target, string Label)> targets, int count, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NetworkLatencyResult>>([]);

    public Task<double?> MeasureDnsAsync(string host, CancellationToken cancellationToken) => Task.FromResult<double?>(null);
}
