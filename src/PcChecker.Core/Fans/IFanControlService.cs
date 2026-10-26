using PcChecker.Core.Common;

namespace PcChecker.Core.Fans;

/// <summary>Reads fan speeds and, where the hardware allows it, sets manual speeds, curves and profiles.</summary>
public interface IFanControlService
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    Task<IReadOnlyList<FanDevice>> GetFansAsync(CancellationToken cancellationToken);
    event EventHandler<IReadOnlyList<FanDevice>>? FansUpdated;
    Task<OperationResult> SetManualAsync(string fanId, double percent, CancellationToken cancellationToken);
    Task<OperationResult> SetAutoAsync(string fanId, CancellationToken cancellationToken);
    Task<OperationResult> ApplyProfileAsync(FanProfile profile, CancellationToken cancellationToken);
    FanProfile? ActiveProfile { get; }
    Task<OperationResult> RevertAllToAutoAsync(CancellationToken cancellationToken);
    IReadOnlyList<FanProfile> BuiltInProfiles { get; }
}
