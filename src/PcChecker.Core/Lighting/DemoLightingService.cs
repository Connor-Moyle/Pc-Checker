using PcChecker.Core.Common;

namespace PcChecker.Core.Lighting;

/// <summary>
/// Demo lighting for the sample gaming desktop: a motherboard with two headers, two RAM sticks, a GPU, an AIO
/// cooler, a keyboard, a mouse and a headset — all "connected" to a pretend OpenRGB server. Pure .NET; every change
/// is remembered so the page reflects what was applied.
/// </summary>
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

    private static readonly RgbMode[] KeyboardModes =
    [
        new(0, "Direct", true, false, true, null, null),
        new(1, "Static", true, false, true, null, null),
        new(2, "Breathing", true, true, true, 0, 100),
        new(3, "Rainbow wave", false, true, true, 0, 100),
        new(4, "Reactive (key press)", true, true, true, 0, 100),
        new(5, "Off", false, false, false, null, null),
    ];

    private static readonly RgbColor DefaultBlue = new(0x4C, 0xC2, 0xFF);

    private readonly object _gate = new();
    private readonly List<RgbDevice> _devices =
    [
        new(0, DemoData.MotherboardName, "ASUS", OpenRgbDeviceTypes.Motherboard, null, 24, [new(0, "Aura header 1", 12), new(1, "Aura header 2", 12)], StandardModes, 0, DefaultBlue),
        new(1, "G.Skill Trident Z5 RGB (slot A2)", "G.Skill", OpenRgbDeviceTypes.Ram, null, 8, [new(0, "Stick", 8)], StandardModes, 0, DefaultBlue),
        new(2, "G.Skill Trident Z5 RGB (slot B2)", "G.Skill", OpenRgbDeviceTypes.Ram, null, 8, [new(0, "Stick", 8)], StandardModes, 0, DefaultBlue),
        new(3, DemoData.GpuName, "NVIDIA", OpenRgbDeviceTypes.Gpu, null, 1, [new(0, "Logo", 1)], StandardModes, 1, DefaultBlue),
        new(4, DemoData.KeyboardName, "Corsair", OpenRgbDeviceTypes.Keyboard, "K70-DEMO", 104, [new(0, "Keys", 104)], KeyboardModes, 3, null),
        new(5, DemoData.MouseName, "Logitech", OpenRgbDeviceTypes.Mouse, null, 1, [new(0, "Logo", 1)], StandardModes, 1, new RgbColor(0xA7, 0x8B, 0xFA)),
    ];

    private readonly Dictionary<int, (int Brightness, int Speed)> _levels = [];
    private readonly List<(DateTimeOffset Time, string Action)> _history = [];

    public bool IsConnected { get; private set; } = true;

    public string Status => IsConnected
        ? LightingStatusText.Connected("0.9 (demo)", _devices.Count)
        : LightingStatusText.Disconnected + " (demo)";

    public bool IsOpenRgbInstalled => true;

    public string? OpenRgbPath => @"C:\Program Files\OpenRGB\OpenRGB.exe";

    public IReadOnlyList<LightingProfile> BuiltInProfiles => LightingProfileCatalog.BuiltIn;

    /// <summary>What the demo applied so far (for tests and the page's "last change" line).</summary>
    public IReadOnlyList<(DateTimeOffset Time, string Action)> History
    {
        get
        {
            lock (_gate)
            {
                return _history.ToList();
            }
        }
    }

    public event EventHandler? DevicesChanged;

    public async Task<OperationResult> ConnectAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 200, 600).ConfigureAwait(false);
        IsConnected = true;
        Remember("Connected");
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok(Status);
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        Remember("Disconnected");
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 100, 300).ConfigureAwait(false);
        if (!IsConnected)
        {
            return [];
        }

        lock (_gate)
        {
            return _devices.ToList();
        }
    }

    /// <summary>Brightness and speed (0–100) last applied to a device; 100/50 when nothing was set.</summary>
    public (int Brightness, int Speed) GetLevels(int deviceIndex)
    {
        lock (_gate)
        {
            return _levels.TryGetValue(deviceIndex, out var l) ? l : (100, 50);
        }
    }

    public Task<OperationResult> SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken) =>
        UpdateAsync(deviceIndex, d =>
        {
            var target = LightingApplyPlanner.PickColorModeIndex(d.Modes, d.ActiveModeIndex) ?? d.ActiveModeIndex;
            return d with { Color = color, ActiveModeIndex = target };
        }, d => $"{d.Name} set to {color.ToHex()}.", cancellationToken);

    public Task<OperationResult> SetZoneColorAsync(int deviceIndex, int zoneIndex, RgbColor color, CancellationToken cancellationToken) =>
        UpdateAsync(deviceIndex, d =>
        {
            if (zoneIndex < 0 || zoneIndex >= d.Zones.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(zoneIndex), $"{d.Name} has no zone {zoneIndex}.");
            }

            var target = LightingApplyPlanner.PickColorModeIndex(d.Modes, d.ActiveModeIndex) ?? d.ActiveModeIndex;
            return d with { Color = color, ActiveModeIndex = target };
        }, d => $"{d.Zones[zoneIndex].Name} on {d.Name} set to {color.ToHex()}.", cancellationToken);

    public Task<OperationResult> SetModeAsync(int deviceIndex, int modeIndex, RgbColor? color, int? speed, int? brightness, CancellationToken cancellationToken) =>
        UpdateAsync(deviceIndex, d =>
        {
            int idx = Math.Clamp(modeIndex, 0, d.Modes.Count - 1);
            var mode = d.Modes[idx];
            lock (_gate)
            {
                var current = _levels.TryGetValue(d.Index, out var l) ? l : (Brightness: 100, Speed: 50);
                _levels[d.Index] = (
                    brightness is { } b && mode.HasBrightness ? Math.Clamp(b, 0, 100) : current.Brightness,
                    speed is { } s && mode.HasSpeed ? LightingApplyPlanner.ClampSpeed(mode, s) : current.Speed);
            }

            return d with { ActiveModeIndex = idx, Color = mode.HasColor ? color ?? d.Color : d.Color };
        }, d => $"{d.Name}: mode \"{d.Modes[d.ActiveModeIndex].Name}\" applied.", cancellationToken);

    public async Task<OperationResult> SetAllAsync(RgbColor color, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 150, 400).ConfigureAwait(false);
        lock (_gate)
        {
            for (int i = 0; i < _devices.Count; i++)
            {
                var d = _devices[i];
                var target = LightingApplyPlanner.PickColorModeIndex(d.Modes, d.ActiveModeIndex) ?? d.ActiveModeIndex;
                _devices[i] = d with { Color = color, ActiveModeIndex = target };
            }
        }

        Remember($"All devices → {color.ToHex()}");
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok($"All {_devices.Count} devices set to {color.ToHex()}.");
    }

    public async Task<OperationResult> TurnOffAllAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 150, 400).ConfigureAwait(false);
        lock (_gate)
        {
            for (int i = 0; i < _devices.Count; i++)
            {
                var d = _devices[i];
                int off = LightingApplyPlanner.FindOffModeIndex(d.Modes) ?? d.ActiveModeIndex;
                _devices[i] = d with { Color = RgbColor.Black, ActiveModeIndex = off };
            }
        }

        Remember("All devices → off");
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("All lighting turned off.");
    }

    public async Task<OperationResult> ApplyProfileAsync(LightingProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await DemoData.DelayAsync(cancellationToken, 200, 500).ConfigureAwait(false);
        lock (_gate)
        {
            for (int i = 0; i < _devices.Count; i++)
            {
                var d = _devices[i];
                var assignment = LightingApplyPlanner.ResolveAssignment(profile, d);
                if (assignment is null)
                {
                    if (profile.AllColor is { } all)
                    {
                        int target = all == RgbColor.Black
                            ? LightingApplyPlanner.FindOffModeIndex(d.Modes) ?? d.ActiveModeIndex
                            : LightingApplyPlanner.PickColorModeIndex(d.Modes, d.ActiveModeIndex) ?? d.ActiveModeIndex;
                        _devices[i] = d with { Color = all, ActiveModeIndex = target };
                    }

                    continue;
                }

                int? modeIndex = LightingApplyPlanner.FindModeIndex(d.Modes, assignment.ModeName)
                    ?? (assignment.ModeName?.Contains("rainbow", StringComparison.OrdinalIgnoreCase) == true
                        ? d.Modes.FirstOrDefault(m => m.Name.Contains("rainbow", StringComparison.OrdinalIgnoreCase))?.Index
                        : null);
                var color = assignment.Color ?? profile.AllColor;
                int active = modeIndex ?? (color is null ? d.ActiveModeIndex : LightingApplyPlanner.PickColorModeIndex(d.Modes, d.ActiveModeIndex) ?? d.ActiveModeIndex);
                var mode = d.Modes[active];
                _devices[i] = d with
                {
                    ActiveModeIndex = active,
                    Color = mode.HasColor && color is { } c ? LightingApplyPlanner.EffectiveColor(c, assignment.Brightness) : d.Color,
                };
                var current = _levels.TryGetValue(d.Index, out var l) ? l : (Brightness: 100, Speed: 50);
                _levels[d.Index] = (assignment.Brightness ?? current.Brightness, assignment.Speed is { } s ? LightingApplyPlanner.ClampSpeed(mode, s) : current.Speed);
            }
        }

        Remember($"Profile \"{profile.Name}\" applied");
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok($"Profile \"{profile.Name}\" applied.");
    }

    public async Task<OperationResult> LaunchOpenRgbServerAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 600).ConfigureAwait(false);
        if (!IsConnected)
        {
            return await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        return OperationResult.Ok("OpenRGB's SDK server is already running (demo).");
    }

    private async Task<OperationResult> UpdateAsync(int deviceIndex, Func<RgbDevice, RgbDevice> change, Func<RgbDevice, string> message, CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 50, 200).ConfigureAwait(false);
        if (!IsConnected)
        {
            return OperationResult.Fail(Status);
        }

        RgbDevice updated;
        lock (_gate)
        {
            int slot = _devices.FindIndex(d => d.Index == deviceIndex);
            if (slot < 0)
            {
                return OperationResult.Fail($"No lighting device with index {deviceIndex}.");
            }

            try
            {
                updated = change(_devices[slot]);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return OperationResult.Fail(ex.Message, ex);
            }

            _devices[slot] = updated;
        }

        var text = message(updated);
        Remember(text);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok(text);
    }

    private void Remember(string action)
    {
        lock (_gate)
        {
            _history.Add((DateTimeOffset.Now, action));
            if (_history.Count > 50)
            {
                _history.RemoveAt(0);
            }
        }
    }
}
