using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Health;
using PcChecker.Core.Power;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.Core.History;

/// <summary>
/// The background recorder. While recording is on it listens to the shared sensor and power services (which poll
/// about once a second), combines the readings with <see cref="HistorySampleAccumulator"/> and writes one sample per
/// interval to <see cref="HistoryFileStore"/>: peak CPU/GPU temperature, average CPU/GPU load, power draw and memory
/// use, the free space of each fixed drive once an hour, and the score of every health check. Old days are rolled up
/// and pruned every few hours. Every failure is logged and swallowed; recording never disturbs the app.
/// </summary>
public sealed class WindowsHistoryService : IHistoryService, IDisposable
{
    private static readonly TimeSpan DriveInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(6);
    private const double BytesPerGb = 1024d * 1024 * 1024;

    private readonly ISensorService _sensors;
    private readonly IPowerService _power;
    private readonly IHealthService _health;
    private readonly ISettingsService _settings;
    private readonly ILogger<WindowsHistoryService> _logger;
    private readonly HistoryFileStore _store;
    private readonly object _gate = new();
    private readonly object _readingsGate = new();
    private readonly HistorySampleAccumulator _readings = new();

    private Timer? _timer;
    private bool _wanted;
    private bool _recording;
    private bool _settingsHooked;
    private int _intervalSeconds;
    private DateTimeOffset _periodStart;
    private DateTimeOffset _lastDrives = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMaintenance = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHealthCaptured = DateTimeOffset.MinValue;
    private bool _writeFailureLogged;
    private int _maintaining;

    public WindowsHistoryService(ISensorService sensors, IPowerService power, IHealthService health, ISettingsService settings,
        ILogger<WindowsHistoryService> logger)
    {
        _sensors = sensors;
        _power = power;
        _health = health;
        _settings = settings;
        _logger = logger;
        _store = new HistoryFileStore(HistoryFileStore.DefaultDirectory());
    }

    public bool IsRecording => _recording;

    public string DataDirectory => _store.Directory;

    public DateTimeOffset? LastRecorded { get; private set; }

    public event EventHandler? Recorded;

    private HistorySettings Options => _settings.Current.History ??= new HistorySettings();

    public void Start()
    {
        lock (_gate)
        {
            _wanted = true;
            if (!_settingsHooked)
            {
                _settingsHooked = true;
                _settings.Changed += OnSettingsChanged;
            }

            ApplyState();
        }

        // Retention applies even while recording is off, so old days still disappear on schedule.
        RunMaintenanceInBackground();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _wanted = false;
            ApplyState();
        }
    }

    public void RecordHealthScore(DateTimeOffset captured, int score)
    {
        if (!Options.Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (captured <= _lastHealthCaptured)
            {
                return;
            }

            _lastHealthCaptured = captured;
        }

        Write(new HistorySample(captured, 0, new Dictionary<string, double> { [HistoryMetrics.Health] = Math.Clamp(score, 0, 100) }));
    }

    public Task<HistoryData> LoadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        Task.Run(() => _store.Read(from, to, cancellationToken), cancellationToken);

    public Task<HistoryStorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken) =>
        Task.Run(_store.GetInfo, cancellationToken);

    public void Dispose()
    {
        Stop();
        if (_settingsHooked)
        {
            _settings.Changed -= OnSettingsChanged;
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        lock (_gate)
        {
            ApplyState();
        }
    }

    /// <summary>Starts, stops or re-times the recorder to match <see cref="_wanted"/> and the settings. Call under the gate.</summary>
    private void ApplyState()
    {
        var options = Options;
        int interval = HistorySettings.ClampInterval(options.SampleIntervalSeconds);
        if (_wanted && options.Enabled)
        {
            if (!_recording)
            {
                StartRecording(interval);
            }
            else if (interval != _intervalSeconds)
            {
                _intervalSeconds = interval;
                _timer?.Change(TimeSpan.FromSeconds(interval), TimeSpan.FromSeconds(interval));
            }

            // A lower retention is not applied straight away: HistorySettings.EffectiveRetention keeps the old one
            // for a day, and the regular maintenance pass deletes the older days after that.
        }
        else if (_recording)
        {
            StopRecording();
        }
    }

    private void StartRecording(int intervalSeconds)
    {
        _intervalSeconds = intervalSeconds;
        lock (_readingsGate)
        {
            _readings.Flush(DateTimeOffset.UtcNow, 0);
            _periodStart = DateTimeOffset.UtcNow;
        }

        _sensors.SnapshotUpdated += OnSnapshot;
        _power.Updated += OnPower;
        try
        {
            _sensors.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The sensor service could not be started for the history recorder.");
        }

        _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(intervalSeconds), TimeSpan.FromSeconds(intervalSeconds));
        _recording = true;
        _logger.LogInformation("History recording started (one sample every {Seconds} s) in {Folder}.", intervalSeconds, _store.Directory);
    }

    private void StopRecording()
    {
        _sensors.SnapshotUpdated -= OnSnapshot;
        _power.Updated -= OnPower;
        _timer?.Dispose();
        _timer = null;
        _recording = false;
        WritePendingSample(DateTimeOffset.UtcNow, includeDrives: false);
        _logger.LogInformation("History recording stopped.");
    }

    private void OnSnapshot(object? sender, SensorSnapshot snapshot)
    {
        try
        {
            var r = TrayReadings.From(snapshot);
            lock (_readingsGate)
            {
                _readings.Add(HistoryMetrics.CpuTemp, r.CpuTempC);
                _readings.Add(HistoryMetrics.GpuTemp, r.GpuTempC);
                _readings.Add(HistoryMetrics.CpuLoad, r.CpuLoadPercent);
                _readings.Add(HistoryMetrics.GpuLoad, r.GpuLoadPercent);
                _readings.Add(HistoryMetrics.Ram, r.RamPercent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A sensor snapshot could not be added to the history sample.");
        }
    }

    private void OnPower(object? sender, PowerSnapshot snapshot)
    {
        var watts = snapshot.EstimatedTotalW ?? (snapshot.OnBattery ? snapshot.BatteryDischargeW : null);
        lock (_readingsGate)
        {
            _readings.Add(HistoryMetrics.Power, watts);
        }
    }

    private void OnTick(object? state)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            WritePendingSample(now, includeDrives: now - _lastDrives >= DriveInterval);
            if (_health.Last is { } report && report.Items.Count > 0)
            {
                RecordHealthScore(report.Captured, report.Score);
            }

            if (now - _lastMaintenance >= MaintenanceInterval)
            {
                RunMaintenanceInBackground();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The history recorder tick failed.");
        }
    }

    /// <summary>Turns the readings collected since the last sample into one line (plus free space when due) and writes it.</summary>
    private void WritePendingSample(DateTimeOffset now, bool includeDrives)
    {
        HistorySample? sample;
        lock (_readingsGate)
        {
            // After sleep the timer fires late; the readings still only cover about one interval.
            int span = (int)Math.Clamp((now - _periodStart).TotalSeconds, 0, Math.Max(1, _intervalSeconds));
            sample = _readings.Flush(TruncateToSecond(now), span);
            _periodStart = now;
        }

        if (includeDrives)
        {
            var drives = ReadFixedDrives();
            if (drives.Count > 0)
            {
                _lastDrives = now;
                var values = new Dictionary<string, double>(sample?.Values ?? new Dictionary<string, double>(), StringComparer.Ordinal);
                foreach (var (drive, freeGb) in drives)
                {
                    values[HistoryMetrics.DiskFree(drive)] = freeGb;
                }

                sample = new HistorySample(TruncateToSecond(now), sample?.SpanSeconds ?? 0, values);
            }
        }

        if (sample is not null)
        {
            Write(sample);
        }
    }

    private void Write(HistorySample sample)
    {
        try
        {
            _store.Append(sample);
            LastRecorded = sample.Time;
            _writeFailureLogged = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full disk or a locked file: log once, keep trying on the next tick.
            if (!_writeFailureLogged)
            {
                _writeFailureLogged = true;
                _logger.LogWarning(ex, "A history sample could not be written to {Folder}.", _store.Directory);
            }

            return;
        }

        try
        {
            Recorded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A history listener failed.");
        }
    }

    private void RunMaintenanceInBackground()
    {
        if (Interlocked.Exchange(ref _maintaining, 1) == 1)
        {
            return;
        }

        _lastMaintenance = DateTimeOffset.UtcNow;
        int retention = Options.EffectiveRetention(DateTimeOffset.UtcNow);
        _ = Task.Run(() =>
        {
            try
            {
                var result = _store.Maintain(HistoryRetention.DayOf(DateTimeOffset.UtcNow), retention);
                if (result.DeletedFiles + result.CompactedFiles > 0)
                {
                    _logger.LogInformation("History tidied: {Deleted} old days deleted, {Compacted} days rolled up, {Dropped} unreadable lines dropped.",
                        result.DeletedFiles, result.CompactedFiles, result.DroppedLines);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "History maintenance failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _maintaining, 0);
            }
        });
    }

    /// <summary>Free space of every ready fixed drive in GB (as Explorer counts them). Drives that cannot be read are skipped.</summary>
    private List<(string Drive, double FreeGb)> ReadFixedDrives()
    {
        var result = new List<(string, double)>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "The drive list could not be read.");
            return result;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    result.Add((drive.Name, drive.TotalFreeSpace / BytesPerGb));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Free space of {Drive} could not be read.", drive.Name);
            }
        }

        return result;
    }

    private static DateTimeOffset TruncateToSecond(DateTimeOffset time) => DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds());
}
