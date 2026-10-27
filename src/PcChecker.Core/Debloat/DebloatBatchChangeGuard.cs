using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Debloat;

/// <summary>
/// Change guard for the app and Windows feature removals that run inside a Debloat batch. The batch itself is one
/// guarded change whose restore point is created before anything in it, so the individual removals skip their own
/// restore point (no one-per-app restore points, and no removal held back by Windows' restore point frequency limit)
/// but are still journaled and logged by the real guard.
/// </summary>
public sealed class DebloatBatchChangeGuard : IChangeGuard
{
    private readonly IChangeGuard _inner;

    public DebloatBatchChangeGuard(IChangeGuard inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>The options the inner guard receives: no restore point before or after, never aborted for lack of one.</summary>
    public static ChangeOptions WithoutRestorePoint(ChangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with { CreateBefore = false, CreateAfter = false, OnRestorePointFailed = RestorePointFailedBehavior.Continue };
    }

    public Task<OperationResult<T>> RunGuardedAsync<T>(string description, ChangeOptions options,
        Func<CancellationToken, Task<OperationResult<T>>> change, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        _inner.RunGuardedAsync(description, WithoutRestorePoint(options), change, progress, cancellationToken);

    public Task<OperationResult> RunGuardedAsync(string description, ChangeOptions options,
        Func<CancellationToken, Task<OperationResult>> change, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        _inner.RunGuardedAsync(description, WithoutRestorePoint(options), change, progress, cancellationToken);
}
