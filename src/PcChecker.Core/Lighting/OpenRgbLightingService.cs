// TODO(lighting): lighting module — OpenRGB.NET client, install detection, server launch, device mapping, profiles.
using PcChecker.Core.Common;

namespace PcChecker.Core.Lighting;

/// <summary>RGB control through an OpenRGB SDK server (OpenRGB.NET).</summary>
public sealed class OpenRgbLightingService : ILightingService
{
    private const string NotImplemented = "Lighting control is not implemented yet.";

    public bool IsConnected => false;
    public string Status => NotImplemented;
    public bool IsOpenRgbInstalled => false;
    public string? OpenRgbPath => null;
    public IReadOnlyList<LightingProfile> BuiltInProfiles => LightingProfileCatalog.BuiltIn;

    public event EventHandler? DevicesChanged;

    public Task<OperationResult> ConnectAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task DisconnectAsync() => Task.CompletedTask;
    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RgbDevice>>([]);
    public Task<OperationResult> SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetZoneColorAsync(int deviceIndex, int zoneIndex, RgbColor color, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetModeAsync(int deviceIndex, int modeIndex, RgbColor? color, int? speed, int? brightness, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> SetAllAsync(RgbColor color, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> TurnOffAllAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> ApplyProfileAsync(LightingProfile profile, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));
    public Task<OperationResult> LaunchOpenRgbServerAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Fail(NotImplemented));

    internal void RaiseForCompiler() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}
