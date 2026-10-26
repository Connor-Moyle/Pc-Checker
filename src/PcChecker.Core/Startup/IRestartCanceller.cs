using PcChecker.Core.Common;

namespace PcChecker.Core.Startup;

/// <summary>
/// Cancels a restart that <see cref="IStartupService.RestartToUefiAsync"/> or
/// <see cref="IStartupService.RestartToAdvancedStartupAsync"/> scheduled (they give a short countdown so the user can back out).
/// Implemented by both the Windows and the demo startup services.
/// </summary>
public interface IRestartCanceller
{
    /// <summary>Seconds the scheduled restart waits before it happens.</summary>
    int RestartDelaySeconds { get; }

    Task<OperationResult> CancelPendingRestartAsync(CancellationToken cancellationToken);
}
