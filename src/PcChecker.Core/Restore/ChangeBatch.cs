using PcChecker.Core.Common;

namespace PcChecker.Core.Restore;

/// <summary>
/// Runs several guarded changes as one batch behind a single restore point. Implemented by <see cref="ChangeGuard"/>:
/// while the batch's work runs, every <see cref="IChangeGuard.RunGuardedAsync"/> call made from it (also from other
/// services) skips its own restore points and records the batch's restore point in the journal instead, so a run of
/// fixes from different pages makes one restore point rather than one per page (Windows also refuses a second point
/// within a short time). Each change is still logged and journaled on its own.
/// </summary>
public interface IChangeBatchRunner
{
    /// <summary>
    /// Creates one restore point when <paramref name="createRestorePoint"/> is true, then runs <paramref name="work"/>.
    /// When that restore point cannot be created the work still runs, without the batch: each change then follows its
    /// own rules (and a change that must not run without a restore point refuses on its own). A batch started inside
    /// another batch joins it.
    /// </summary>
    Task<ChangeBatchOutcome> RunBatchAsync(string description, bool createRestorePoint, Func<CancellationToken, Task> work,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}

/// <summary>What a batch did about restore points.</summary>
/// <param name="RestorePointSequence">The restore point the whole batch shares, or null when it has none.</param>
/// <param name="RestorePointWarning">Why the batch's restore point could not be created, or null.</param>
/// <param name="Joined">True when the batch ran inside another batch and used its restore point.</param>
public sealed record ChangeBatchOutcome(long? RestorePointSequence, string? RestorePointWarning, bool Joined = false)
{
    public static ChangeBatchOutcome None { get; } = new(null, null);
}

/// <summary>The batch the current async flow runs in: its description and the restore point it shares.</summary>
public sealed record ChangeBatchState(string Description, long RestorePointSequence);

/// <summary>
/// The current batch (flows with async calls, so services called from the batch's work see it) and the pure rule for
/// how a change inside a batch treats restore points.
/// </summary>
public static class ChangeBatch
{
    private static readonly AsyncLocal<ChangeBatchState?> CurrentState = new();

    /// <summary>The batch the calling code runs in, or null.</summary>
    public static ChangeBatchState? Current => CurrentState.Value;

    /// <summary>
    /// Enters <paramref name="state"/> for the rest of the calling async method and everything it awaits. Dispose the
    /// result to leave the batch early (an async method's changes are undone when it returns anyway).
    /// </summary>
    internal static IDisposable Enter(ChangeBatchState state)
    {
        var previous = CurrentState.Value;
        CurrentState.Value = state;
        return new Scope(previous);
    }

    /// <summary>
    /// Decides the restore points for one change. Outside a batch: the options, falling back to the user's policy.
    /// Inside a batch: no restore point before or after (the batch made one), and the journal records the batch's.
    /// </summary>
    public static (bool CreateBefore, bool CreateAfter, long? SharedSequence) Resolve(ChangeOptions options, bool policyBefore, bool policyAfter,
        ChangeBatchState? batch)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (batch is not null)
        {
            return (false, false, batch.RestorePointSequence);
        }

        return (options.CreateBefore ?? policyBefore, options.CreateAfter ?? policyAfter, null);
    }

    private sealed class Scope(ChangeBatchState? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                CurrentState.Value = previous;
            }
        }
    }
}
