// TODO(performance): performance module — startup items, services, processes, facts -> SlowdownRules.
using PcChecker.Core.Common;

namespace PcChecker.Core.Performance;

/// <summary>Windows implementation of <see cref="IPerformanceAnalyzer"/>.</summary>
public sealed class WindowsPerformanceAnalyzer : IPerformanceAnalyzer
{
    public PerformanceReport? Last { get; private set; }

    public Task<PerformanceReport> AnalyzeAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        Last = PerformanceReport.Empty("Performance analysis is not implemented yet.");
        return Task.FromResult(Last);
    }
}
