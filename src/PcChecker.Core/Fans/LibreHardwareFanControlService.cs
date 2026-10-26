// TODO(fans): fans module — enumerate SensorType.Control via IHardwareMonitorHost, curves loop, profiles, watchdog.
using PcChecker.Core.Common;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.Fans;

/// <summary>Fan control through the shared LibreHardwareMonitor host.</summary>
public sealed class LibreHardwareFanControlService : IFanControlService
{
    private const string NotImplemented = "Fan control is not implemented yet.";
    private readonly IHardwareMonitorHost _host;

    public LibreHardwareFanControlService(IHardwareMonitorHost host)
    {
        _host = host;
    }

    public bool IsAvailable => false;
    public string? UnavailableReason => _host.IsOpen ? NotImplemented : (_host.OpenError ?? NotImplemented);
    public FanProfile? ActiveProfile => null;
    public IReadOnlyList<FanProfile> BuiltInProfiles => FanCurveMath.BuiltInProfiles("cpu.package", "gpu.core");

    public event EventHandler<IReadOnlyList<FanDevice>>? FansUpdated;

    public Task<IReadOnlyList<FanDevice>> GetFansAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FanDevice>>([]);

    public Task<OperationResult> SetManualAsync(string fanId, double percent, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> SetAutoAsync(string fanId, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> ApplyProfileAsync(FanProfile profile, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Fail(NotImplemented));

    public Task<OperationResult> RevertAllToAutoAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Ok("No fans are under software control."));

    internal void RaiseForCompiler() => FansUpdated?.Invoke(this, []);
}
