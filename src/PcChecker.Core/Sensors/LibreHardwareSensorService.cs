using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Sensors;

/// <summary>
/// Sensor service backed by LibreHardwareMonitor. Also the <see cref="IHardwareMonitorHost"/> other modules share
/// (fans, SMART, overclock readouts); register one instance for both interfaces. The kernel driver is opened lazily
/// on <see cref="Start"/> from a background thread, polling never touches the UI thread, and every call into the
/// monitor is serialized by one lock so the polling loop and other modules do not race.
/// </summary>
public sealed class LibreHardwareSensorService : ISensorService, IHardwareMonitorHost, ISensorServiceExtras, IDisposable
{
    private const string AdminHint = "Make sure PC Checker runs as administrator, that the PawnIO driver is installed and that no other monitoring tool holds the hardware.";

    /// <summary>
    /// Shown when PawnIO is missing. LibreHardwareMonitor reads the CPU and the motherboard sensor chip only through the
    /// signed PawnIO driver (it no longer bundles WinRing0, which Microsoft Defender quarantines), and it does not install it.
    /// </summary>
    internal const string PawnIoMissing = "The PawnIO driver is not installed, so CPU temperatures and the motherboard sensor chip (case and CPU fan headers, VRM and chipset readings) cannot be read. "
        + "Install PawnIO from https://pawnio.eu (or run \"winget install namazso.PawnIO\"), then restart PC Checker.";

    private readonly ISettingsService _settings;
    private readonly ILogger<LibreHardwareSensorService> _logger;
    private readonly object _computerLock = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<string, SensorRingBuffer> _history = new(StringComparer.Ordinal);
    private readonly AlertTracker _alerts = new();
    private readonly UpdateVisitor _visitor = new();
    private readonly List<string> _warnings = [];

    private Computer? _computer;
    private bool _isOpen;
    private string? _openError;
    private bool _started;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private int _historyCapacity;

    public LibreHardwareSensorService(ISettingsService settings, ILogger<LibreHardwareSensorService> logger)
    {
        _settings = settings;
        _logger = logger;
        _historyCapacity = SensorRingBuffer.CapacityFor(settings.Current.Monitoring.HistoryMinutes, settings.Current.Monitoring.PollIntervalMs);
    }

    public bool IsAvailable => _isOpen;

    public string? UnavailableReason => _isOpen ? null : _openError ?? (_started ? "The hardware monitor is still starting." : "Sensor monitoring has not been started yet.");

    public SensorSnapshot? Latest { get; private set; }

    public IReadOnlyList<string> SensorIds
    {
        get
        {
            lock (_stateLock)
            {
                return _history.Keys.ToList();
            }
        }
    }

    public bool IsOpen => _isOpen;

    public string? DriverProblem => PlatformGuard.IsWindows && !IsPawnIoInstalled() ? PawnIoMissing : null;

    public string? OpenError => _isOpen ? null : _openError;

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_stateLock)
            {
                return _warnings.ToList();
            }
        }
    }

    public event EventHandler<SensorSnapshot>? SnapshotUpdated;

    public event EventHandler<TemperatureAlert>? AlertRaised;

    public void Start()
    {
        lock (_stateLock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            _loopTask = Task.Run(() => PollLoopAsync(token), CancellationToken.None);
        }
    }

    public void Stop()
    {
        Task? loop;
        lock (_stateLock)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _loopCts?.Cancel();
            loop = _loopTask;
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // A cancelled loop may surface as an aggregate exception; nothing to do.
        }

        lock (_stateLock)
        {
            _loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;
        }
    }

    public void Restart()
    {
        Stop();
        CloseComputer();
        Start();
    }

    public IReadOnlyList<(DateTimeOffset Time, double Value)> GetHistory(string sensorId, TimeSpan span)
    {
        lock (_stateLock)
        {
            return _history.TryGetValue(sensorId, out var buffer) ? buffer.Window(span, DateTimeOffset.Now) : [];
        }
    }

    public IReadOnlyList<SensorReading> GetTemperatures()
    {
        var latest = Latest;
        if (latest is null)
        {
            return [];
        }

        return latest.OfKind(SensorKind.Temperature)
            .OrderBy(r => SensorMapping.GroupOrder(r.Hardware))
            .ThenBy(r => r.HardwareName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void ResetMinMax()
    {
        lock (_stateLock)
        {
            foreach (var buffer in _history.Values)
            {
                buffer.ResetMinMax();
            }
        }
    }

    public Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_isOpen)
        {
            return Task.CompletedTask;
        }

        return Task.Run(OpenComputer, cancellationToken);
    }

    public T? WithComputer<T>(Func<IComputer, T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        lock (_computerLock)
        {
            if (!_isOpen || _computer is null)
            {
                return default;
            }

            try
            {
                return func(_computer);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A hardware monitor call failed.");
                return default;
            }
        }
    }

    public void WithComputer(Action<IComputer> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        WithComputer<object?>(c =>
        {
            action(c);
            return null;
        });
    }

    public void Dispose()
    {
        Stop();
        CloseComputer();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        OpenComputer();
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_isOpen)
            {
                try
                {
                    Poll();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sensor poll failed; will retry.");
                }
            }

            int interval = Math.Clamp(_settings.Current.Monitoring.PollIntervalMs, 250, 60_000);
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OpenComputer()
    {
        lock (_computerLock)
        {
            if (_isOpen)
            {
                return;
            }

            if (!PlatformGuard.IsWindows)
            {
                _openError = "Hardware sensors are only available on Windows.";
                return;
            }

            Computer? computer = null;
            try
            {
                computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = true,
                    IsStorageEnabled = true,
                    IsNetworkEnabled = true,
                    IsControllerEnabled = true,
                    IsPsuEnabled = true,
                    IsBatteryEnabled = true,
                };
                computer.Open();
                computer.Accept(_visitor);

                if (computer.Hardware.Count == 0)
                {
                    TryClose(computer);
                    _openError = "The hardware monitor opened but found no hardware. " + (DriverProblem ?? AdminHint);
                    _logger.LogWarning("LibreHardwareMonitor found no hardware.");
                    return;
                }

                _computer = computer;
                _isOpen = true;
                _openError = null;
                BuildWarnings(computer);
                _logger.LogInformation("LibreHardwareMonitor opened with {Count} hardware items.", computer.Hardware.Count);
            }
            catch (Exception ex)
            {
                TryClose(computer);
                _computer = null;
                _isOpen = false;
                _openError = $"The hardware monitor could not start: {ex.Message} {AdminHint}";
                _logger.LogWarning(ex, "LibreHardwareMonitor failed to open.");
            }
        }
    }

    private void CloseComputer()
    {
        lock (_computerLock)
        {
            TryClose(_computer);
            _computer = null;
            _isOpen = false;
        }
    }

    private void TryClose(Computer? computer)
    {
        if (computer is null)
        {
            return;
        }

        try
        {
            computer.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Closing the hardware monitor failed.");
        }
    }

    private void BuildWarnings(Computer computer)
    {
        var notes = new List<string>();
        bool hasCpuTemp = false, hasMotherboard = false, hasGpu = false;
        foreach (var hardware in computer.Hardware)
        {
            var kind = SensorMapping.ToHardwareKind(hardware.HardwareType);
            hasMotherboard |= kind == HardwareKind.Motherboard;
            hasGpu |= kind == HardwareKind.Gpu;
            if (kind == HardwareKind.Cpu && EnumerateSensors(hardware).Any(s => s.SensorType == SensorType.Temperature))
            {
                hasCpuTemp = true;
            }
        }

        string? driverProblem = DriverProblem;
        if (driverProblem is not null)
        {
            notes.Add(driverProblem);
        }

        if (!hasCpuTemp)
        {
            notes.Add("No CPU temperature sensor was found. " + (driverProblem is null ? AdminHint : "Install PawnIO (see above)."));
        }

        if (!hasMotherboard)
        {
            notes.Add("No motherboard sensor chip was detected, so VRM, chipset and case fan readings are not available on this board.");
        }

        if (!hasGpu)
        {
            notes.Add("No graphics card sensors were found. Install the latest graphics driver if a dedicated GPU is present.");
        }

        lock (_stateLock)
        {
            _warnings.Clear();
            _warnings.AddRange(notes);
        }
    }

    private bool IsPawnIoInstalled()
    {
        try
        {
            return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
        }
        catch (Exception ex)
        {
            // Could not read the uninstall entry: do not claim the driver is missing.
            _logger.LogDebug(ex, "Could not check whether PawnIO is installed.");
            return true;
        }
    }

    private void Poll()
    {
        var now = DateTimeOffset.Now;
        var raw = new List<(string Id, string HardwareName, HardwareKind Hardware, SensorKind Kind, string Name, double? Value, string Unit)>();

        lock (_computerLock)
        {
            if (!_isOpen || _computer is null)
            {
                return;
            }

            _computer.Accept(_visitor);
            foreach (var hardware in _computer.Hardware)
            {
                CollectHardware(hardware, raw);
            }
        }

        var monitoring = _settings.Current.Monitoring;
        var readings = new List<SensorReading>(raw.Count);
        lock (_stateLock)
        {
            int capacity = SensorRingBuffer.CapacityFor(monitoring.HistoryMinutes, monitoring.PollIntervalMs);
            bool resize = capacity != _historyCapacity;
            _historyCapacity = capacity;

            foreach (var r in raw)
            {
                if (!_history.TryGetValue(r.Id, out var buffer))
                {
                    _history[r.Id] = buffer = new SensorRingBuffer(capacity);
                }
                else if (resize)
                {
                    buffer.Resize(capacity);
                }

                if (r.Value is { } value)
                {
                    buffer.Add(now, value);
                }

                readings.Add(new SensorReading(r.Id, r.HardwareName, r.Hardware, r.Kind, r.Name, r.Value, buffer.Min, buffer.Max, r.Unit));
            }
        }

        var snapshot = new SensorSnapshot(now, readings);
        Latest = snapshot;
        SnapshotUpdated?.Invoke(this, snapshot);

        if (!monitoring.AlertsEnabled)
        {
            return;
        }

        List<TemperatureAlert>? alerts = null;
        lock (_stateLock)
        {
            foreach (var reading in readings)
            {
                if (reading.Kind != SensorKind.Temperature)
                {
                    continue;
                }

                if (_alerts.Evaluate(reading, monitoring, now) is { } alert)
                {
                    (alerts ??= []).Add(alert);
                }
            }
        }

        if (alerts is null)
        {
            return;
        }

        foreach (var alert in alerts)
        {
            _logger.LogWarning("{Hardware} {Sensor} is at {Value:0.#} °C (threshold {Threshold:0.#} °C, {Severity}).",
                alert.HardwareName, alert.SensorId, alert.Value, alert.Threshold, alert.Severity);
            AlertRaised?.Invoke(this, alert);
        }
    }

    private static void CollectHardware(IHardware hardware,
        List<(string Id, string HardwareName, HardwareKind Hardware, SensorKind Kind, string Name, double? Value, string Unit)> into)
    {
        var kind = SensorMapping.ToHardwareKind(hardware.HardwareType);
        string hardwareName = string.IsNullOrWhiteSpace(hardware.Name) ? SensorMapping.DisplayName(kind) : hardware.Name;
        string hardwareId = hardware.Identifier?.ToString() ?? string.Empty;

        foreach (var sensor in hardware.Sensors)
        {
            string id = SensorMapping.BuildId(hardwareId, sensor.Identifier?.ToString());
            if (id.Length == 0)
            {
                continue;
            }

            into.Add((
                id,
                hardwareName,
                kind,
                SensorMapping.ToSensorKind(sensor.SensorType),
                string.IsNullOrWhiteSpace(sensor.Name) ? SensorMapping.DisplayName(SensorMapping.ToSensorKind(sensor.SensorType)) : sensor.Name,
                SensorMapping.ConvertValue(sensor.SensorType, sensor.Value),
                SensorMapping.UnitFor(sensor.SensorType)));
        }

        foreach (var sub in hardware.SubHardware)
        {
            CollectHardware(sub, into);
        }
    }

    private static IEnumerable<ISensor> EnumerateSensors(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
        {
            yield return sensor;
        }

        foreach (var sub in hardware.SubHardware)
        {
            foreach (var sensor in EnumerateSensors(sub))
            {
                yield return sensor;
            }
        }
    }

    /// <summary>Standard LibreHardwareMonitor visitor: refreshes every hardware item and its sub-hardware.</summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
            {
                sub.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}
