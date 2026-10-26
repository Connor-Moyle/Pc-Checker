using System.Diagnostics;
using System.Net.NetworkInformation;
using PcChecker.App.Helpers;
using PcChecker.Core.Common;
using PcChecker.Core.Power;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.App.Services;

/// <summary>
/// Turns the current sensor and power snapshots plus a few system counters (RAM, network throughput, disk activity)
/// into one value per overlay metric. Sensor name heuristics live in <see cref="OverlaySensorMatcher"/> (Core, pure);
/// this class adds the Windows-only sources and prefers them where they are more reliable.
/// Every system call is guarded: a failing source simply yields "--" for that metric.
/// </summary>
public sealed class OverlayMetricResolver : IDisposable
{
    private readonly ISensorService _sensors;
    private readonly IPowerService _power;
    private readonly Stopwatch _networkClock = new();
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private double _downMBps;
    private double _upMBps;
    private bool _networkFailed;
    private PerformanceCounter? _diskCounter;
    private bool _diskCounterFailed;

    public OverlayMetricResolver(ISensorService sensors, IPowerService power)
    {
        _sensors = sensors;
        _power = power;
    }

    /// <summary>Resolves every requested metric at once (one network/disk sample per call).</summary>
    public Dictionary<string, OverlayMetricValue> ResolveAll(IEnumerable<string> metricIds)
    {
        var ids = metricIds.ToList();
        var snapshot = SafeLatestSensors();
        var power = SafeLatestPower();
        bool wantsNetwork = ids.Contains(OverlayMetricIds.NetDown) || ids.Contains(OverlayMetricIds.NetUp);
        if (wantsNetwork)
        {
            SampleNetwork();
        }

        var result = new Dictionary<string, OverlayMetricValue>(ids.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            result[id] = Resolve(id, snapshot, power);
        }

        return result;
    }

    /// <summary>Resolves one metric from the given snapshots (system counters are read on demand).</summary>
    public OverlayMetricValue Resolve(string metricId, SensorSnapshot? snapshot, PowerSnapshot? power)
    {
        switch (metricId)
        {
            case OverlayMetricIds.RamUsed:
            {
                var mem = NativeMethods.QueryMemory();
                return mem is { } m ? new(m.UsedBytes / (1024.0 * 1024 * 1024), "GB") : OverlaySensorMatcher.Resolve(snapshot, metricId);
            }

            case OverlayMetricIds.RamPercent:
            {
                var mem = NativeMethods.QueryMemory();
                return mem is { } m ? new(m.LoadPercent, "%") : OverlaySensorMatcher.Resolve(snapshot, metricId);
            }

            case OverlayMetricIds.NetDown:
                return _networkFailed ? OverlaySensorMatcher.Resolve(snapshot, metricId) : new(_downMBps, "MB/s");

            case OverlayMetricIds.NetUp:
                return _networkFailed ? OverlaySensorMatcher.Resolve(snapshot, metricId) : new(_upMBps, "MB/s");

            case OverlayMetricIds.DiskActivity:
            {
                var fromSensors = OverlaySensorMatcher.Resolve(snapshot, metricId);
                return fromSensors.HasValue ? fromSensors : ReadDiskCounter();
            }

            case OverlayMetricIds.BatteryPercent:
                return power?.BatteryPercent is { } percent ? new(percent, "%") : OverlaySensorMatcher.Resolve(snapshot, metricId);

            case OverlayMetricIds.BatteryWatts:
                return power?.BatteryDischargeW is { } watts ? new(watts, "W") : OverlaySensorMatcher.Resolve(snapshot, metricId);

            case OverlayMetricIds.SystemPower:
                return power?.EstimatedTotalW is { } total ? new(total, "W") : OverlaySensorMatcher.Resolve(snapshot, metricId);

            case OverlayMetricIds.CpuPower:
            {
                var fromSensors = OverlaySensorMatcher.Resolve(snapshot, metricId);
                return fromSensors.HasValue ? fromSensors : power?.CpuPackageW is { } cpu ? new(cpu, "W") : OverlayMetricValue.None;
            }

            case OverlayMetricIds.GpuPower:
            {
                var fromSensors = OverlaySensorMatcher.Resolve(snapshot, metricId);
                return fromSensors.HasValue ? fromSensors : power?.GpuW is { } gpu ? new(gpu, "W") : OverlayMetricValue.None;
            }

            default:
                return OverlaySensorMatcher.Resolve(snapshot, metricId);
        }
    }

    public void Dispose()
    {
        try
        {
            _diskCounter?.Dispose();
        }
        catch
        {
            // Nothing to clean up.
        }

        _diskCounter = null;
    }

    private SensorSnapshot? SafeLatestSensors()
    {
        try
        {
            return _sensors.Latest;
        }
        catch
        {
            return null;
        }
    }

    private PowerSnapshot? SafeLatestPower()
    {
        try
        {
            return _power.Latest;
        }
        catch
        {
            return null;
        }
    }

    private void SampleNetwork()
    {
        if (_networkFailed)
        {
            return;
        }

        try
        {
            long received = 0, sent = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }

            if (!_networkClock.IsRunning)
            {
                _networkClock.Start();
                _lastBytesReceived = received;
                _lastBytesSent = sent;
                return;
            }

            double seconds = _networkClock.Elapsed.TotalSeconds;
            if (seconds < 0.25)
            {
                return;
            }

            _downMBps = Math.Max(0, received - _lastBytesReceived) / seconds / (1024.0 * 1024.0);
            _upMBps = Math.Max(0, sent - _lastBytesSent) / seconds / (1024.0 * 1024.0);
            _lastBytesReceived = received;
            _lastBytesSent = sent;
            _networkClock.Restart();
        }
        catch
        {
            _networkFailed = true;
        }
    }

    private OverlayMetricValue ReadDiskCounter()
    {
        if (_diskCounterFailed || !PlatformGuard.IsWindows)
        {
            return OverlayMetricValue.None;
        }

        try
        {
            _diskCounter ??= new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", readOnly: true);
            double value = Math.Clamp(_diskCounter.NextValue(), 0, 100);
            return new(value, "%");
        }
        catch
        {
            _diskCounterFailed = true;
            return OverlayMetricValue.None;
        }
    }
}
