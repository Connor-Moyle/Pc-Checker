using PcChecker.Core.Common;

namespace PcChecker.Core.Lighting;

/// <summary>RGB lighting for motherboards, RAM, GPUs, coolers and peripherals through the OpenRGB SDK server.</summary>
public interface ILightingService
{
    bool IsConnected { get; }
    string Status { get; }
    bool IsOpenRgbInstalled { get; }
    string? OpenRgbPath { get; }
    Task<OperationResult> ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync();
    Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken cancellationToken);
    Task<OperationResult> SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken);
    Task<OperationResult> SetZoneColorAsync(int deviceIndex, int zoneIndex, RgbColor color, CancellationToken cancellationToken);
    Task<OperationResult> SetModeAsync(int deviceIndex, int modeIndex, RgbColor? color, int? speed, int? brightness, CancellationToken cancellationToken);
    Task<OperationResult> SetAllAsync(RgbColor color, CancellationToken cancellationToken);
    Task<OperationResult> TurnOffAllAsync(CancellationToken cancellationToken);
    Task<OperationResult> ApplyProfileAsync(LightingProfile profile, CancellationToken cancellationToken);
    Task<OperationResult> LaunchOpenRgbServerAsync(CancellationToken cancellationToken);
    IReadOnlyList<LightingProfile> BuiltInProfiles { get; }
    event EventHandler? DevicesChanged;
}
