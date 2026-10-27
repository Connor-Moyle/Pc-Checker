using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Fans;

/// <summary>
/// Fan control through the shared LibreHardwareMonitor host: enumerates every fan control (motherboard SuperIO
/// headers, GPU fans, some coolers), pairs it with its RPM sensor, and drives manual speeds, curves and profiles
/// from a one-second loop. Every hardware access happens inside <see cref="IHardwareMonitorHost.WithComputer(Action{IComputer})"/>,
/// and the public async methods always run that access on a thread-pool thread: the host lock is shared with the
/// sensor poll loop, so taking it (and calling <c>IHardware.Update()</c>) on the caller's UI thread would freeze the window.
/// </summary>
public sealed partial class LibreHardwareFanControlService : IFanControlService, IFanControlAdvanced, IDisposable
{
    private const string ModuleName = "Fans";
    private readonly IHardwareMonitorHost _host;
    private readonly ISensorService _sensors;
    private readonly ISettingsService _settings;
    private readonly IChangeGuard _guard;
    private readonly ILogger<LibreHardwareFanControlService> _logger;
    private readonly object _gate = new();
    private readonly List<FanEntry> _entries = [];
    private readonly List<RpmOnlyEntry> _rpmOnly = [];
    private readonly List<IHardware> _fanHardware = [];
    private readonly Dictionary<string, ISensor> _lhmTemperatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FanControlAlert> _alerts = [];
    private readonly List<string> _capabilityNotes = [];
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);

    /// <summary>
    /// Serializes everything that writes to the fans: the one-second loop and every user change (manual, curve, profile,
    /// back to automatic). Without it a tick that read a fan's mode just before "Revert all to automatic" would put the
    /// fan straight back into manual mode at the curve value, or overwrite a manual speed the user had just set. Never
    /// disposed: a tick still running during shutdown releases it.
    /// </summary>
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private IReadOnlyList<FanProfile>? _builtInProfiles;
    private Timer? _timer;
    private bool _discovered;
    private bool _disposed;

    public LibreHardwareFanControlService(IHardwareMonitorHost host, ISensorService sensors, ISettingsService settings,
        IChangeGuard guard, ILogger<LibreHardwareFanControlService> logger)
    {
        _host = host;
        _sensors = sensors;
        _settings = settings;
        _guard = guard;
        _logger = logger;
    }

    public bool IsAvailable { get; private set; }
    public string? UnavailableReason { get; private set; } = "Fans have not been detected yet.";
    public FanProfile? ActiveProfile { get; private set; }

    public IReadOnlyList<FanProfile> BuiltInProfiles
    {
        get
        {
            lock (_gate)
            {
                if (_builtInProfiles is { } cached)
                {
                    return cached;
                }
            }

            // Built outside the gate: it reads temperatures through the host, and the host lock must never be taken while _gate is held.
            var built = BuildBuiltInProfiles();
            lock (_gate)
            {
                return _builtInProfiles ??= built;
            }
        }
    }

    public IReadOnlyList<string> CapabilityNotes
    {
        get
        {
            lock (_gate)
            {
                return _capabilityNotes.ToList();
            }
        }
    }

    public IReadOnlyList<FanControlAlert> Alerts
    {
        get
        {
            lock (_gate)
            {
                return _alerts.ToList();
            }
        }
    }

    public IReadOnlyList<string> SoftwareControlledFanIds
    {
        get
        {
            lock (_gate)
            {
                return _entries.Where(e => e.Mode != FanMode.Auto).Select(e => e.Id).ToList();
            }
        }
    }

    public event EventHandler<IReadOnlyList<FanDevice>>? FansUpdated;
    public event EventHandler<FanControlAlert>? AlertRaised;

    /// <summary>Where the crash marker lives for this settings directory.</summary>
    public string SessionFilePath => FanSessionStore.DefaultPath(_settings.SettingsDirectory);

    /// <summary>
    /// Re-scans the hardware every time (this is what "Detect fans" calls): a host that was still opening at startup,
    /// a newly plugged controller or a fan that disappeared are all picked up. Software state of fans that are still
    /// present is kept.
    /// </summary>
    public async Task<IReadOnlyList<FanDevice>> GetFansAsync(CancellationToken cancellationToken)
    {
        await DiscoverAsync(cancellationToken, force: true).ConfigureAwait(false);
        var fans = Snapshot();
        FansUpdated?.Invoke(this, fans);
        return fans;
    }

    public Task<OperationResult> SetManualAsync(string fanId, double percent, CancellationToken cancellationToken) =>
        SetManualAsync(fanId, percent, allowBelowMinimum: false, cancellationToken);

    public async Task<OperationResult> SetManualAsync(string fanId, double percent, bool allowBelowMinimum, CancellationToken cancellationToken)
    {
        await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var entry = Find(fanId);
        if (entry is null)
        {
            return OperationResult.Fail($"Fan \"{fanId}\" was not found. Click \"Detect fans\" and try again.");
        }

        double clamped = FanSafetyRules.ClampManual(percent, _settings.Current.Fans.MinimumPercent, allowBelowMinimum, entry.MinPercent, entry.MaxPercent);
        return await _guard.RunGuardedAsync($"Set {entry.Name} to {clamped:0} %",
            new ChangeOptions(false, false, ModuleName, entry.Id, true, "Fans → Revert all to automatic"),
            _ => Task.Run(() => UnderControlGate(() =>
            {
                var result = ApplySoftware(entry, clamped, "manual");
                if (result.Success)
                {
                    lock (_gate)
                    {
                        entry.Mode = FanMode.Manual;
                        entry.ManualPercent = clamped;
                        entry.Curve = null;
                        entry.Forced = false;
                    }

                    UpdateSessionFile();
                    EnsureTimer();
                }

                return result;
            })), null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> SetAutoAsync(string fanId, CancellationToken cancellationToken)
    {
        await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var entry = Find(fanId);
        if (entry is null)
        {
            return OperationResult.Fail($"Fan \"{fanId}\" was not found.");
        }

        return await _guard.RunGuardedAsync($"Return {entry.Name} to automatic control",
            new ChangeOptions(false, false, ModuleName, entry.Id, false, null),
            _ => Task.Run(() => UnderControlGate(() =>
            {
                var result = ApplyDefault(entry);
                UpdateSessionFile();
                return result;
            })), null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> SetCurveAsync(string fanId, FanCurve curve, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(curve);
        await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var entry = Find(fanId);
        if (entry is null)
        {
            return OperationResult.Fail($"Fan \"{fanId}\" was not found. Click \"Detect fans\" and try again.");
        }

        var problem = FanCurveMath.Validate(curve);
        if (problem is not null)
        {
            return OperationResult.Fail(problem);
        }

        return await _guard.RunGuardedAsync($"Put {entry.Name} on a fan curve",
            new ChangeOptions(false, false, ModuleName, entry.Id, true, "Fans → Revert all to automatic"),
            _ => Task.Run(() => UnderControlGate(() => ApplyCurveCore(entry, curve, "curve"))), null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> ApplyProfileAsync(FanProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (!IsAvailable)
        {
            return OperationResult.Fail(UnavailableReason ?? "Fan control is not available on this PC.");
        }

        return await _guard.RunGuardedAsync($"Apply fan profile \"{profile.Name}\"",
            new ChangeOptions(false, false, ModuleName, profile.Name, true, "Fans → Revert all to automatic"),
            _ => Task.Run(() => UnderControlGate(() => ApplyProfileCore(profile))), null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> RevertAllToAutoAsync(CancellationToken cancellationToken)
    {
        List<FanEntry> entries;
        lock (_gate)
        {
            entries = _entries.Where(e => e.Mode != FanMode.Auto || e.LastApplied is not null).ToList();
        }

        if (entries.Count == 0)
        {
            FanSessionStore.Delete(SessionFilePath);
            ActiveProfile = null;
            return OperationResult.Ok("No fans are under software control.");
        }

        return await _guard.RunGuardedAsync("Return all fans to automatic control",
            new ChangeOptions(false, false, ModuleName, "all fans", false, null),
            _ => Task.Run(() => UnderControlGate(() =>
            {
                // Re-read under the control gate: a change that finished while the guard ran may have added a fan.
                lock (_gate)
                {
                    entries = _entries.Where(e => e.Mode != FanMode.Auto || e.LastApplied is not null).ToList();
                }

                int ok = 0;
                var failures = new List<string>();
                foreach (var entry in entries)
                {
                    var r = ApplyDefault(entry);
                    if (r.Success)
                    {
                        ok++;
                    }
                    else
                    {
                        failures.Add($"{entry.Name}: {r.Message}");
                    }
                }

                ActiveProfile = null;
                UpdateSessionFile();
                FansUpdated?.Invoke(this, Snapshot());
                return failures.Count == 0
                    ? OperationResult.Ok($"{ok} fan{(ok == 1 ? string.Empty : "s")} returned to automatic control.")
                    : OperationResult.Fail($"{ok} fans reverted; {failures.Count} did not: {string.Join("; ", failures)}");
            })), null, cancellationToken).ConfigureAwait(false);
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken, bool force = false)
    {
        if (_discovered && !force)
        {
            return;
        }

        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_discovered && !force)
            {
                return;
            }

            await _host.EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            if (!_host.IsOpen)
            {
                lock (_gate)
                {
                    IsAvailable = false;
                    UnavailableReason = "The hardware monitor could not start, so fans cannot be read or controlled. "
                        + (_host.OpenError ?? "Run PC Checker as administrator and make sure no other monitoring tool is blocking the driver.");
                    _capabilityNotes.Clear();
                }

                // Not marked as discovered: the host may simply still be opening, so the next call tries again.
                return;
            }

            var discovered = new List<(FanSensorInfo Info, ISensor Sensor)>();
            var hardwareWithFans = new List<IHardware>();
            var temps = new Dictionary<string, ISensor>(StringComparer.OrdinalIgnoreCase);

            // Both awaits above complete synchronously once the host is open, so without this hop the walk (and its
            // hardware.Update() calls under the host lock shared with the sensor poll loop) would run on the caller's
            // UI thread. Everything after it continues on the thread pool (ConfigureAwait(false)).
            await Task.Run(() => _host.WithComputer(computer =>
            {
                foreach (var hardware in computer.Hardware)
                {
                    Walk(hardware, discovered, hardwareWithFans, temps);
                }
            }), cancellationToken).ConfigureAwait(false);

            var pairs = FanPairing.Pair(discovered.Select(d => d.Info));
            var unpaired = FanPairing.Unpaired(discovered.Select(d => d.Info), pairs);
            var sensorsById = discovered.ToDictionary(d => d.Info.Id, d => d.Sensor, StringComparer.OrdinalIgnoreCase);

            lock (_gate)
            {
                var previous = _entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
                _entries.Clear();
                foreach (var pair in pairs)
                {
                    var controlSensor = sensorsById[pair.Control.Id];
                    if (controlSensor.Control is not { } control)
                    {
                        continue;
                    }

                    var entry = new FanEntry(pair.Control.Id, FanPairing.FriendlyName(pair.Control, pair.Rpm), pair.Control.Hardware,
                        pair.Control.HardwareName, pair.Control.HardwareId, controlSensor, control,
                        pair.Rpm is null ? null : sensorsById[pair.Rpm.Id]);
                    if (previous.TryGetValue(entry.Id, out var old))
                    {
                        entry.Mode = old.Mode;
                        entry.ManualPercent = old.ManualPercent;
                        entry.Curve = old.Curve;
                        entry.LastApplied = old.LastApplied;
                        entry.Forced = old.Forced;
                        entry.LastRpm = old.LastRpm;
                        entry.LastPercent = old.LastPercent;
                    }

                    _entries.Add(entry);
                }

                _rpmOnly.Clear();
                foreach (var rpm in unpaired)
                {
                    _rpmOnly.Add(new RpmOnlyEntry(rpm.Id, rpm.Name, rpm.Hardware, rpm.HardwareName, sensorsById[rpm.Id]));
                }

                _fanHardware.Clear();
                _fanHardware.AddRange(hardwareWithFans.Distinct());
                _lhmTemperatures.Clear();
                foreach (var (id, sensor) in temps)
                {
                    _lhmTemperatures[id] = sensor;
                }

                _builtInProfiles = null;
                BuildCapabilityNotes();
                IsAvailable = _entries.Count > 0;
                UnavailableReason = IsAvailable ? null : DescribeUnavailable();
            }

            _discovered = true;
            _logger.LogInformation("Fan discovery: {Controls} controllable fan(s), {RpmOnly} read-only, {Temps} temperature source(s).",
                _entries.Count, _rpmOnly.Count, _lhmTemperatures.Count);
            if (_entries.Count > 0)
            {
                EnsureTimer();
            }

            // Rebuild the built-in profiles here, off the UI thread: building them can read temperatures through the
            // host, and the Fans page reads BuiltInProfiles on the UI thread right after discovery.
            try
            {
                _ = BuiltInProfiles;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not pre-build the built-in fan profiles.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fan discovery failed.");
            lock (_gate)
            {
                IsAvailable = false;
                UnavailableReason = "Fan detection failed: " + ex.Message + " Click \"Detect fans\" to try again.";
            }
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private void Walk(IHardware hardware, List<(FanSensorInfo, ISensor)> sink, List<IHardware> withFans, Dictionary<string, ISensor> temps)
    {
        try
        {
            var kind = MapKind(hardware);
            bool updated = false;
            if (kind is HardwareKind.Motherboard or HardwareKind.Gpu or HardwareKind.Cooler or HardwareKind.Psu or HardwareKind.Cpu)
            {
                hardware.Update();
                updated = true;
            }

            string hardwareId = hardware.Identifier.ToString();
            bool hasFan = false;
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType == SensorType.Control && sensor.Control is not null)
                {
                    sink.Add((new FanSensorInfo(sensor.Identifier.ToString(), hardwareId, hardware.Name, kind, SensorKind.Control, sensor.Name, sensor.Index, sensor.Value), sensor));
                    hasFan = true;
                }
                else if (sensor.SensorType == SensorType.Fan)
                {
                    sink.Add((new FanSensorInfo(sensor.Identifier.ToString(), hardwareId, hardware.Name, kind, SensorKind.Fan, sensor.Name, sensor.Index, sensor.Value), sensor));
                    hasFan = true;
                }
                else if (sensor.SensorType == SensorType.Temperature && updated)
                {
                    temps[sensor.Identifier.ToString()] = sensor;
                }
            }

            if (hasFan)
            {
                withFans.Add(hardware);
            }

            foreach (var sub in hardware.SubHardware)
            {
                Walk(sub, sink, withFans, temps);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Skipping hardware {Name} during fan discovery.", hardware.Name);
        }
    }

    private static HardwareKind MapKind(IHardware? hardware) => hardware?.HardwareType.ToString() switch
    {
        "Motherboard" or "SuperIO" or "EmbeddedController" => HardwareKind.Motherboard,
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => HardwareKind.Gpu,
        "Cpu" => HardwareKind.Cpu,
        "Memory" => HardwareKind.Memory,
        "Storage" => HardwareKind.Storage,
        "Network" => HardwareKind.Network,
        "Cooler" => HardwareKind.Cooler,
        "Psu" => HardwareKind.Psu,
        "Battery" => HardwareKind.Battery,
        _ => HardwareKind.Other,
    };

    /// <summary>One controllable fan and its current software state.</summary>
    private sealed class FanEntry(string id, string name, HardwareKind kind, string hardwareName, string hardwareId,
        ISensor controlSensor, IControl control, ISensor? rpmSensor)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public HardwareKind Kind { get; } = kind;
        public string HardwareName { get; } = hardwareName;
        public string HardwareId { get; } = hardwareId;
        public ISensor ControlSensor { get; } = controlSensor;
        public IControl Control { get; } = control;
        public ISensor? RpmSensor { get; } = rpmSensor;
        public double MinPercent => Math.Clamp(Control.MinSoftwareValue, 0, 100);
        public double MaxPercent => Control.MaxSoftwareValue <= 0 ? 100 : Math.Clamp(Control.MaxSoftwareValue, 1, 100);
        public FanMode Mode { get; set; } = FanMode.Auto;
        public double? ManualPercent { get; set; }
        public FanCurve? Curve { get; set; }
        public double? LastApplied { get; set; }
        public bool Forced { get; set; }
        public int MissingTemperatureTicks { get; set; }
        public double? LastRpm { get; set; }
        public double? LastPercent { get; set; }
    }

    /// <summary>A fan whose speed can be read but not changed.</summary>
    private sealed record RpmOnlyEntry(string Id, string Name, HardwareKind Kind, string HardwareName, ISensor Sensor)
    {
        public double? LastRpm { get; set; }
    }
}
