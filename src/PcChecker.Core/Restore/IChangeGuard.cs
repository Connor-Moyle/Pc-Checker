using PcChecker.Core.Common;

namespace PcChecker.Core.Restore;

/// <summary>
/// Wraps every system-changing operation: creates restore points per settings, records the action in the journal and logs it.
/// </summary>
public interface IChangeGuard
{
    Task<OperationResult<T>> RunGuardedAsync<T>(string description, ChangeOptions options,
        Func<CancellationToken, Task<OperationResult<T>>> change, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    Task<OperationResult> RunGuardedAsync(string description, ChangeOptions options,
        Func<CancellationToken, Task<OperationResult>> change, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}
