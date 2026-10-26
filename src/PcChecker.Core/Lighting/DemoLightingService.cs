using PcChecker.Core.Common;

namespace PcChecker.Core.Lighting;

/// <summary>Demo lighting: a motherboard, two RAM sticks, a GPU, a keyboard and a mouse, all "connected".</summary>
public sealed class DemoLightingService : ILightingService
{
    private static readonly RgbMode[] StandardModes =
    [
        new(0, "Direct", true, false, true, null, null),
        new(1, "Static", true, false, true, null, null),
        new(2, "Breathing", true, true, true, 0, 100),
        new(3, "Rainbow", false, true, true, 0, 100),
        new(4, "Off", false, false, false, null, null),
    ];

    private readonly object _gate = new();
    private readonly List<RgbDevice> _devices =
    [
        new(0, DemoData.MotherboardName, "ASUS", "Motherboard", null, 24, [new(0, "Aura header 1", 12), new(1, "Aura header 2", 12)], StandardModes, 0, new RgbColor(0x4C, 0xC2, 0xFF)),
        new(1, "G.Skill Trident Z5 RGB (slot A2)", "G.Skill", "RAM", null, 8, [new(0, "Stick", 8)], StandardModes, 0, new RgbColor(0x4C, 0xC2, 0xFF)),
        new(2, "G.Skill Trident Z5 RGB (slot B2)", "G.Skill", "RAM", null, 8, [new(0, "Stick", 8)], StandardModes, 0, new RgbColor(0x4C, 0xC2, 0xFF)),
        new(3, DemoData.GpuName, "NVIDIA", "GPU", null, 1, [new(0, "Logo", 1)], StandardModes, 1, new RgbColor(0x4C, 0xC2, 0xFF)),
        new(4, DemoData.KeyboardName, "Corsair", "Keyboard", "K70-DEMO", 104, [new(0, "Keys", 104)], StandardModes, 3, null),
        new(5, DemoData.MouseName, "Logitech", "Mouse", null, 1, [new(0, "Logo", 1)], StandardModes, 1, new RgbColor(0xA7, 0x8B, 0xFA)),
    ];

    public bool IsConnected { get; private set; } = true;
    public string Status => IsConnected ? $"Connected to OpenRGB 0.9 ({_devices.Count} devices, demo)" : "Disconnected (demo)";
    public bool IsOpenRgbInstalled => true;
    public string? OpenRgbPath => @"C:\Program Files\OpenRGB\OpenRGB.exe";
    public IReadOnlyList<LightingProfile> BuiltInProfiles => LightingProfileCatalog.BuiltIn;

    public event EventHandler? DevicesChanged;

    public async Task<OperationResult> ConnectAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        IsConnected = true;
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok(Status);
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _devices.ToList();
        }
    }

    public Task<OperationResult> SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken) =>
        Update(deviceIndex, d => d with { Color = color, ActiveModeIndex = d.Modes[d.ActiveModeIndex].HasColor ? d.ActiveModeIndex : 0 }, $"Colour set to {color.ToHex()} (demo).", cancellationToken);

    public Task<OperationResult> SetZoneColorAsync(int deviceIndex, int zoneIndex, RgbColor color, CancellationToken cancellationToken) =>
        Update(deviceIndex, d => d with { Color = color }, $"Zone {zoneIndex} colour set to {color.ToHex()} (demo).", cancellationToken);

    public Task<OperationResult> SetModeAsync(int deviceIndex, int modeIndex, RgbColor? color, int? speed, int? brightness, CancellationToken cancellationToken) =>
        Update(deviceIndex, d => d with { ActiveModeIndex = Math.Clamp(modeIndex, 0, d.Modes.Count - 1), Color = color ?? d.Color }, "Mode changed (demo).", cancellationToken);

    public async Task<OperationResult> SetAllAsync(RgbColor color, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            for (int i = 0; i < _devices.Count; i++)
            {
                _devices[i] = _devices[i] with { Color = color, ActiveModeIndex = 0 };
            }
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok($"All devices set to {color.ToHex()} (demo).");
    }

    public async Task<OperationResult> TurnOffAllAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            for (int i = 0; i < _devices.Count; i++)
            {
                _devices[i] = _devices[i] with { Color = RgbColor.Black, ActiveModeIndex = 4 };
            }
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("All lighting turned off (demo).");
    }

    public async Task<OperationResult> ApplyProfileAsync(LightingProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.AllColor is { } all)
        {
            return await SetAllAsync(all, cancellationToken).ConfigureAwait(false);
        }

        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok($"Profile \"{profile.Name}\" applied (demo).");
    }

    public async Task<OperationResult> LaunchOpenRgbServerAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("OpenRGB server is already running (demo).");
    }

    private async Task<OperationResult> Update(int deviceIndex, Func<RgbDevice, RgbDevice> change, string message, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 200).ConfigureAwait(false);
        lock (_gate)
        {
            if (deviceIndex < 0 || deviceIndex >= _devices.Count)
            {
                return OperationResult.Fail($"No lighting device with index {deviceIndex}.");
            }

            _devices[deviceIndex] = change(_devices[deviceIndex]);
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok(message);
    }
}
