using PcChecker.Core.Common;

namespace PcChecker.Core.Health;

/// <summary>Collects disk SMART, battery wear, device problems, event-log errors and crash dumps into a health report.</summary>
public interface IHealthService
{
    Task<HealthReport> AnalyzeAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    HealthReport? Last { get; }
}
