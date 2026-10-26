using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using OpenRGB.NET;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Lighting;

/// <summary>
/// RGB control through an OpenRGB SDK server (OpenRGB.NET). Lighting is cosmetic, so changes are logged and
/// journaled but do not create restore points. Every server call is guarded: a dropped socket marks the service
/// disconnected and a reconnect is attempted with backoff.
/// </summary>
public sealed class OpenRgbLightingService : ILightingService, IDisposable
{
    private const string Module = "Lighting";
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromSeconds(15);

    private readonly ISettingsService _settings;
    private readonly IActionJournal _journal;
    private readonly ILogger<OpenRgbLightingService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lazy<string?> _installPath;

    private OpenRgbClient? _client;
    private Device[] _raw = [];
    private readonly Dictionary<int, int> _activeModeOverride = [];
    private List<RgbDevice> _devices = [];
    private string? _lastError;
    private int _reconnectAttempts;
    private bool _disposed;

    public OpenRgbLightingService(ISettingsService settings, IActionJournal journal, ILogger<OpenRgbLightingService> logger)
    {
        _settings = settings;
        _journal = journal;
        _logger = logger;
        _installPath = new Lazy<string?>(OpenRgbInstallLocator.Find, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsConnected => _client?.Connected == true;

    public string Status => LightingStatusText.For(IsConnected, IsOpenRgbInstalled, ProtocolVersionText, _devices.Count, _lastError);

    public bool IsOpenRgbInstalled => OpenRgbPath is not null;

    public string? OpenRgbPath => SafeInstallPath();

    public IReadOnlyList<LightingProfile> BuiltInProfiles => LightingProfileCatalog.BuiltIn;

    public event EventHandler? DevicesChanged;

    private string? ProtocolVersionText
    {
        get
        {
            try
            {
                return _client is { Connected: true } c ? $"(SDK protocol {c.CommonProtocolVersion.Number})" : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<OperationResult> ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ConnectCore(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private OperationResult ConnectCore()
    {
        var lighting = _settings.Current.Lighting;
        DisposeClient();
        try
        {
            var client = new OpenRgbClient(lighting.OpenRgbHost, lighting.OpenRgbPort, "PC Checker", autoConnect: false, timeoutMs: 3000);
            client.Connect();
            _client = client;
            _raw = client.GetAllControllerData() ?? [];
            _devices = _raw.Select(OpenRgbDeviceMapper.Map).ToList();
            _lastError = null;
            _reconnectAttempts = 0;
            _logger.LogInformation("Connected to OpenRGB at {Host}:{Port}; {Count} devices.", lighting.OpenRgbHost, lighting.OpenRgbPort, _devices.Count);
            RaiseDevicesChanged();
            return OperationResult.Ok(Status);
        }
        catch (Exception ex)
        {
            DisposeClient();
            _lastError = ex is SocketException or IOException or TimeoutException ? null : ex.Message;
            _logger.LogWarning(ex, "Could not connect to OpenRGB at {Host}:{Port}.", lighting.OpenRgbHost, lighting.OpenRgbPort);
            RaiseDevicesChanged();
            return OperationResult.Fail(Status, ex);
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeClient();
            _devices = [];
            _raw = [];
            _lastError = null;
            _logger.LogInformation("Disconnected from OpenRGB.");
        }
        finally
        {
            _gate.Release();
        }

        RaiseDevicesChanged();
    }

    public async Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not { Connected: true } client)
            {
                return _devices.ToList();
            }

            return await Task.Run(() =>
            {
                try
                {
                    _raw = client.GetAllControllerData() ?? [];
                    _devices = _raw.Select(OpenRgbDeviceMapper.Map).ToList();
                }
                catch (Exception ex)
                {
                    HandleDrop(ex);
                }

                return (IReadOnlyList<RgbDevice>)_devices.ToList();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<OperationResult> SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken) =>
        RunAsync(client => SetDeviceColor(client, deviceIndex, color), cancellationToken);

    public Task<OperationResult> SetZoneColorAsync(int deviceIndex, int zoneIndex, RgbColor color, CancellationToken cancellationToken) =>
        RunAsync(client =>
        {
            var raw = Raw(deviceIndex);
            if (raw is null)
            {
                return OperationResult.Fail($"No lighting device with index {deviceIndex}.");
            }

            if (zoneIndex < 0 || zoneIndex >= (raw.Zones?.Length ?? 0))
            {
                return OperationResult.Fail($"{raw.Name} has no zone {zoneIndex}.");
            }

            var ensure = EnsureColorMode(client, raw, requirePerLed: true);
            if (!ensure.Success)
            {
                return ensure;
            }

            int ledCount = (int)raw.Zones![zoneIndex].LedCount;
            client.UpdateZoneLeds(deviceIndex, zoneIndex, OpenRgbDeviceMapper.ToOpenRgb(LightingApplyPlanner.Fill(ledCount, color)));
            return OperationResult.Ok($"{raw.Zones[zoneIndex].Name} set to {color.ToHex()}.");
        }, cancellationToken);

    public Task<OperationResult> SetModeAsync(int deviceIndex, int modeIndex, RgbColor? color, int? speed, int? brightness, CancellationToken cancellationToken) =>
        RunAsync(client => SetMode(client, deviceIndex, modeIndex, color, speed, brightness), cancellationToken);

    public async Task<OperationResult> SetAllAsync(RgbColor color, CancellationToken cancellationToken)
    {
        var result = await RunAsync(client => ForEachDevice(client, d => SetDeviceColor(client, d.Index, color)), cancellationToken).ConfigureAwait(false);
        await JournalAsync("Set all", "All devices", result, after: color.ToHex()).ConfigureAwait(false);
        return result;
    }

    public async Task<OperationResult> TurnOffAllAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(client => ForEachDevice(client, d =>
        {
            var modes = _devices.FirstOrDefault(x => x.Index == d.Index)?.Modes ?? [];
            var off = LightingApplyPlanner.FindOffModeIndex(modes);
            return off is { } idx
                ? SetMode(client, d.Index, idx, null, null, null)
                : SetDeviceColor(client, d.Index, RgbColor.Black);
        }), cancellationToken).ConfigureAwait(false);
        await JournalAsync("Turn off", "All devices", result, after: "Off").ConfigureAwait(false);
        return result;
    }

    public async Task<OperationResult> ApplyProfileAsync(LightingProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var result = await RunAsync(client => ForEachDevice(client, raw =>
        {
            var device = _devices.FirstOrDefault(x => x.Index == raw.Index);
            if (device is null)
            {
                return OperationResult.Ok();
            }

            var assignment = LightingApplyPlanner.ResolveAssignment(profile, device);
            if (assignment is null)
            {
                if (profile.AllColor is not { } all)
                {
                    return OperationResult.Ok();
                }

                // "Night (off)": prefer the device's real Off mode over painting it black.
                if (all == RgbColor.Black && LightingApplyPlanner.FindOffModeIndex(device.Modes) is { } off)
                {
                    return SetMode(client, device.Index, off, null, null, null);
                }

                return SetDeviceColor(client, device.Index, all);
            }

            var modeIndex = LightingApplyPlanner.FindModeIndex(device.Modes, assignment.ModeName);
            var color = assignment.Color ?? profile.AllColor;
            if (modeIndex is { } m)
            {
                return SetMode(client, device.Index, m, color, assignment.Speed, assignment.Brightness);
            }

            if (assignment.ModeName?.Equals("Rainbow", StringComparison.OrdinalIgnoreCase) == true)
            {
                // No rainbow effect on this device: paint a static rainbow across its LEDs instead.
                return SetDeviceColors(client, device.Index, LightingApplyPlanner.RainbowFill(device.LedCount));
            }

            if (color is { } c)
            {
                return SetDeviceColor(client, device.Index, LightingApplyPlanner.EffectiveColor(c, assignment.Brightness));
            }

            return OperationResult.Ok();
        }), cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            _settings.Current.Lighting.ActiveProfileName = profile.Name;
        }

        await JournalAsync("Apply profile", profile.Name, result, after: profile.AllColor?.ToHex()).ConfigureAwait(false);
        return result.Success ? OperationResult.Ok($"Profile \"{profile.Name}\" applied.") : result;
    }

    public async Task<OperationResult> LaunchOpenRgbServerAsync(CancellationToken cancellationToken)
    {
        var lighting = _settings.Current.Lighting;
        if (await OpenRgbInstallLocator.IsPortOpenAsync(lighting.OpenRgbHost, lighting.OpenRgbPort, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
        {
            return await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        var path = OpenRgbPath;
        if (path is null)
        {
            return OperationResult.Fail(LightingStatusText.NotInstalled);
        }

        var start = OpenRgbInstallLocator.StartServer(path, lighting.OpenRgbPort);
        if (!start.Success)
        {
            return start;
        }

        _logger.LogInformation("Started OpenRGB server from {Path}; waiting for port {Port}.", path, lighting.OpenRgbPort);
        var deadline = DateTimeOffset.UtcNow + ServerStartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await OpenRgbInstallLocator.IsPortOpenAsync(lighting.OpenRgbHost, lighting.OpenRgbPort, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                return await ConnectAsync(cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }

        return OperationResult.Fail("OpenRGB started but its SDK server did not answer within 15 seconds. Open OpenRGB, go to the SDK Server tab and press Start Server.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeClient();
        _gate.Dispose();
    }

    // ---- internals -------------------------------------------------------------------------------------------

    private async Task<OperationResult> RunAsync(Func<OpenRgbClient, OperationResult> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not { Connected: true })
            {
                var reconnect = await TryReconnectAsync(cancellationToken).ConfigureAwait(false);
                if (!reconnect)
                {
                    return OperationResult.Fail(Status);
                }
            }

            var client = _client!;
            return await Task.Run(() =>
            {
                try
                {
                    return action(client);
                }
                catch (Exception ex)
                {
                    HandleDrop(ex);
                    return OperationResult.Fail($"OpenRGB did not accept the change: {ex.Message}", ex);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Called with the gate held. Reconnects with exponential backoff up to five attempts.</summary>
    private async Task<bool> TryReconnectAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Current.Lighting.AutoConnect || _reconnectAttempts >= 5)
        {
            return false;
        }

        int delayMs = Math.Min(8000, 500 * (1 << _reconnectAttempts));
        _reconnectAttempts++;
        _logger.LogInformation("Reconnecting to OpenRGB (attempt {Attempt}) after {Delay} ms.", _reconnectAttempts, delayMs);
        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        var attempts = _reconnectAttempts;
        var result = await Task.Run(() => ConnectCore(), cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            _reconnectAttempts = attempts;
        }

        return result.Success;
    }

    private void HandleDrop(Exception ex)
    {
        if (ex is SocketException or IOException or ObjectDisposedException or TimeoutException)
        {
            _logger.LogWarning(ex, "OpenRGB connection dropped.");
            DisposeClient();
            RaiseDevicesChanged();
        }
        else
        {
            _logger.LogWarning(ex, "OpenRGB call failed.");
        }
    }

    private OperationResult ForEachDevice(OpenRgbClient client, Func<Device, OperationResult> perDevice)
    {
        if (_raw.Length == 0)
        {
            _raw = client.GetAllControllerData() ?? [];
            _devices = _raw.Select(OpenRgbDeviceMapper.Map).ToList();
        }

        var failures = new List<string>();
        foreach (var raw in _raw)
        {
            var r = perDevice(raw);
            if (!r.Success)
            {
                failures.Add($"{raw.Name}: {r.Message}");
            }
        }

        RefreshCache(client);
        return failures.Count == 0
            ? OperationResult.Ok($"Applied to {_raw.Length} device(s).")
            : OperationResult.Fail($"{failures.Count} of {_raw.Length} devices did not accept the change: {string.Join("; ", failures)}");
    }

    private OperationResult SetDeviceColor(OpenRgbClient client, int deviceIndex, RgbColor color)
    {
        var raw = Raw(deviceIndex);
        if (raw is null)
        {
            return OperationResult.Fail($"No lighting device with index {deviceIndex}.");
        }

        var ensure = EnsureColorMode(client, raw, requirePerLed: false);
        if (!ensure.Success)
        {
            return ensure;
        }

        var active = raw.Modes is { Length: > 0 } modes && ActiveModeOf(raw) >= 0 && ActiveModeOf(raw) < modes.Length ? modes[ActiveModeOf(raw)] : null;
        int ledCount = raw.Leds?.Length ?? raw.Colors?.Length ?? 0;
        if (active is not null && !OpenRgbDeviceMapper.IsPerLed(active))
        {
            // Mode-specific colour: resend the mode with the new colour.
            client.UpdateMode(deviceIndex, active.Index, null, null, [OpenRgbDeviceMapper.ToOpenRgb(color)]);
        }
        else
        {
            client.UpdateLeds(deviceIndex, OpenRgbDeviceMapper.ToOpenRgb(LightingApplyPlanner.Fill(ledCount, color)));
        }

        return OperationResult.Ok($"{raw.Name} set to {color.ToHex()}.");
    }

    private OperationResult SetDeviceColors(OpenRgbClient client, int deviceIndex, IReadOnlyList<RgbColor> colors)
    {
        var raw = Raw(deviceIndex);
        if (raw is null)
        {
            return OperationResult.Fail($"No lighting device with index {deviceIndex}.");
        }

        var ensure = EnsureColorMode(client, raw, requirePerLed: true);
        if (!ensure.Success)
        {
            return ensure;
        }

        client.UpdateLeds(deviceIndex, OpenRgbDeviceMapper.ToOpenRgb(colors));
        return OperationResult.Ok($"{raw.Name} painted with {colors.Count} colours.");
    }

    private OperationResult SetMode(OpenRgbClient client, int deviceIndex, int modeIndex, RgbColor? color, int? speed, int? brightness)
    {
        var raw = Raw(deviceIndex);
        if (raw is null)
        {
            return OperationResult.Fail($"No lighting device with index {deviceIndex}.");
        }

        if (raw.Modes is null || modeIndex < 0 || modeIndex >= raw.Modes.Length)
        {
            return OperationResult.Fail($"{raw.Name} has no mode {modeIndex}.");
        }

        var mode = raw.Modes[modeIndex];
        var mapped = OpenRgbDeviceMapper.MapMode(mode);
        uint? speedValue = speed is { } s && mapped.HasSpeed ? (uint)LightingApplyPlanner.ClampSpeed(mapped, s) : null;
        var effective = color is { } c ? LightingApplyPlanner.EffectiveColor(c, brightness) : (RgbColor?)null;

        Color[] modeColors = mode.Colors ?? [];
        if (effective is { } ec && mapped.HasColor && !OpenRgbDeviceMapper.IsPerLed(mode))
        {
            modeColors = [OpenRgbDeviceMapper.ToOpenRgb(ec)];
        }

        client.UpdateMode(deviceIndex, modeIndex, speedValue, null, modeColors);
        _activeModeOverride[raw.Index] = modeIndex;

        if (effective is { } led && OpenRgbDeviceMapper.IsPerLed(mode))
        {
            int ledCount = raw.Leds?.Length ?? raw.Colors?.Length ?? 0;
            client.UpdateLeds(deviceIndex, OpenRgbDeviceMapper.ToOpenRgb(LightingApplyPlanner.Fill(ledCount, led)));
        }

        string note = brightness is { } b && b < 100 && !OpenRgbDeviceMapper.IsPerLed(mode) && !mapped.HasColor
            ? " Brightness is applied by dimming the colour, which this effect does not use."
            : string.Empty;
        return OperationResult.Ok($"{raw.Name}: mode \"{mode.Name}\" applied.{note}");
    }

    /// <summary>Switches the device into a mode that accepts a colour (Direct/Static) when it is not in one already.</summary>
    private OperationResult EnsureColorMode(OpenRgbClient client, Device raw, bool requirePerLed)
    {
        var modes = (raw.Modes ?? []).Select(OpenRgbDeviceMapper.MapMode).ToList();
        if (modes.Count == 0)
        {
            return OperationResult.Ok();
        }

        int? target = LightingApplyPlanner.PickColorModeIndex(modes, ActiveModeOf(raw));
        if (requirePerLed)
        {
            var perLed = raw.Modes!.FirstOrDefault(m => m.Index == target && OpenRgbDeviceMapper.IsPerLed(m))
                ?? raw.Modes!.FirstOrDefault(m => OpenRgbDeviceMapper.IsPerLed(m) && m.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase))
                ?? raw.Modes!.FirstOrDefault(OpenRgbDeviceMapper.IsPerLed);
            target = perLed?.Index;
        }

        if (target is null)
        {
            return OperationResult.Fail($"{raw.Name} has no mode that accepts a colour.");
        }

        if (LightingApplyPlanner.NeedsModeSwitch(modes, ActiveModeOf(raw), target.Value))
        {
            var mode = raw.Modes![target.Value];
            client.UpdateMode(raw.Index, target.Value, null, null, mode.Colors ?? []);
            _activeModeOverride[raw.Index] = target.Value;
        }

        return OperationResult.Ok();
    }

    private void RefreshCache(OpenRgbClient client)
    {
        try
        {
            _raw = client.GetAllControllerData() ?? _raw;
            _activeModeOverride.Clear();
            _devices = _raw.Select(OpenRgbDeviceMapper.Map).ToList();
            RaiseDevicesChanged();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not refresh OpenRGB device list after a change.");
        }
    }

    /// <summary>The mode the device is in, counting switches made since the last refresh of the server data.</summary>
    private int ActiveModeOf(Device raw) => _activeModeOverride.TryGetValue(raw.Index, out var m) ? m : raw.ActiveModeIndex;

    private Device? Raw(int deviceIndex) => _raw.FirstOrDefault(d => d.Index == deviceIndex) ?? (deviceIndex >= 0 && deviceIndex < _raw.Length ? _raw[deviceIndex] : null);

    private string? SafeInstallPath()
    {
        try
        {
            return _installPath.Value;
        }
        catch
        {
            return null;
        }
    }

    private void DisposeClient()
    {
        try
        {
            _client?.Dispose();
        }
        catch
        {
            // Closing a dead socket must never fail the caller.
        }

        _client = null;
    }

    private void RaiseDevicesChanged()
    {
        try
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A DevicesChanged subscriber threw.");
        }
    }

    private async Task JournalAsync(string action, string target, OperationResult result, string? after)
    {
        try
        {
            await _journal.RecordAsync(ActionRecord.Create(Module, action, target, result.Success ? "Success" : "Failed: " + result.Message,
                after: after, reversible: true, undoHint: "Pick another colour or profile on the Lighting page.")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write the lighting journal entry.");
        }
    }
}
