using PcChecker.Core.Common;

namespace PcChecker.Core.Restore;

/// <summary>System Restore: query, enable, list and create restore points.</summary>
public interface IRestorePointService
{
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken);
    Task<OperationResult> EnableAsync(string drive, CancellationToken cancellationToken);
    Task<IReadOnlyList<RestorePoint>> ListAsync(CancellationToken cancellationToken);
    Task<OperationResult<RestorePoint>> CreateAsync(string description, CancellationToken cancellationToken);
}
