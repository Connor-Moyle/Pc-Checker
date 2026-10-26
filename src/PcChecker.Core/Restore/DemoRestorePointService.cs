using PcChecker.Core.Common;

namespace PcChecker.Core.Restore;

/// <summary>Demo restore points kept in memory.</summary>
public sealed class DemoRestorePointService : IRestorePointService
{
    private readonly object _gate = new();
    private readonly List<RestorePoint> _points =
    [
        new(41, "Windows Update", DateTimeOffset.Now.AddDays(-9), "Install"),
        new(42, "PC Checker: before Disable startup item", DateTimeOffset.Now.AddDays(-2), "Modify settings"),
    ];

    private bool _enabled = true;

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(_enabled);

    public async Task<OperationResult> EnableAsync(string drive, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        _enabled = true;
        return OperationResult.Ok($"System protection enabled on {drive} (demo).");
    }

    public async Task<IReadOnlyList<RestorePoint>> ListAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 250).ConfigureAwait(false);
        lock (_gate)
        {
            return _points.OrderByDescending(p => p.SequenceNumber).ToList();
        }
    }

    public async Task<OperationResult<RestorePoint>> CreateAsync(string description, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 700).ConfigureAwait(false);
        RestorePoint point;
        lock (_gate)
        {
            point = new RestorePoint(_points.Max(p => p.SequenceNumber) + 1, description, DateTimeOffset.Now, "Modify settings");
            _points.Add(point);
        }

        return OperationResult<RestorePoint>.Ok(point, $"Restore point {point.SequenceNumber} created (demo).");
    }
}
