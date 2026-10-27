using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;
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
    /// Called once at app start. A marker left by a crashed run means its fans were abandoned under software control.
    /// Graphics card fans are returned to automatic. Motherboard fans cannot be: the fan chip keeps the manual speed
    /// until the PC restarts, because LibreHardwareMonitor only restores the firmware setting it saved itself in the
    /// same process. Those fans are kept under software control (Balanced curve, overheating protection on) and the
    /// marker remembers them until the PC restarts. Fans that cannot be found keep the marker too, so a later start can
    /// still recover them. A marker flagged as a clean exit (the previous session ended normally with "revert on exit"
    /// off) is handled quietly: closing the hardware monitor already handed the fans back to the firmware, so the profile
    /// it names is simply re-applied and the curves and the safety loop take over again. Then the saved profile is
    /// applied when "apply on startup" is on. Returns plain-English notices for the dashboard.
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
                bool rebooted = FanSessionStore.RebootedSince(session, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(Environment.TickCount64));
                var leftOver = FanSessionStore.FansWithoutFirmwareControl(session, rebooted);
                var recovery = await RecoverLeftOverFansAsync(leftOver, cancellationToken).ConfigureAwait(false);

                if (crashed)
                {
                    string text = DescribeCrashRecovery(session, rebooted, recovery);
                    RaiseAlert(FanAlertKind.RecoveredFromCrash, string.Empty, "All fans", text, Severity.Medium);
                    _logger.LogWarning("Recovered from a previous fan-control session: {Text}", text);
                    notices.Add(text);
                }
                else
                {
                    if (recovery.Held > 0 || recovery.Unconfirmed > 0)
                    {
                        notices.Add(DescribeHeldAndUnconfirmed(recovery));
                    }

                    // Closing the hardware monitor at exit already handed the fans back to the firmware (except those
                    // held above), so there is nothing to revert: only the profile that was active is brought back.
                    var previousProfile = FindProfile(session.ProfileName);
                    if (previousProfile is not null)
                    {
                        var reapply = await ApplyProfileAsync(previousProfile, cancellationToken).ConfigureAwait(false);
                        if (reapply.Success)
                        {
                            reappliedProfile = previousProfile.Name;
                            notices.Add($"Fan profile \"{previousProfile.Name}\" was active when PC Checker last closed (revert on exit is off), so it was re-applied. The motherboard ran the fans while PC Checker was closed.");
                            _logger.LogInformation("Re-applied fan profile {Profile} left active by the previous session.", previousProfile.Name);
                        }
                        else
                        {
                            _logger.LogWarning("Could not re-apply fan profile {Profile} from the previous session: {Message}", previousProfile.Name, reapply.Message);
                            notices.Add($"Fan profile \"{previousProfile.Name}\" was active when PC Checker last closed, but it could not be re-applied: {reapply.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not revert fans from the previous session.");
                notices.Add(crashed
                    ? "PC Checker could not confirm the fans were returned to automatic control after the last unclean shutdown. Restart the PC to hand them back to the motherboard, or open the Fans page and click \"Revert all to automatic\"."
                    : "PC Checker could not restore the fan settings from the last session. Open the Fans page to check the fans.");
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

    /// <summary>What happened to the fans a previous session left without firmware control.</summary>
    private sealed record LeftOverRecovery(int Total, int Reverted, int Held, int Unconfirmed);

    /// <summary>
    /// Takes care of fans the previous session left without firmware control: graphics card and other fans go back to
    /// automatic, motherboard fans are held under software control until the PC restarts, and fans that cannot be found
    /// or written stay recorded in the marker. With nothing left over, this only clears the old marker.
    /// </summary>
    private async Task<LeftOverRecovery> RecoverLeftOverFansAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            UpdateSessionFile();
            return new LeftOverRecovery(0, 0, 0, 0);
        }

        await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var motherboard = new List<FanEntry>();
        var others = new List<FanEntry>();
        int missing = 0;
        lock (_gate)
        {
            foreach (var id in ids)
            {
                var entry = _entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    // Not found (driver blocked, still loading, hardware gone): keep it in the marker until it is handled.
                    _needsRestart.Add(id);
                    missing++;
                }
                else if (entry.Kind == HardwareKind.Motherboard)
                {
                    _needsRestart.Add(entry.Id);
                    motherboard.Add(entry);
                }
                else
                {
                    others.Add(entry);
                }
            }
        }

        int reverted = 0, held = 0;
        if (motherboard.Count + others.Count > 0)
        {
            await _guard.RunGuardedAsync($"Recover {motherboard.Count + others.Count} fan(s) left under control by the last session",
                new ChangeOptions(false, false, ModuleName, "fans left by the last session", true, "Restart the PC to hand motherboard fans back to the BIOS"),
                _ => Task.Run(() => UnderControlGate(() =>
                {
                    foreach (var entry in others)
                    {
                        if (ApplyDefault(entry).Success)
                        {
                            reverted++;
                        }
                        else
                        {
                            lock (_gate)
                            {
                                _needsRestart.Add(entry.Id);
                            }
                        }
                    }

                    foreach (var entry in motherboard)
                    {
                        if (HoldUntilRestart(entry).Success)
                        {
                            held++;
                        }
                    }

                    FansUpdated?.Invoke(this, Snapshot());
                    return OperationResult.Ok($"{reverted} fan(s) returned to automatic, {held} held until the PC restarts.");
                })), null, cancellationToken).ConfigureAwait(false);
        }

        // Writes the marker for this process (held fans and anything still unconfirmed), or deletes it when all is handled.
        UpdateSessionFile();
        return new LeftOverRecovery(ids.Count, reverted, held, ids.Count - reverted - held);
    }

    private static string DescribeCrashRecovery(FanSession session, bool rebooted, LeftOverRecovery recovery)
    {
        if (rebooted)
        {
            return "PC Checker did not shut down cleanly last time while it was controlling fans. The PC has restarted since, so the motherboard and graphics card are in charge of the fans again.";
        }

        var parts = new List<string> { $"PC Checker did not shut down cleanly last time while {session.FanIds.Count} fan(s) were under its control." };
        if (recovery.Reverted > 0)
        {
            parts.Add($"{recovery.Reverted} fan(s) were returned to automatic control.");
        }

        if (recovery.Held > 0 || recovery.Unconfirmed > 0)
        {
            parts.Add(DescribeHeldAndUnconfirmed(recovery));
        }

        if (recovery.Total == 0)
        {
            parts.Add("The fans were already back under automatic control.");
        }

        return string.Join(" ", parts);
    }

    private static string DescribeHeldAndUnconfirmed(LeftOverRecovery recovery)
    {
        var parts = new List<string>();
        if (recovery.Held > 0)
        {
            parts.Add($"{recovery.Held} motherboard fan(s) stay at the speed they were left at until the PC restarts, so PC Checker keeps driving them (Balanced curve, or full speed) with its overheating protection on. Restart the PC (Restart, not Shut down) to hand them back to the motherboard.");
        }

        if (recovery.Unconfirmed > 0)
        {
            parts.Add($"PC Checker could not find or take over {recovery.Unconfirmed} fan(s), so it cannot confirm they are back under automatic control. Restart the PC, or open the Fans page and click \"Detect fans\".");
        }

        return string.Join(" ", parts);
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
    /// Called at app exit: reverts to automatic when the setting says so, then flags the session marker as a clean exit
    /// so the next start does not mistake it for a crash. Either way the fans end up under firmware control: closing the
    /// hardware monitor (when the sensor service is disposed) restores every fan LibreHardwareMonitor changed. With the
    /// setting off, the marker only lets the next start re-apply the profile that was active.
    /// </summary>
    public async Task ShutdownAsync()
    {
        try
        {
            if (_settings.Current.Fans.RevertToAutoOnExit)
            {
                await RevertAllToAutoAsync(CancellationToken.None).ConfigureAwait(false);
            }

            // Also after a revert: fans held until the PC restarts keep the marker, and the next start must not report a crash.
            FanSessionStore.MarkCleanExit(SessionFilePath);
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
            _capabilityNotes.Add(AmdGpuFanRules.IsOverdrive8Card(group.Key)
                ? $"Read-only: {group.Key}. {AmdGpuFanRules.ReadOnlyNote}"
                : $"Read-only: {group.Key} reports {group.Count()} fan speed(s) but exposes no control");
        }

        if (_entries.Count == 0 && _rpmOnly.Count == 0)
        {
            _capabilityNotes.Add("No fan sensors were found by the hardware monitor.");
        }

        // Graphics card fans come through the graphics driver and show up even when the motherboard chip cannot be read,
        // so say plainly when the CPU and case fan headers are missing instead of listing only what was found.
        if (!_entries.Any(e => e.Kind == HardwareKind.Motherboard) && !_rpmOnly.Any(r => r.Kind == HardwareKind.Motherboard))
        {
            _capabilityNotes.Add(_host.DriverProblem is { } problem
                ? "Motherboard fan headers (CPU and case fans): not readable. " + problem
                : "Motherboard fan headers (CPU and case fans): none found. Open \"Why is a fan missing?\" below for the reason.");
        }
    }

    private string DescribeUnavailable()
    {
        if (_host.DriverProblem is { } driverProblem)
        {
            return "No motherboard fan controls were found. " + driverProblem;
        }

        if (_rpmOnly.Count > 0)
        {
            return $"This PC reports {_rpmOnly.Count} fan speed(s) but no fan controls the app can drive. Laptops and many pre-built desktops only let the firmware (BIOS/EC) control the fans; use the manufacturer's utility or the BIOS fan settings instead.";
        }

        return "No fans were detected. If this is a laptop or a pre-built desktop, the fans are managed by the firmware and cannot be read here. On a desktop, make sure PC Checker runs as administrator so the motherboard sensor chip can be read.";
    }

    private IReadOnlyList<FanProfile> BuildBuiltInProfiles()
    {
        var sources = GetTemperatureSources();
        IReadOnlyList<SensorReading> temps = [];
        try
        {
            temps = _sensors.GetTemperatures();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sensor temperatures unavailable for the built-in fan profiles.");
        }

        // Same picks as the Dashboard: Tdie before Tctl, and the discrete GPU's core (never the integrated Radeon,
        // whose temperature follows the CPU).
        string cpu = TemperatureRoles.PickCpuTemperature(temps)?.Id ?? PickSensor(sources, HardwareKind.Cpu, "package", "tdie", "tctl", "core") ?? "cpu.package";
        string gpu = TemperatureRoles.PickGpuCoreTemperature(temps)?.Id ?? PickSensor(sources, HardwareKind.Gpu, "core", "hot") ?? "gpu.core";
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
