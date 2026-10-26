// TODO(health): health module — SMART via WMI/MSFT_PhysicalDisk, battery report, event logs, dumps.
using PcChecker.Core.Common;

namespace PcChecker.Core.Health;

/// <summary>Windows implementation of <see cref="IHealthService"/>.</summary>
public sealed class WindowsHealthService : IHealthService
{
    public HealthReport? Last { get; private set; }

    public Task<HealthReport> AnalyzeAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Health check", 100));
        Last = HealthReport.Empty("Health analysis is not implemented yet.");
        return Task.FromResult(Last);
    }
}
