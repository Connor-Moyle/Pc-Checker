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
/// disconnected and a reconnect is attempted with backoff, without ever sleeping while the service is locked.
/// </summary>
/// <remarks>
/// Brightness: OpenRGB.NET 3.1.1 re-reads a mode from the server inside <c>UpdateMode</c> and offers no way to send the
/// mode's own brightness value, so PC Checker dims by scaling the colour it sends. Effects that pick their own colours
/// therefore cannot be dimmed from here; results and the page say so instead of pretending.
/// </remarks>
public sealed class OpenRgbLightingService : ILightingService, IDisposable
{
    private const string Module = "Lighting";
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);

    private readonly ISettingsService _settings;
    private readonly IActionJournal _journal;
    private readonly ILogger<OpenRgbLightingService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _installGate = new();
    private string? _installPath;
    private DateTimeOffset _installCheckedAt = DateTimeOffset.MinValue;

    private OpenRgbClient? _client;
    private Device[] _raw = [];
    /// <summary>Mode switches made since <see cref="_raw"/> was last read from the server; cleared with every re-read.</summary>
    private readonly Dictionary<int, int> _activeModeOverride = [];
    private List<RgbDevice> _devices = [];
    /// <summary>Devices the server lists that could not be read in full (shown, not controllable).</summary>
    private List<RgbDevice> _unreadable = [];
    private OpenRgbAvailability _availability = OpenRgbAvailability.Unreachable;
    private bool _attempted;
    private string? _lastError;
    private int _reconnectFailures;
    private DateTimeOffset _reconnectNotBefore = DateTimeOffset.MinValue;
    private Timer? _watch;
    private bool _disposed;

    public OpenRgbLightingService(ISettingsService settings, IActionJournal journal, ILogger<OpenRgbLightingService> logger)
    {
        _settings = settings;
        _journal = journal;
        _logger = logger;
    }

    public bool IsConnected => _client?.Connected == true;

    public string Status
    {
        get
        {
            if (!_attempted && !IsConnected)
            {
                return LightingStatusText.For(false, IsOpenRgbInstalled, null, 0, _lastError);
            }

            var availability = IsConnected ? OpenRgbAvailability.Connected : _availability;
            string text = LightingStatusText.For(availability, ServerAddress, ProtocolVersionText, _devices.Count);
            return string.IsNullOrWhiteSpace(_lastError) || availability == OpenRgbAvailability.Connected ? text : $"{text} ({_lastError.Trim()})";
        }
    }

    public string ServerAddress => $"{_settings.Current.Lighting.OpenRgbHost}:{_settings.Current.Lighting.OpenRgbPort}";

    public OpenRgbAvailability Availability => IsConnected
        ? OpenRgbAvailability.Connected
        : _attempted
            ? _availability
            : OpenRgbInstallRules.Availability(false, OpenRgbInstallRules.IsLocalHost(_settings.Current.Lighting.OpenRgbHost), IsOpenRgbInstalled, processRunning: false);

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
            // A deliberate connect starts the backoff schedule over.
            _reconnectFailures = 0;
            _reconnectNotBefore = DateTimeOffset.MinValue;
            return await Task.Run(() => ConnectCore(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Called with the gate held.</summary>
    private OperationResult ConnectCore()
    {
        var lighting = _settings.Current.Lighting;
        bool wasConnected = IsConnected;
        DisposeClient();
        try
        {
            _attempted = true;
            var client = new OpenRgbClient(lighting.OpenRgbHost, lighting.OpenRgbPort, "PC Checker", autoConnect: false, timeoutMs: 3000);
            client.Connect();
            _client = client;
            LoadFromServer(client);
            _availability = OpenRgbAvailability.Connected;
            _lastError = null;
            _reconnectFailures = 0;
            _reconnectNotBefore = DateTimeOffset.MinValue;
            // OpenRGB.NET 3.1.1 wires this event into its connection before anyone can subscribe, so it may never fire;
            // the watch timer below backs it up by comparing the server's device count every few seconds.
            client.DeviceListUpdated += OnServerDeviceListUpdated;
            StartWatch();
            _logger.LogInformation("Connected to OpenRGB at {Host}:{Port}; {Count} devices.", lighting.OpenRgbHost, lighting.OpenRgbPort, _devices.Count);
            RaiseDevicesChanged();
            return OperationResult.Ok(Status);
        }
        catch (Exception ex)
        {
            DisposeClient();
            _lastError = IsNetworkError(ex) ? null : ex.Message;
            _availability = OpenRgbInstallRules.Availability(false, OpenRgbInstallRules.IsLocalHost(lighting.OpenRgbHost), SafeInstallPath(lookAgain: true) is not null,
                OpenRgbInstallLocator.IsProcessRunning());
            // Keep trying in the background with growing pauses, so starting OpenRGB later connects the page by itself.
            // Only the first failure in a row is logged at information level; the background retries are debug noise.
            _logger.Log(_reconnectFailures == 0 ? LogLevel.Information : LogLevel.Debug,
                "Could not connect to OpenRGB at {Host}:{Port} ({State}): {Message}", lighting.OpenRgbHost, lighting.OpenRgbPort, _availability, ex.Message);
            _reconnectFailures++;
            _reconnectNotBefore = DateTimeOffset.UtcNow + LightingApplyPlanner.ReconnectBackoff(_reconnectFailures);
            if (lighting.AutoConnect)
            {
                StartWatch();
            }

            if (wasConnected)
            {
                RaiseDevicesChanged();
            }

            return OperationResult.Fail(Status, ex);
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopWatch();
            DisposeClient();
            ReplaceRaw([], []);
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
                    LoadFromServer(client);
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

    public async Task<OperationResult> SetDeviceColorAsync(int deviceIndex, RgbColor color, CancellationToken cancellationToken)
    {
        var result = await RunAsync(client => SetDeviceColor(client, deviceIndex, color), cancellationToken).ConfigureAwait(false);
        await RecordAsync("Set colour", DeviceName(deviceIndex), result, after: color.ToHex()).ConfigureAwait(false);
        return result;
    }

    public async Task<OperationResult> SetZoneColorAsync(int deviceIndex, int zoneIndex, RgbColor color, CancellationToken cancellationToken)
    {
        var result = await RunAsync(client =>
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
            if (ledCount == 0)
            {
                return OperationResult.Fail($"{raw.Zones[zoneIndex].Name} has no LEDs set yet. Enter how many LEDs are connected to it first.");
            }

            client.UpdateZoneLeds(deviceIndex, zoneIndex, OpenRgbDeviceMapper.ToOpenRgb(LightingApplyPlanner.Fill(ledCount, color)));
            return OperationResult.Ok($"{raw.Zones[zoneIndex].Name} set to {color.ToHex()}.");
        }, cancellationToken).ConfigureAwait(false);
        await RecordAsync("Set zone colour", $"{DeviceName(deviceIndex)} zone {zoneIndex}", result, after: color.ToHex()).ConfigureAwait(false);
        return result;
    }

    public async Task<OperationResult> ResizeZoneAsync(int deviceIndex, int zoneIndex, int ledCount, CancellationToken cancellationToken)
    {
        var result = await RunAsync(client =>
        {
            var device = _devices.FirstOrDefault(d => d.Index == deviceIndex);
            if (device is null || device.IsReadOnly)
            {
                return OperationResult.Fail($"No lighting device with index {deviceIndex}.");
            }

            if (zoneIndex < 0 || zoneIndex >= device.Zones.Count)
            {
                return OperationResult.Fail($"{device.Name} has no zone {zoneIndex}.");
            }

            var zone = device.Zones[zoneIndex];
            if (!zone.IsResizable)
            {
                return OperationResult.Fail($"{zone.Name} has a fixed number of LEDs ({zone.LedCount}).");
            }

            int size = LightingApplyPlanner.ClampZoneSize(zone, ledCount);
            client.ResizeZone(deviceIndex, zoneIndex, size);
            LoadFromServer(client);
            RaiseDevicesChanged();
            return OperationResult.Ok($"{zone.Name} on {device.Name} now has {size} LED{(size == 1 ? string.Empty : "s")}.");
        }, cancellationToken).ConfigureAwait(false);
        await RecordAsync("Set zone size", $"{DeviceName(deviceIndex)} zone {zoneIndex}", result, after: ledCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
        return result;
    }

    public async Task<OperationResult> SetModeAsync(int deviceIndex, int modeIndex, RgbColor? color, int? speed, int? brightness, CancellationToken cancellationToken)
    {
        var result = await RunAsync(client => SetMode(client, deviceIndex, modeIndex, color, speed, brightness), cancellationToken).ConfigureAwait(false);
        string after = $"mode {modeIndex}" + (color is { } c ? $" {c.ToHex()}" : string.Empty) + (speed is { } s ? $" speed {s}" : string.Empty) + (brightness is { } b ? $" brightness {b}%" : string.Empty);
        await RecordAsync("Set mode", DeviceName(deviceIndex), result, after).ConfigureAwait(false);
        return result;
    }

    public async Task<OperationResult> SetAllAsync(RgbColor color, CancellationToken cancellationToken)
    {
        var result = await RunAsync(client => ForEachDevice(client, d => SetDeviceColor(client, d.Index, color)), cancellationToken).ConfigureAwait(false);
        await RecordAsync("Set all", "All devices", result, after: color.ToHex()).ConfigureAwait(false);
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
        await RecordAsync("Turn off", "All devices", result, after: "Off").ConfigureAwait(false);
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

            // Exact mode name first; a "Rainbow" request also accepts the device's own "Rainbow Wave" / "Spectrum Cycle".
            var modeIndex = LightingApplyPlanner.ResolveModeIndex(device.Modes, assignment.ModeName);
            var color = assignment.Color ?? profile.AllColor;
            if (modeIndex is { } m)
            {
                return SetMode(client, device.Index, m, color, assignment.Speed, assignment.Brightness);
            }

            if (LightingApplyPlanner.IsRainbowRequest(assignment.ModeName))
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

        await RecordAsync("Apply profile", profile.Name, result, after: profile.AllColor?.ToHex()).ConfigureAwait(false);
        return result.Success ? OperationResult.Ok($"Profile \"{profile.Name}\" applied.") : result;
    }

    public async Task<OperationResult> LaunchOpenRgbServerAsync(CancellationToken cancellationToken)
    {
        var lighting = _settings.Current.Lighting;
        if (await OpenRgbInstallLocator.IsPortOpenAsync(lighting.OpenRgbHost, lighting.OpenRgbPort, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
        {
            return await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        _attempted = true;
        var path = await Task.Run(() => SafeInstallPath(lookAgain: true), cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            _availability = OpenRgbAvailability.NotInstalled;
            return OperationResult.Fail(LightingStatusText.NotInstalled);
        }

        // Never start a second OpenRGB: two instances contend for the same SMBus/USB devices.
        if (await Task.Run(OpenRgbInstallLocator.IsProcessRunning, cancellationToken).ConfigureAwait(false))
        {
            _availability = OpenRgbAvailability.RunningWithoutServer;
            _logger.LogInformation("OpenRGB is running without its SDK server; asking the user to start it there.");
            return OperationResult.Fail(LightingStatusText.RunningWithoutServer);
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
        StopWatch();
        DisposeClient();
        try
        {
            _gate.Dispose();
        }
        catch
        {
            // A watch tick may still be releasing the gate; nothing to do.
        }
    }

    // ---- internals -------------------------------------------------------------------------------------------

    private async Task<OperationResult> RunAsync(Func<OpenRgbClient, OperationResult> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not { Connected: true })
            {
                var reconnected = await Task.Run(TryReconnectNow, cancellationToken).ConfigureAwait(false);
                if (!reconnected)
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

    /// <summary>
    /// Called with the gate held. Reconnects when the backoff window has passed; never sleeps, so a change made while
    /// the server is down fails fast with the status text instead of blocking the service for seconds.
    /// </summary>
    private bool TryReconnectNow()
    {
        if (!_settings.Current.Lighting.AutoConnect)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _reconnectNotBefore)
        {
            return false;
        }

        _logger.LogDebug("Reconnecting to OpenRGB (attempt {Attempt}).", _reconnectFailures + 1);
        return ConnectCore().Success; // a failure counts itself and schedules the next try
    }

    private void HandleDrop(Exception ex)
    {
        if (IsNetworkError(ex) || ex is ObjectDisposedException)
        {
            _logger.LogWarning(ex, "OpenRGB connection dropped.");
            DisposeClient();
            _reconnectNotBefore = DateTimeOffset.UtcNow + LightingApplyPlanner.ReconnectBackoff(1);
            RaiseDevicesChanged();
        }
        else
        {
            _logger.LogWarning(ex, "OpenRGB call failed.");
        }
    }

    private void StartWatch()
    {
        if (_disposed)
        {
            return;
        }

        _watch ??= new Timer(_ => WatchTick(), null, WatchInterval, WatchInterval);
    }

    private void StopWatch()
    {
        var watch = Interlocked.Exchange(ref _watch, null);
        try
        {
            watch?.Dispose();
        }
        catch
        {
            // Already gone.
        }
    }

    /// <summary>
    /// Every few seconds: while connected, compare the server's device count with ours and reload on a difference
    /// (OpenRGB detected or lost a device); after a drop, reconnect once the backoff window has passed. Skips the tick
    /// when the service is busy so it never queues behind user changes.
    /// </summary>
    private void WatchTick()
    {
        if (_disposed || !_gate.Wait(0))
        {
            return;
        }

        try
        {
            if (_client is { Connected: true } client)
            {
                int count;
                try
                {
                    count = client.GetControllerCount();
                }
                catch (Exception ex)
                {
                    HandleDrop(ex);
                    return;
                }

                if (count != _raw.Length + _unreadable.Count)
                {
                    _logger.LogInformation("OpenRGB now reports {New} devices (was {Old}); reloading.", count, _raw.Length + _unreadable.Count);
                    RefreshCache(client);
                }
            }
            else
            {
                TryReconnectNow();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OpenRGB watch tick failed.");
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void OnServerDeviceListUpdated(object? sender, EventArgs e)
    {
        try
        {
            if (_disposed || !_gate.Wait(TimeSpan.FromSeconds(2)))
            {
                return;
            }

            try
            {
                if (_client is { Connected: true } client)
                {
                    _logger.LogInformation("OpenRGB reported a device list change; reloading.");
                    RefreshCache(client);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not handle the OpenRGB device list update.");
        }
    }

    private OperationResult ForEachDevice(OpenRgbClient client, Func<Device, OperationResult> perDevice)
    {
        if (_raw.Length == 0)
        {
            LoadFromServer(client);
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

        int activeIndex = ActiveModeOf(raw);
        var active = raw.Modes is { Length: > 0 } modes && activeIndex >= 0 && activeIndex < modes.Length ? modes[activeIndex] : null;
        int ledCount = raw.Leds?.Length ?? raw.Colors?.Length ?? 0;
        if (active is not null && !OpenRgbDeviceMapper.IsPerLed(active))
        {
            // Mode-specific colour: resend the mode with every one of its colour slots set (some controllers use 2+).
            client.UpdateMode(deviceIndex, active.Index, null, null, ModeColors(active, color));
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

        // Null leaves the mode's colours as they are; otherwise every colour slot of the mode gets the new colour.
        Color[]? modeColors = effective is { } ec && mapped.HasColor && !OpenRgbDeviceMapper.IsPerLed(mode)
            ? ModeColors(mode, ec)
            : null;

        client.UpdateMode(deviceIndex, modeIndex, speedValue, null, modeColors);
        _activeModeOverride[raw.Index] = modeIndex;

        if (effective is { } led && OpenRgbDeviceMapper.IsPerLed(mode))
        {
            int ledCount = raw.Leds?.Length ?? raw.Colors?.Length ?? 0;
            client.UpdateLeds(deviceIndex, OpenRgbDeviceMapper.ToOpenRgb(LightingApplyPlanner.Fill(ledCount, led)));
        }

        string note = brightness is { } b && b < 100 && !LightingApplyPlanner.CanDim(mapped)
            ? " " + LightingApplyPlanner.BrightnessUnsupportedNote
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
            client.UpdateMode(raw.Index, target.Value, null, null, null);
            _activeModeOverride[raw.Index] = target.Value;
        }

        return OperationResult.Ok();
    }

    /// <summary>Every colour slot of a mode-specific-colour mode filled with one colour (null when the mode has no slots).</summary>
    private static Color[]? ModeColors(Mode mode, RgbColor color)
    {
        int slots = mode.Colors?.Length ?? 0;
        return slots > 0 ? OpenRgbDeviceMapper.ToOpenRgb(LightingApplyPlanner.Fill(slots, color)) : null;
    }

    /// <summary>Re-reads the server's device data after a change and tells the page. Called with the gate held.</summary>
    private void RefreshCache(OpenRgbClient client)
    {
        try
        {
            LoadFromServer(client);
            RaiseDevicesChanged();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not refresh OpenRGB device list after a change.");
        }
    }

    /// <summary>
    /// The single place the raw server data is replaced. Mode overrides describe switches made on top of the previous
    /// snapshot, so they are dropped here; otherwise a mode the user changed in OpenRGB's own window would be
    /// mis-reported and a later colour send could land while the device is still in an effect mode.
    /// </summary>
    private void ReplaceRaw(Device[] raw, List<RgbDevice> unreadable)
    {
        _raw = raw;
        _unreadable = unreadable;
        _activeModeOverride.Clear();
        _devices = raw.Select(OpenRgbDeviceMapper.Map).Concat(unreadable).OrderBy(d => d.Index).ToList();
    }

    /// <summary>
    /// Reads every device from the server. When OpenRGB.NET cannot read the whole list (a device newer or odder than it
    /// expects), the devices are read one at a time so the others still appear, and the ones it cannot read are read
    /// with PC Checker's own defensive reader and listed as "details unreadable". Network errors are passed on.
    /// </summary>
    private void LoadFromServer(OpenRgbClient client)
    {
        try
        {
            ReplaceRaw(client.GetAllControllerData() ?? [], []);
            return;
        }
        catch (Exception ex) when (!IsNetworkError(ex) && ex is not ObjectDisposedException)
        {
            _logger.LogWarning(ex, "OpenRGB.NET could not read the whole device list; reading the devices one by one.");
        }

        int count = client.GetControllerCount();
        var readable = new List<Device>();
        var failed = new List<(int Index, string Reason)>();
        for (int i = 0; i < count; i++)
        {
            try
            {
                readable.Add(client.GetControllerData(i));
            }
            catch (Exception ex) when (!IsNetworkError(ex) && ex is not ObjectDisposedException)
            {
                failed.Add((i, ex.Message));
            }
        }

        ReplaceRaw(readable.ToArray(), DescribeUnreadable(failed));
    }

    /// <summary>Reads the devices OpenRGB.NET could not with the defensive reader, so they at least show with their name.</summary>
    private List<RgbDevice> DescribeUnreadable(List<(int Index, string Reason)> failed)
    {
        if (failed.Count == 0)
        {
            return [];
        }

        OpenRgbProbeResult? probe = null;
        try
        {
            var lighting = _settings.Current.Lighting;
            // Called on a worker thread (never the UI thread); the probe uses ConfigureAwait(false) throughout.
            var read = OpenRgbSdkProbe.ReadAllAsync(lighting.OpenRgbHost, lighting.OpenRgbPort, TimeSpan.FromSeconds(5), CancellationToken.None);
            probe = read.Wait(TimeSpan.FromSeconds(6)) ? read.Result : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The read-only OpenRGB probe failed.");
        }

        var list = new List<RgbDevice>();
        foreach (var (index, reason) in failed)
        {
            const string problem = "PC Checker could not read this device's lighting details from OpenRGB, so it cannot be controlled here.";
            var probed = probe?.Devices.FirstOrDefault(d => d.Index == index);
            list.Add(probed is not null
                ? probed with { Modes = [], ActiveModeIndex = 0, Problem = problem }
                : new RgbDevice(index, $"Device {index + 1}", "Unknown", OpenRgbDeviceTypes.Unknown, null, 0, [], [], 0, null, null, problem));
            _logger.LogWarning("OpenRGB device {Index} could not be read: {Reason}", index, reason);
        }

        return list;
    }

    private static bool IsNetworkError(Exception ex) => ex is SocketException or IOException or TimeoutException;

    /// <summary>The mode the device is in, counting switches made since the last refresh of the server data.</summary>
    private int ActiveModeOf(Device raw) => _activeModeOverride.TryGetValue(raw.Index, out var m) ? m : raw.ActiveModeIndex;

    private Device? Raw(int deviceIndex) => _raw.FirstOrDefault(d => d.Index == deviceIndex) ?? (deviceIndex >= 0 && deviceIndex < _raw.Length ? _raw[deviceIndex] : null);

    private string DeviceName(int deviceIndex) => _devices.FirstOrDefault(d => d.Index == deviceIndex)?.Name ?? $"Device {deviceIndex}";

    /// <summary>
    /// The OpenRGB.exe path. Found once (status reads on the UI thread then use the cached value); while it is not found,
    /// connection attempts and Start OpenRGB look again from their worker threads, so an install made from the page (or
    /// by hand) shows up without restarting PC Checker.
    /// </summary>
    private string? SafeInstallPath(bool lookAgain = false)
    {
        lock (_installGate)
        {
            bool checkedBefore = _installCheckedAt != DateTimeOffset.MinValue;
            if (_installPath is not null || (checkedBefore && !(lookAgain && DateTimeOffset.UtcNow - _installCheckedAt > TimeSpan.FromSeconds(2))))
            {
                return _installPath;
            }

            try
            {
                _installPath = OpenRgbInstallLocator.Find();
            }
            catch
            {
                _installPath = null;
            }

            _installCheckedAt = DateTimeOffset.UtcNow;
            return _installPath;
        }
    }

    private void DisposeClient()
    {
        var client = _client;
        _client = null;
        if (client is null)
        {
            return;
        }

        try
        {
            client.DeviceListUpdated -= OnServerDeviceListUpdated;
            client.Dispose();
        }
        catch
        {
            // Closing a dead socket must never fail the caller.
        }
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

    /// <summary>Lighting is cosmetic: every change is logged and journaled (reversible by picking another colour), never restore-pointed.</summary>
    private async Task RecordAsync(string action, string target, OperationResult result, string? after)
    {
        if (result.Success)
        {
            _logger.LogInformation("Lighting: {Action} on {Target} → {After}.", action, target, after ?? "(unchanged)");
        }
        else
        {
            _logger.LogWarning("Lighting: {Action} on {Target} failed: {Message}", action, target, result.Message);
        }

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
