using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.History;
using PcChecker.Core.Processes;
using PcChecker.Core.Settings;

namespace PcChecker.Core.AppHistory;

/// <summary>
/// PC Checker's own recording: while PC Checker runs (also from the tray) and History recording is on, a timer reads
/// every process's processor time and disk bytes once a minute (the same light Win32 calls as the Processes page),
/// <see cref="AppUsageAccumulator"/> adds up the difference per app, and every ten minutes the totals are appended to
/// <see cref="AppUsageFileStore"/>. Older days are rolled up and the History retention setting applies. Every failure
/// is logged and swallowed; recording never disturbs the app.
/// </summary>
public sealed partial class WindowsAppHistoryService
{
    private static readonly TimeSpan ReadInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WriteInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(6);

    private readonly object _recorderGate = new();
    private readonly AppUsageAccumulator _accumulator = new();
    private readonly Dictionary<string, string> _pathCache = new(StringComparer.Ordinal);
    private Timer? _timer;
    private bool _wanted;
    private bool _recording;
    private bool _settingsHooked;
    private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMaintenance = DateTimeOffset.MinValue;
    private int _maintaining;
    private int _ticking;
    private bool _writeFailureLogged;

    public bool IsRecording => _recording;

    public void Start()
    {
        lock (_recorderGate)
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
        lock (_recorderGate)
        {
            _wanted = false;
            ApplyState();
        }
    }

    private void UnhookSettings()
    {
        if (_settingsHooked)
        {
            _settings.Changed -= OnSettingsChanged;
            _settingsHooked = false;
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        lock (_recorderGate)
        {
            ApplyState();
        }
    }

    /// <summary>Starts or stops the timer to match <see cref="_wanted"/> and the History setting. Call under the gate.</summary>
    private void ApplyState()
    {
        if (_wanted && HistoryOptions.Enabled && PlatformGuard.IsWindows)
        {
            if (!_recording)
            {
                _accumulator.Reset();
                _lastWrite = DateTimeOffset.UtcNow;
                _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(5), ReadInterval);
                _recording = true;
                _logger.LogInformation("App usage recording started in {Folder}.", _store.Directory);
            }
        }
        else if (_recording)
        {
            _timer?.Dispose();
            _timer = null;
            _recording = false;
            WritePending(DateTimeOffset.UtcNow);
            _accumulator.Reset();
            _logger.LogInformation("App usage recording stopped.");
        }
    }

    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            var readings = ReadProcesses();
            var now = DateTimeOffset.UtcNow;
            lock (_recorderGate)
            {
                if (!_recording)
                {
                    return;
                }

                _accumulator.Add(readings, now, Environment.ProcessorCount);
                if (now - _lastWrite >= WriteInterval)
                {
                    WritePending(now);
                }
            }

            if (now - _lastMaintenance >= MaintenanceInterval)
            {
                RunMaintenanceInBackground();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The app usage recorder tick failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>Writes what was added up so far (the page calls this before it reads, so the last minutes show).</summary>
    private void FlushRecorder()
    {
        lock (_recorderGate)
        {
            if (_recording)
            {
                WritePending(DateTimeOffset.UtcNow);
            }
        }
    }

    /// <summary>Drops usage that was added up but not written yet (before the recording is deleted).</summary>
    private void DiscardPendingUsage()
    {
        lock (_recorderGate)
        {
            _accumulator.Flush(DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Appends the period's totals. Call under the recorder gate.</summary>
    private void WritePending(DateTimeOffset now)
    {
        _lastWrite = now;
        var records = _accumulator.Flush(now);
        if (records.Count == 0)
        {
            return;
        }

        try
        {
            _store.Append(records);
            _writeFailureLogged = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_writeFailureLogged)
            {
                _writeFailureLogged = true;
                _logger.LogWarning(ex, "App usage could not be written to {Folder}.", _store.Directory);
            }
        }
    }

    /// <summary>Every process's counters, keyed by its program file (or its name when the path cannot be read).</summary>
    private List<AppProcessReading> ReadProcesses()
    {
        var entries = ProcessNative.EnumerateProcesses();
        var readings = new List<AppProcessReading>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Pid <= 0)
            {
                continue;
            }

            var reading = ProcessNative.Read(entry.Pid);
            if (!reading.Ok)
            {
                continue;
            }

            var cacheKey = entry.Pid + ":" + reading.CreationFileTime;
            seen.Add(cacheKey);
            if (!_pathCache.TryGetValue(cacheKey, out var appKey))
            {
                var (path, _) = ProcessNative.ReadIdentity(entry.Pid);
                appKey = AppNames.KeyFor(string.IsNullOrWhiteSpace(path) ? entry.Name : path);
                _pathCache[cacheKey] = appKey;
            }

            readings.Add(new AppProcessReading(entry.Pid, ProcessNative.FromFileTime(reading.CreationFileTime), appKey,
                TimeSpan.FromTicks(reading.CpuTicks), reading.ReadBytes, reading.WriteBytes));
        }

        foreach (var stale in _pathCache.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _pathCache.Remove(stale);
        }

        return readings;
    }

    private void RunMaintenanceInBackground()
    {
        if (Interlocked.Exchange(ref _maintaining, 1) == 1)
        {
            return;
        }

        _lastMaintenance = DateTimeOffset.UtcNow;
        int retention = HistoryOptions.EffectiveRetention(DateTimeOffset.UtcNow);
        _ = Task.Run(() =>
        {
            try
            {
                var (deleted, compacted) = _store.Maintain(HistoryRetention.DayOf(DateTimeOffset.UtcNow), retention);
                if (deleted + compacted > 0)
                {
                    _logger.LogInformation("App usage tidied: {Deleted} old days deleted, {Compacted} days rolled up to hours.", deleted, compacted);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "App usage maintenance failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _maintaining, 0);
            }
        });
    }
}
