using PcChecker.Core.Common;

namespace PcChecker.Core.Lighting;

/// <summary>
/// Demo lighting for the sample gaming desktop: OpenRGB controls the motherboard (with a resizable addressable
/// header), the graphics card and the keyboard; the Corsair cooler hub and RAM are found by the detection pass but held
/// by iCUE (see <see cref="DemoRgbHardwareService"/>). Pure .NET; every change is remembered so the page reflects
/// what was applied.
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
        new(0, DemoData.MotherboardName, "ASUS", OpenRgbDeviceTypes.Motherboard, null, 32,
            [new(0, "Aura Mainboard", 8, 8, 8), new(1, "Addressable header 1", 24, 0, 120)], StandardModes, 0, DefaultBlue,
            @"HID: \\?\HID#VID_0B05&PID_19AF&MI_02#8&2d4bd25e&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"),
        new(1, DemoData.GpuName, "NVIDIA", OpenRgbDeviceTypes.Gpu, null, 1, [new(0, "Logo", 1, 1, 1)], StandardModes, 1, DefaultBlue,
            "I2C: NVIDIA GPU, address 0x49"),
        new(2, DemoData.KeyboardName, "Corsair", OpenRgbDeviceTypes.Keyboard, "K70-DEMO", 104, [new(0, "Keys", 104, 104, 104)], KeyboardModes, 3, null,
            @"HID: \\?\HID#VID_1B1C&PID_1BC4&MI_01#8&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"),
    ];

    private readonly Dictionary<int, (int Brightness, int Speed)> _levels = [];
    private readonly List<(DateTimeOffset Time, string Action)> _history = [];

    public bool IsConnected { get; private set; } = true;

    public string Status => IsConnected
        ? LightingStatusText.Connected("0.9 (demo)", _devices.Count)
        : LightingStatusText.Disconnected + " (demo)";

    public bool IsOpenRgbInstalled => true;

    public string? OpenRgbPath => @"C:\Program Files\OpenRGB\OpenRGB.exe";

    public string ServerAddress => "127.0.0.1:6742";

    public OpenRgbAvailability Availability => IsConnected ? OpenRgbAvailability.Connected : OpenRgbAvailability.InstalledNotRunning;

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
        RaiseDevicesChanged();
        return OperationResult.Ok(Status);
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        Remember("Disconnected");
        RaiseDevicesChanged();
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

    public Task<OperationResult> ResizeZoneAsync(int deviceIndex, int zoneIndex, int ledCount, CancellationToken cancellationToken)
    {
        int applied = 0;
        return UpdateAsync(deviceIndex, d =>
        {
            if (zoneIndex < 0 || zoneIndex >= d.Zones.Count || !d.Zones[zoneIndex].IsResizable)
            {
                throw new ArgumentOutOfRangeException(nameof(zoneIndex), $"{d.Name} has no resizable zone {zoneIndex}.");
            }

            var zone = d.Zones[zoneIndex];
            applied = LightingApplyPlanner.ClampZoneSize(zone, ledCount);
            var zones = d.Zones.ToList();
            zones[zoneIndex] = zone with { LedCount = applied };
            return d with { Zones = zones, LedCount = zones.Sum(z => z.LedCount) };
        }, d => $"{d.Zones[zoneIndex].Name} on {d.Name} now has {applied} LEDs.", cancellationToken);
    }

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
        RaiseDevicesChanged();
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
        RaiseDevicesChanged();
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

                int? modeIndex = LightingApplyPlanner.ResolveModeIndex(d.Modes, assignment.ModeName);
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
        RaiseDevicesChanged();
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
        RaiseDevicesChanged();
        return OperationResult.Ok(text);
    }

    private void RaiseDevicesChanged()
    {
        try
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A subscriber's failure must not fail the change that was already applied.
        }
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
