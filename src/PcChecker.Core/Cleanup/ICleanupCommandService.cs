using PcChecker.Core.Common;

namespace PcChecker.Core.Cleanup;

/// <summary>
/// Runs the Windows tools behind <see cref="CleanupKind.Command"/> categories: DISM component cleanup, Disk Cleanup
/// handlers (StateFlags + cleanmgr /sagerun), restore point trimming and the clipboard. The cleaner calls
/// <see cref="RunAsync"/> inside <c>IChangeGuard</c>, after the user confirmed.
/// </summary>
public interface ICleanupCommandService
{
    /// <summary>A size estimate for the scan (DISM's analysis, the shadow storage in use); never changes anything.</summary>
    Task<CommandEstimate> EstimateAsync(CleanupCategory category, CancellationToken cancellationToken);

    /// <summary>Runs the tool for one category. Output lines are streamed through <paramref name="progress"/>; cancellable.</summary>
    Task<OperationResult> RunAsync(CleanupCategory category, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}
