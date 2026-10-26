// TODO(restore): restore module — SystemRestore WMI class, Enable-ComputerRestore, frequency registry key.
using PcChecker.Core.Common;

namespace PcChecker.Core.Restore;

/// <summary>Windows implementation of <see cref="IRestorePointService"/>.</summary>
public sealed class WindowsRestorePointService : IRestorePointService
{
    private const string NotImplemented = "Restore points are not implemented yet.";

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<OperationResult> EnableAsync(string drive, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<IReadOnlyList<RestorePoint>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RestorePoint>>([]);

    public Task<OperationResult<RestorePoint>> CreateAsync(string description, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<RestorePoint>.Fail(NotImplemented));
}
