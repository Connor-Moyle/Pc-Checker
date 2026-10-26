using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Sensors;

namespace PcChecker.Core.Fans;

/// <summary>Startup recovery, shutdown, temperature sources, the raw control list and the capability summary.</summary>
public sealed partial class LibreHardwareFanControlService
{
    public IReadOnlyList<FanTemperatureSource> GetTemperatureSources()
    {
        var list = new List<FanTemperatureSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var t in _sensors.GetTemperatures())
            {
                if (seen.Add(t.Id))
                {
                    list.Add(new FanTemperatureSource(t.Id, $"{t.HardwareName} — {t.Name}", t.Hardware, t.Value));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sensor service temperatures unavailable; falling back to the hardware monitor.");
        }

        if (list.Count == 0)
        {
            // Copy under the gate, read through the host outside it: the host lock can be held by a slow hardware
            // update. This takes the host lock, so UI callers must invoke it from a background thread.
            KeyValuePair<string, ISensor>[] lhm;
            lock (_gate)
            {
                lhm = _lhmTemperatures.ToArray();
            }

            if (lhm.Length > 0)
            {
                var values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
                _host.WithComputer(_ =>
                {
                    foreach (var (id, sensor) in lhm)
                    {
                        values[id] = sensor.Value;
                    }
                });

                foreach (var (id, sensor) in lhm)
                {
                    if (seen.Add(id))
                    {
                        list.Add(new FanTemperatureSource(id, $"{sensor.Hardware?.Name} — {sensor.Name}", MapKind(sensor.Hardware), values.TryGetValue(id, out var v) ? v : null));
                    }
                }
            }
        }

        return list.OrderBy(s => s.Hardware).ThenBy(s => s.Label).ToList();
    }

    public IReadOnlyList<RawFanControl> GetRawControls()
    {
        List<FanEntry> entries;
        lock (_gate)
        {
            entries = _entries.ToList();
        }

        var result = new List<RawFanControl>(entries.Count);
        _host.WithComputer(_ =>
        {
            foreach (var e in entries)
            {
                try
                {
                    var c = e.Control;
                    result.Add(new RawFanControl(e.Id, e.HardwareName, e.ControlSensor.Name, c.ControlMode.ToString(),
                        c.ControlMode == ControlMode.Software ? c.SoftwareValue : null, c.MinSoftwareValue, c.MaxSoftwareValue,
                        e.ControlSensor.Value, e.RpmSensor?.Identifier.ToString()));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not read raw control {Id}.", e.Id);
                }
            }
        });
        return result;
    }

    /// <summary>
    /// Called once at app start. A marker left by a crashed run means its fans were abandoned under software control:
    /// they are returned to automatic and the user is warned. A marker flagged as a clean exit (the previous session
    /// ended normally with "revert on exit" off) is handled quietly: the profile it names is re-applied so the curves
    /// and the safety loop take over again, or the fans go back to automatic with an accurate message. Then the saved
    /// profile is applied when "apply on startup" is on. Returns plain-English notices for the dashboard.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunStartupAsync(CancellationToken cancellationToken)
    {
        var notices = new List<string>();
        string? reappliedProfile = null;
        var session = FanSessionStore.TryRead(SessionFilePath);
        if (session is not null && (session.CleanExit || FanSessionStore.IsStale(session, FanSessionStore.LookupProcess)))
        {
            bool crashed = !session.CleanExit;
            try
            {
                await DiscoverAsync(cancellationToken).ConfigureAwait(false);
                var previousProfile = crashed ? null : FindProfile(session.ProfileName);
                if (previousProfile is not null)
                {
                    FanSessionStore.Delete(SessionFilePath);
                    var reapply = await ApplyProfileAsync(previousProfile, cancellationToken).ConfigureAwait(false);
                    if (reapply.Success)
                    {
                        reappliedProfile = previousProfile.Name;
                        notices.Add($"Fan profile \"{previousProfile.Name}\" was still in charge of {session.FanIds.Count} fan(s) when PC Checker last closed (revert on exit is off), so it was re-applied.");
                        _logger.LogInformation("Re-applied fan profile {Profile} left active by the previous session.", previousProfile.Name);
                    }
                    else
                    {
                        _logger.LogWarning("Could not re-apply fan profile {Profile} from the previous session: {Message}", previousProfile.Name, reapply.Message);
                    }
                }

                if (reappliedProfile is null)
                {
                    int reverted = 0;
                    foreach (var id in session.FanIds)
                    {
                        var entry = Find(id);
                        if (entry is not null && ApplyDefault(entry).Success)
                        {
                            reverted++;
                        }
                    }

                    FanSessionStore.Delete(SessionFilePath);
                    string text;
                    if (crashed)
                    {
                        text = reverted > 0
                            ? $"PC Checker did not shut down cleanly last time while {session.FanIds.Count} fan(s) were under its control. {reverted} fan(s) were returned to automatic control."
                            : "PC Checker did not shut down cleanly last time while it was controlling fans. The fans were already back under automatic control.";
                        RaiseAlert(FanAlertKind.RecoveredFromCrash, string.Empty, "All fans", text, Severity.Medium);
                        _logger.LogWarning("Recovered from a previous fan-control session: {Text}", text);
                    }
                    else
                    {
                        text = reverted > 0
                            ? $"{session.FanIds.Count} fan(s) were left under manual control when PC Checker last closed (revert on exit is off). {reverted} fan(s) were returned to automatic control at startup for safety; turn on \"Apply on startup\" to bring a profile back automatically."
                            : "Fans were left under manual control when PC Checker last closed (revert on exit is off); they were already back under automatic control.";
                        _logger.LogInformation("Previous session ended cleanly with fans under manual control: {Text}", text);
                    }

                    notices.Add(text);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not revert fans from the previous session.");
                notices.Add(crashed
                    ? "PC Checker could not confirm the fans were returned to automatic control after the last unclean shutdown. Open the Fans page and click \"Revert all to automatic\"."
                    : "PC Checker could not confirm the fans it left under manual control last time were returned to automatic. Open the Fans page and click \"Revert all to automatic\".");
            }
        }

        var fanSettings = _settings.Current.Fans;
        if (fanSettings.ApplyOnStartup && !string.IsNullOrWhiteSpace(fanSettings.ActiveProfileName)
            && !fanSettings.ActiveProfileName.Equals(reappliedProfile, StringComparison.OrdinalIgnoreCase))
        {
            var profile = FindProfile(fanSettings.ActiveProfileName);
            if (profile is not null)
            {
                var result = await ApplyProfileAsync(profile, cancellationToken).ConfigureAwait(false);
                notices.Add(result.Success
                    ? $"Fan profile \"{profile.Name}\" was applied at startup."
                    : $"Fan profile \"{profile.Name}\" could not be applied at startup: {result.Message}");
            }
        }

        return notices;
    }

    private FanProfile? FindProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return _settings.Current.Fans.Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? BuiltInProfiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Called at app exit: reverts to automatic when the setting says so; otherwise flags the session marker as a clean
    /// exit so the next start does not mistake the fans it deliberately left alone for a crash.
    /// </summary>
    public async Task ShutdownAsync()
    {
        try
        {
            if (_settings.Current.Fans.RevertToAutoOnExit)
            {
                await RevertAllToAutoAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                FanSessionStore.MarkCleanExit(SessionFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reverting fans on exit failed.");
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _timer = null;
        _discoveryGate.Dispose();
    }
    private void BuildCapabilityNotes()
    {
        _capabilityNotes.Clear();
        foreach (var group in _entries.GroupBy(e => e.HardwareId))
        {
            var first = group.First();
            string what = first.Kind switch
            {
                HardwareKind.Gpu => "GPU fans",
                HardwareKind.Cooler => "Cooler / pump",
                HardwareKind.Psu => "Power supply fan",
                _ => "Motherboard fan headers (CPU and case fans)",
            };
            _capabilityNotes.Add($"{what}: {first.HardwareName} — {group.Count()} controllable");
        }

        foreach (var group in _rpmOnly.GroupBy(r => r.HardwareName))
        {
            _capabilityNotes.Add($"Read-only: {group.Key} reports {group.Count()} fan speed(s) but exposes no control");
        }

        if (_entries.Count == 0 && _rpmOnly.Count == 0)
        {
            _capabilityNotes.Add("No fan sensors were found by the hardware monitor.");
        }
    }

    private string DescribeUnavailable()
    {
        if (_rpmOnly.Count > 0)
        {
            return $"This PC reports {_rpmOnly.Count} fan speed(s) but no fan controls the app can drive. Laptops and many pre-built desktops only let the firmware (BIOS/EC) control the fans; use the manufacturer's utility or the BIOS fan settings instead.";
        }

        return "No fans were detected. If this is a laptop or a pre-built desktop, the fans are managed by the firmware and cannot be read here. On a desktop, make sure PC Checker runs as administrator so the motherboard sensor chip can be read.";
    }

    private IReadOnlyList<FanProfile> BuildBuiltInProfiles()
    {
        var sources = GetTemperatureSources();
        string cpu = PickSensor(sources, HardwareKind.Cpu, "package", "tctl", "tdie", "core") ?? "cpu.package";
        string gpu = PickSensor(sources, HardwareKind.Gpu, "core", "hot") ?? "gpu.core";
        List<string> gpuFans;
        List<string> cpuFans;
        lock (_gate)
        {
            gpuFans = _entries.Where(e => e.Kind == HardwareKind.Gpu).Select(e => e.Id).ToList();
            cpuFans = _entries.Where(e => e.Kind != HardwareKind.Gpu).Select(e => e.Id).ToList();
        }

        if (cpuFans.Count == 0 && gpuFans.Count == 0)
        {
            return FanCurveMath.BuiltInProfiles(cpu, gpu);
        }

        return FanCurveMath.BuiltInProfiles(cpu, gpu, cpuFans, gpuFans);
    }

    private static string? PickSensor(IReadOnlyList<FanTemperatureSource> sources, HardwareKind kind, params string[] preferredWords)
    {
        var ofKind = sources.Where(s => s.Hardware == kind).ToList();
        foreach (var word in preferredWords)
        {
            var hit = ofKind.FirstOrDefault(s => s.Label.Contains(word, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit.Id;
            }
        }

        return ofKind.FirstOrDefault()?.Id;
    }
}
