using PcChecker.Core.Common;

namespace PcChecker.Core.Performance;

/// <summary>Finds what is slowing the PC down (startup items, services, memory and disk pressure, settings).</summary>
public interface IPerformanceAnalyzer
{
    Task<PerformanceReport> AnalyzeAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
    PerformanceReport? Last { get; }
}
