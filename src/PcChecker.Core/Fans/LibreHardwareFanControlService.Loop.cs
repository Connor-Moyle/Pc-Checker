using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.Fans;

/// <summary>The one-second loop: fresh RPM/percent readings, curve evaluation, the thermal safety net and the crash marker.</summary>
public sealed partial class LibreHardwareFanControlService
{
    private const int TickMs = 1000;
    private const int MaxAlerts = 50;

    /// <summary>A sensor snapshot older than this is treated as missing, so a stalled sensor loop can never drive a curve from a frozen temperature.</summary>
    internal static readonly TimeSpan MaxTemperatureAge = TimeSpan.FromSeconds(5);
    private int _ticking;
    private bool _forcedEpisode;

    private FanEntry? Find(string fanId)
    {
        lock (_gate)
        {
            return _entries.FirstOrDefault(e => e.Id.Equals(fanId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void EnsureTimer()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _timer ??= new Timer(_ => Tick(), null, TickMs, TickMs);
        }
    }

    /// <summary>Current view of every fan (controllable first, then read-only ones).</summary>
    private IReadOnlyList<FanDevice> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<FanDevice>(_entries.Count + _rpmOnly.Count);
            foreach (var e in _entries)
            {
                list.Add(new FanDevice(e.Id, e.Name, e.Kind, e.HardwareName, e.LastRpm, e.LastPercent, true, e.MinPercent, e.MaxPercent,
                    e.Mode, e.Id, e.RpmSensor?.Identifier.ToString()));
            }

            foreach (var r in _rpmOnly)
            {
                list.Add(new FanDevice(r.Id, r.Name, r.Kind, r.HardwareName, r.LastRpm, null, false, 0, 100, FanMode.Auto, null, r.Id));
            }

            return list;
        }
    }

    /// <summary>Writes a software value to the hardware. Caller updates the entry's mode on success.</summary>
    private OperationResult ApplySoftware(FanEntry entry, double percent, string why)
    {
        OperationResult? result = null;
        _host.WithComputer(_ =>
        {
            try
            {
                entry.Control.SetSoftware((float)percent);
                entry.LastApplied = percent;
                entry.LastPercent = percent;
                result = OperationResult.Ok($"{entry.Name} set to {percent:0} % ({why}).");
            }
            catch (Exception ex)
            {
                result = OperationResult.Fail($"{entry.Name} did not accept the new speed: {ex.Message}", ex);
            }
        });

        if (result is null)
        {
            return OperationResult.Fail("The hardware monitor is not open, so the fan speed could not be changed.");
        }

        if (!result.Success)
        {
            _logger.LogWarning("SetSoftware failed for {Fan}: {Message}", entry.Id, result.Message);
        }

        return result;
    }

    /// <summary>Hands a fan back to the firmware and clears its software state.</summary>
    private OperationResult ApplyDefault(FanEntry entry)
    {
        OperationResult? result = null;
        _host.WithComputer(_ =>
        {
            try
            {
                entry.Control.SetDefault();
                result = OperationResult.Ok($"{entry.Name} is back under automatic control.");
            }
            catch (Exception ex)
            {
                result = OperationResult.Fail($"{entry.Name} could not be returned to automatic control: {ex.Message}", ex);
            }
        });

        lock (_gate)
        {
            entry.Mode = FanMode.Auto;
            entry.ManualPercent = null;
            entry.Curve = null;
            entry.LastApplied = null;
            entry.Forced = false;
            entry.MissingTemperatureTicks = 0;
        }

        return result ?? OperationResult.Fail("The hardware monitor is not open, so the fan could not be reverted.");
    }

    private OperationResult ApplyProfileCore(FanProfile profile)
    {
        var applied = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();
        var temps = CollectTemperatures();

        foreach (var (fanId, assignment) in profile.Fans)
        {
            var entry = Find(fanId);
            if (entry is null)
            {
                skipped.Add(fanId);
                continue;
            }

            OperationResult result;
            switch (assignment.Mode)
            {
                case FanMode.Manual:
                    double manual = FanSafetyRules.ClampManual(assignment.ManualPercent ?? 50, _settings.Current.Fans.MinimumPercent, false, entry.MinPercent, entry.MaxPercent);
                    result = ApplySoftware(entry, manual, $"profile {profile.Name}");
                    if (result.Success)
                    {
                        lock (_gate)
                        {
                            entry.Mode = FanMode.Manual;
                            entry.ManualPercent = manual;
                            entry.Curve = null;
                            entry.Forced = false;
                        }
                    }

                    break;
                case FanMode.Curve when assignment.Curve is not null:
                    var problem = FanCurveMath.Validate(assignment.Curve);
                    if (problem is not null)
                    {
                        failed.Add($"{entry.Name}: {problem}");
                        continue;
                    }

                    result = ApplyCurveCore(entry, assignment.Curve, $"profile {profile.Name}", temps);
                    break;
                default:
                    result = ApplyDefault(entry);
                    break;
            }

            if (result.Success)
            {
                applied.Add(entry.Name);
            }
            else
            {
                failed.Add($"{entry.Name}: {result.Message}");
            }
        }

        ActiveProfile = FanCurveMath.Clone(profile);
        UpdateSessionFile();
        EnsureTimer();
        FansUpdated?.Invoke(this, Snapshot());

        if (applied.Count == 0 && failed.Count == 0)
        {
            return OperationResult.Fail($"Profile \"{profile.Name}\" does not mention any fan on this PC ({skipped.Count} unknown fan id(s)). Save a new profile from the current fans instead.");
        }

        string summary = $"Profile \"{profile.Name}\" applied to {applied.Count} fan(s).";
        if (skipped.Count > 0)
        {
            summary += $" {skipped.Count} fan(s) in the profile were not found on this PC.";
        }

        return failed.Count == 0 ? OperationResult.Ok(summary) : OperationResult.Fail(summary + " Problems: " + string.Join("; ", failed));
    }

    /// <summary>Puts a fan on a (validated) curve and applies the first value right away.</summary>
    private OperationResult ApplyCurveCore(FanEntry entry, FanCurve source, string why, Dictionary<string, double>? temps = null)
    {
        temps ??= CollectTemperatures();
        var curve = FanCurveMath.Clone(source);
        curve.Points = FanCurveMath.Normalize(curve.Points);
        double? temp = temps.TryGetValue(curve.TemperatureSensorId, out var t) ? t : null;
        double target = temp is null ? Math.Max(50, _settings.Current.Fans.MinimumPercent) : FanCurveMath.Evaluate(curve, temp.Value, null);
        target = FanSafetyRules.ClampManual(target, _settings.Current.Fans.MinimumPercent, false, entry.MinPercent, entry.MaxPercent);
        var result = ApplySoftware(entry, target, $"{why}, {(temp is null ? "no temperature yet" : $"{temp:0} °C")}");
        if (result.Success)
        {
            lock (_gate)
            {
                entry.Mode = FanMode.Curve;
                entry.Curve = curve;
                entry.ManualPercent = null;
                entry.Forced = false;
                entry.MissingTemperatureTicks = 0;
            }

            UpdateSessionFile();
            EnsureTimer();
        }

        return result;
    }

    /// <summary>
    /// Latest temperature by sensor id: from the sensor service when its snapshot is fresh (see <see cref="MaxTemperatureAge"/>),
    /// otherwise straight from the hardware monitor after refreshing the hardware that owns each sensor. Stale or
    /// unreadable sensors are left out of the map, which is what makes the "temperature source missing" safety fire.
    /// </summary>
    private Dictionary<string, double> CollectTemperatures()
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (_sensors.Latest is { } latest && IsFresh(latest.Timestamp))
            {
                foreach (var r in latest.OfKind(SensorKind.Temperature))
                {
                    if (r.Value is { } v && !double.IsNaN(v))
                    {
                        map[r.Id] = v;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read temperatures from the sensor service.");
        }

        KeyValuePair<string, ISensor>[] lhm;
        lock (_gate)
        {
            lhm = _lhmTemperatures.Where(kv => !map.ContainsKey(kv.Key)).ToArray();
        }

        if (lhm.Length > 0)
        {
            _host.WithComputer(_ =>
            {
                // Refresh each piece of hardware once so the fallback never returns the value cached at discovery.
                foreach (var hw in lhm.Select(kv => kv.Value.Hardware).Where(h => h is not null).Distinct())
                {
                    try
                    {
                        hw!.Update();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Update failed for {Hardware} while reading temperatures.", hw!.Name);
                    }
                }

                foreach (var (id, sensor) in lhm)
                {
                    if (sensor.Value is { } v && !float.IsNaN(v))
                    {
                        map[id] = v;
                    }
                }
            });
        }

        return map;
    }

    private static bool IsFresh(DateTimeOffset timestamp) => DateTimeOffset.UtcNow - timestamp.ToUniversalTime() <= MaxTemperatureAge;

    private IReadOnlyList<SensorReading> CurrentTemperatureReadings()
    {
        try
        {
            if (_sensors.Latest is not { } latest || IsFresh(latest.Timestamp))
            {
                var fromService = _sensors.GetTemperatures();
                if (fromService.Count > 0)
                {
                    return fromService;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sensor service temperatures unavailable for the safety check.");
        }

        var list = new List<SensorReading>();
        KeyValuePair<string, ISensor>[] lhm;
        lock (_gate)
        {
            lhm = _lhmTemperatures.ToArray();
        }

        _host.WithComputer(_ =>
        {
            foreach (var hw in lhm.Select(kv => kv.Value.Hardware).Where(h => h is not null).Distinct())
            {
                try
                {
                    hw!.Update();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Update failed for {Hardware} while reading temperatures.", hw!.Name);
                }
            }

            foreach (var (id, sensor) in lhm)
            {
                list.Add(new SensorReading(id, sensor.Hardware?.Name ?? string.Empty, MapKind(sensor.Hardware), SensorKind.Temperature, sensor.Name, sensor.Value, null, null, "°C"));
            }
        });
        return list;
    }

    private void Tick()
    {
        if (_disposed || Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fan loop tick failed; will retry next second.");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private void TickCore()
    {
        List<FanEntry> entries;
        List<RpmOnlyEntry> rpmOnly;
        List<IHardware> hardware;
        lock (_gate)
        {
            entries = _entries.ToList();
            rpmOnly = _rpmOnly.ToList();
            hardware = _fanHardware.ToList();
        }

        if (entries.Count == 0 && rpmOnly.Count == 0)
        {
            return;
        }

        // 1. Fresh readings.
        _host.WithComputer(_ =>
        {
            foreach (var hw in hardware)
            {
                try
                {
                    hw.Update();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Update failed for {Hardware}.", hw.Name);
                }
            }

            foreach (var e in entries)
            {
                e.LastRpm = e.RpmSensor?.Value;
                e.LastPercent = e.ControlSensor.Value ?? e.LastApplied;
            }

            foreach (var r in rpmOnly)
            {
                r.LastRpm = r.Sensor.Value;
            }
        });

        // 2. Safety net: anything critical while we drive fans below 100 % → full speed.
        var software = entries.Where(e => e.Mode != FanMode.Auto).ToList();
        if (software.Count > 0)
        {
            var monitoring = _settings.Current.Monitoring;
            var readings = CurrentTemperatureReadings();
            var critical = FanSafetyRules.FindCriticalReading(readings, monitoring);
            if (critical is not null)
            {
                foreach (var e in software.Where(e => (e.LastApplied ?? 0) < 100))
                {
                    if (ApplySoftware(e, 100, "safety").Success)
                    {
                        e.Forced = true;
                    }
                }

                if (!_forcedEpisode)
                {
                    _forcedEpisode = true;
                    var text = $"{critical.HardwareName} {critical.Name} reached {critical.Value:0} °C (critical limit {FanSafetyRules.CriticalThreshold(critical.Hardware, monitoring):0} °C). All software-controlled fans were forced to 100 % until it cools down.";
                    RaiseAlert(FanAlertKind.ForcedFullSpeed, string.Empty, "All controlled fans", text, Severity.High);
                    _logger.LogWarning("Fan safety: {Text}", text);
                }
            }
            else if (_forcedEpisode && FanSafetyRules.CanRelease(readings, monitoring))
            {
                _forcedEpisode = false;
                foreach (var e in software)
                {
                    e.Forced = false;
                    if (e.Mode == FanMode.Manual && e.ManualPercent is { } manual)
                    {
                        ApplySoftware(e, manual, "manual resumed");
                    }
                }

                _logger.LogInformation("Fan safety: temperatures are back to normal; fan control resumed.");
            }
        }

        // 3. Curves.
        var curves = software.Where(e => e.Mode == FanMode.Curve && e.Curve is not null && !e.Forced).ToList();
        if (curves.Count > 0)
        {
            var temps = CollectTemperatures();
            foreach (var e in curves)
            {
                var curve = e.Curve!;
                if (!temps.TryGetValue(curve.TemperatureSensorId, out var temp))
                {
                    if (++e.MissingTemperatureTicks == 10)
                    {
                        if (ApplySoftware(e, 100, "temperature source missing").Success)
                        {
                            RaiseAlert(FanAlertKind.TemperatureSourceMissing, e.Id, e.Name,
                                $"The temperature \"{curve.TemperatureSensorId}\" that drives {e.Name} has not reported a fresh value for 10 seconds, so the fan was set to 100 % to stay safe.", Severity.Medium);
                        }
                    }

                    continue;
                }

                e.MissingTemperatureTicks = 0;
                double target = FanCurveMath.Evaluate(curve, temp, e.LastApplied);
                target = FanSafetyRules.ClampManual(target, _settings.Current.Fans.MinimumPercent, false, e.MinPercent, e.MaxPercent);
                if (e.LastApplied is null || Math.Abs(target - e.LastApplied.Value) >= 1)
                {
                    ApplySoftware(e, target, $"curve at {temp:0} °C");
                }
            }
        }

        FansUpdated?.Invoke(this, Snapshot());
    }

    private void UpdateSessionFile()
    {
        try
        {
            List<string> ids;
            lock (_gate)
            {
                ids = _entries.Where(e => e.Mode != FanMode.Auto).Select(e => e.Id).ToList();
            }

            if (ids.Count == 0)
            {
                FanSessionStore.Delete(SessionFilePath);
                return;
            }

            var existing = FanSessionStore.TryRead(SessionFilePath);
            var session = existing is { ProcessId: var pid } && pid == Environment.ProcessId ? existing : new FanSession { ProcessId = Environment.ProcessId };
            session.UpdatedUtc = DateTimeOffset.UtcNow;
            session.ProfileName = ActiveProfile?.Name;
            session.FanIds = ids;
            FanSessionStore.Write(SessionFilePath, session);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not update the fan session marker.");
        }
    }

    private void RaiseAlert(FanAlertKind kind, string fanId, string fanName, string message, Severity severity)
    {
        var alert = new FanControlAlert(DateTimeOffset.Now, kind, fanId, fanName, message, severity);
        lock (_gate)
        {
            _alerts.Add(alert);
            if (_alerts.Count > MaxAlerts)
            {
                _alerts.RemoveAt(0);
            }
        }

        AlertRaised?.Invoke(this, alert);
    }
}
