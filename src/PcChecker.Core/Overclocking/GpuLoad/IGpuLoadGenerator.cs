namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>
/// Puts a real, verifiable load on a graphics card without a game: a compute shader runs over a large buffer and
/// its results are compared between runs, so a wrong answer (an "artifact") is counted as an error.
/// </summary>
public interface IGpuLoadGenerator
{
    /// <summary>
    /// Runs the load until <see cref="GpuLoadOptions.Duration"/> passes or <paramref name="cancellationToken"/> is
    /// cancelled. Never throws for cancellation or GPU faults: those are reported in the result.
    /// </summary>
    Task<GpuLoadResult> RunAsync(GpuLoadOptions options, IProgress<GpuLoadProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Hardware adapters that can run the load, best first (software renderers are left out). Empty when none.</summary>
    IReadOnlyList<GpuAdapterChoice> ListAdapters();
}
