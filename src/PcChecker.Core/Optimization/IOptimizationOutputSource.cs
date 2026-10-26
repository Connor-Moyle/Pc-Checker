namespace PcChecker.Core.Optimization;

/// <summary>
/// Optional companion to <see cref="IOptimizationService"/>: long-running actions (DISM, SFC, drive optimization,
/// network reset) stream their console output through <see cref="OutputLine"/> so the page can show progress.
/// </summary>
public interface IOptimizationOutputSource
{
    event EventHandler<string>? OutputLine;
}
