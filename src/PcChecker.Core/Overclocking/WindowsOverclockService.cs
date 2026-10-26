using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Fans;
using PcChecker.Core.Hardware;
using PcChecker.Core.Restore;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// Windows implementation of <see cref="IOverclockService"/>: vendor detection, GPU tuning through the vendor
/// tuners (NVAPI / ADL / IGCL), CPU tuning through Intel XTU or ryzenadj, the crash watchdog session file and the
/// stress test. Every system change goes through <see cref="IChangeGuard"/>.
/// </summary>
public sealed partial class WindowsOverclockService : IOverclockService, IDisposable
{
    private const string ModuleName = "Overclock";

    private readonly ISettingsService _settings;
    private readonly ISensorService _sensors;
    private readonly IHardwareMonitorHost _host;
    private readonly IHardwareInventoryService _hardware;
    private readonly IFanControlService _fans;
    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsOverclockService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _watchdogLock = new();
    private Task<OperationResult>? _watchdogRun;

    private IGpuTuner? _tuner;
    private CpuToolDetection _cpuTool = new(CpuControlMethod.None, null, "Not detected yet.");
    private IntelXtuCli? _xtu;
    private OverclockProfile _applied = OverclockProfile.Stock();
    private bool _fanViaHost;

    public WindowsOverclockService(ISettingsService settings, ISensorService sensors, IHardwareMonitorHost host,
        IHardwareInventoryService hardware, IFanControlService fans, IChangeGuard guard, ILogger<WindowsOverclockService> logger)
    {
        _settings = settings;
        _sensors = sensors;
        _host = host;
        _hardware = hardware;
        _fans = fans;
        _guard = guard;
        _logger = logger;
    }

    public OverclockCapabilities? Capabilities { get; private set; }

    private string SessionPath => OverclockSessionStore.PathFor(_settings.SettingsDirectory);

    public async Task<OverclockCapabilities> DetectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var notes = new List<string>();
            var inventory = await GetInventoryAsync(cancellationToken).ConfigureAwait(false);

            // GPU
            var gpus = inventory?.Graphics.Count > 0 ? inventory.Graphics : WmiOverclockFacts.QueryGpus();
            var gpu = GpuVendorDetector.PickPrimary(gpus);
            var vendor = gpu is null ? GpuVendor.Unknown : GpuVendorDetector.Detect(gpu);
            bool integrated = gpu is not null && GpuVendorDetector.LooksIntegrated(gpu);
            string? gpuName = gpu?.Name;

            _tuner?.Dispose();
            _tuner = null;
            GpuTunerDetection detection = GpuTunerDetection.Unavailable("No graphics card was recognised.");
            if (integrated)
            {
                detection = GpuTunerDetection.Unavailable($"{gpuName} is integrated graphics and shares the CPU's power budget; it cannot be tuned here.");
            }
            else
            {
                detection = await Task.Run(() => DetectGpuTuner(vendor, gpuName), cancellationToken).ConfigureAwait(false);
                if (detection.Method == GpuControlMethod.None && vendor == GpuVendor.Unknown && gpu is null)
                {
                    detection = detection with { Notes = ["No graphics card was recognised by Windows or the vendor drivers."] };
                }
            }

            gpuName = detection.GpuName ?? gpuName;
            notes.AddRange(detection.Notes);
            if (detection.Method != GpuControlMethod.None && vendor == GpuVendor.Unknown)
            {
                vendor = _tuner?.Vendor ?? vendor;
            }

            // CPU
            var cpu = inventory?.Processors.FirstOrDefault();
            string? cpuName = cpu?.Name;
            string cpuVendor = VendorSoftwareCatalog.CpuVendorName(cpu?.Manufacturer ?? cpu?.Name);
            if (cpu is null)
            {
                var (wmiName, wmiVendor) = WmiOverclockFacts.QueryCpu();
                cpuName = wmiName;
                cpuVendor = VendorSoftwareCatalog.CpuVendorName(wmiVendor ?? wmiName);
            }

            _cpuTool = CpuToolLocator.Detect(cpuVendor, File.Exists, AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH"));
            _xtu = _cpuTool.Method == CpuControlMethod.IntelXtuCli && _cpuTool.ToolPath is not null ? new IntelXtuCli(_cpuTool.ToolPath, _logger) : null;
            notes.Add(_cpuTool.Note);

            // Memory
            var modules = inventory?.MemoryModules.Count > 0
                ? inventory.MemoryModules.Select(m => (m.ConfiguredClockSpeedMhz, m.SpeedMhz, m.MemoryType)).ToList()
                : WmiOverclockFacts.QueryMemory().ToList();
            var xmp = XmpStateEvaluator.Evaluate(modules.Select(m => (m.Item1, m.Item2)), modules.Select(m => m.Item3).FirstOrDefault(t => t is not null));
            notes.Add(xmp.Advice);

            // Fans through the shared host
            try
            {
                await _host.EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Hardware monitor host could not be opened for fan control.");
            }

            _fanViaHost = HostFanControl.IsAvailable(_host);
            var limits = detection.Limits is { } l ? l with { FanControl = l.FanControl || _fanViaHost } : null;

            var matrix = SupportMatrixBuilder.Build(new SupportFacts(vendor, detection.Method, limits, integrated, cpuVendor, _cpuTool.Method, xmp.State, _fanViaHost));
            Capabilities = new OverclockCapabilities(vendor, gpuName, detection.Method, limits, cpuName, _cpuTool.Method, _cpuTool.ToolPath,
                xmp.State, xmp.ConfiguredMhz, xmp.MaxMhz, notes.Distinct().ToList())
            {
                SupportMatrix = matrix,
            };

            // Restore knowledge of a profile that is still live from this session.
            var session = OverclockSessionStore.Load(SessionPath);
            if (session is { Clean: false })
            {
                _applied = session.Profile;
            }

            _logger.LogInformation("Overclock detection: GPU {Gpu} via {GpuMethod}, CPU {Cpu} via {CpuMethod}, XMP {Xmp}.", gpuName, detection.Method, cpuName, _cpuTool.Method, xmp.State);
            return Capabilities;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OverclockState> GetStateAsync(CancellationToken cancellationToken)
    {
        var live = SensorPicker.Pick(_sensors.Latest);
        int? coreClock = live.GpuCoreClockMhz, memClock = live.GpuMemoryClockMhz;
        double? gpuTemp = live.GpuTempC, gpuPower = live.GpuPowerW;

        if (_tuner is not null && (coreClock is null || gpuTemp is null))
        {
            var state = await Task.Run(() => _tuner.GetState(), cancellationToken).ConfigureAwait(false);
            if (state.Success && state.Value is { } s)
            {
                coreClock ??= s.CoreClockMhz;
                memClock ??= s.MemoryClockMhz;
                gpuTemp ??= s.TempC;
                gpuPower ??= s.PowerW;
            }
        }

        return new OverclockState(_applied.Clone(), coreClock, memClock, gpuTemp, gpuPower, live.CpuTempC, live.CpuClockMhz);
    }

    public async Task<OperationResult> ApplyAsync(OverclockProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var capabilities = Capabilities ?? await DetectAsync(cancellationToken).ConfigureAwait(false);
        var settings = _settings.Current.Overclock;

        var validation = OverclockSafetyValidator.Validate(profile, capabilities, settings);
        if (!validation.Success)
        {
            return validation;
        }

        var tooHot = CheckTemperatures(settings);
        if (tooHot is not null)
        {
            return tooHot;
        }

        var options = new ChangeOptions(null, null, ModuleName, capabilities.GpuName ?? capabilities.CpuName ?? "System", true, "Reset to defaults on the Overclock page")
        {
            // The brief asks deep changes (Drivers, Overclock, Features) to stop when the restore point cannot be made.
            OnRestorePointFailed = RestorePointFailedBehavior.Abort,
        };
        return await _guard.RunGuardedAsync($"Apply overclock profile \"{profile.Name}\"", options, ct => ApplyCoreAsync(profile, capabilities, ct), null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> ResetAsync(CancellationToken cancellationToken)
    {
        if (_tuner is null && _xtu is null && Capabilities is null)
        {
            await DetectAsync(cancellationToken).ConfigureAwait(false);
        }

        var options = new ChangeOptions(false, false, ModuleName, Capabilities?.GpuName ?? "System", false, null);
        return await _guard.RunGuardedAsync("Reset overclock to defaults", options, ResetCoreAsync, null, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _tuner?.Dispose();
        _tuner = null;
        _gate.Dispose();
    }

    private async Task<OperationResult> ApplyCoreAsync(OverclockProfile profile, OverclockCapabilities capabilities, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Remember what was true before, so a profile that fails completely leaves no trace of itself behind.
            var previousSession = OverclockSessionStore.Load(SessionPath);
            var previousApplied = _applied;
            string? previousActiveName = _settings.Current.Overclock.ActiveProfileName;
            bool previousClean = _settings.Current.Overclock.LastSessionClean;

            var session = OverclockSessionStore.Load(SessionPath) ?? new OverclockSession();
            session.Profile = profile.Clone();
            session.AppliedUtc = DateTimeOffset.UtcNow;
            session.Clean = false;
            session.GpuMethod = capabilities.GpuMethod;
            session.CpuMethod = capabilities.CpuMethod;
            if (!OverclockSessionStore.Save(SessionPath, session))
            {
                return OperationResult.Fail("The watchdog file could not be written, so the overclock was not applied (it would not be reverted after a crash).");
            }

            _settings.Current.Overclock.LastSessionClean = false;
            _settings.Current.Overclock.ActiveProfileName = profile.Name;
            await _settings.SaveAsync().ConfigureAwait(false);

            var messages = new List<string>();
            var failures = new List<string>();

            bool gpuRequested = profile.GpuCoreOffsetMhz != 0 || profile.GpuMemoryOffsetMhz != 0 || profile.GpuPowerLimitPercent != 100 || profile.GpuTempLimitC is not null || profile.GpuFanPercent is not null;
            bool gpuUnchanged = SameGpuSettings(profile, _applied);
            bool gpuOk = true;
            GpuApplyOutcome? gpuOutcome = null;
            if (_tuner is not null && (gpuRequested || !_applied.IsStock))
            {
                if (gpuUnchanged)
                {
                    // A CPU-only apply carries the GPU values that are already live; do not re-send them.
                    if (gpuRequested)
                    {
                        messages.Add("GPU settings left as they are.");
                    }
                }
                else
                {
                    gpuOutcome = await ApplyGpuAsync(profile, messages, failures, cancellationToken).ConfigureAwait(false);
                    gpuOk = gpuOutcome.AnySucceeded;
                }
            }

            bool touchesCpu = profile.CpuAllCoreRatio is not null || profile.CpuVoltageOffsetMv is not null || profile.CpuPowerLimitW is not null;
            bool cpuOk = true;
            if (touchesCpu)
            {
                var cpu = await ApplyCpuAsync(profile, session, cancellationToken).ConfigureAwait(false);
                (cpu.Success ? messages : failures).Add(cpu.Message);
                cpuOk = cpu.Success;
                if (cpu.Success)
                {
                    session.CpuTouched = true;
                    OverclockSessionStore.Save(SessionPath, session);
                }
            }

            if (failures.Count > 0 && messages.Count == 0)
            {
                // Nothing took: put the session file and settings back exactly as they were.
                if (previousSession is null)
                {
                    OverclockSessionStore.Delete(SessionPath);
                }
                else
                {
                    OverclockSessionStore.Save(SessionPath, previousSession);
                }

                _applied = previousApplied;
                _settings.Current.Overclock.ActiveProfileName = previousActiveName;
                _settings.Current.Overclock.LastSessionClean = previousClean;
                await _settings.SaveAsync().ConfigureAwait(false);
                return OperationResult.Fail(string.Join(" ", failures));
            }

            // Record only what really took, sub-setting by sub-setting: a failed power or temperature limit keeps its
            // previous value so the next apply re-sends it instead of believing it is already live. No CPU values when the CPU half failed.
            var applied = profile.Clone();
            if (gpuOutcome is not null)
            {
                if (gpuOutcome.Offsets == false)
                {
                    applied.GpuCoreOffsetMhz = previousApplied.GpuCoreOffsetMhz;
                    applied.GpuMemoryOffsetMhz = previousApplied.GpuMemoryOffsetMhz;
                }

                if (gpuOutcome.Power == false)
                {
                    applied.GpuPowerLimitPercent = previousApplied.GpuPowerLimitPercent;
                }

                if (gpuOutcome.Temp == false)
                {
                    applied.GpuTempLimitC = previousApplied.GpuTempLimitC;
                }

                if (gpuOutcome.Fan == false)
                {
                    applied.GpuFanPercent = previousApplied.GpuFanPercent;
                }
            }

            if (!cpuOk)
            {
                applied.CpuAllCoreRatio = null;
                applied.CpuVoltageOffsetMv = null;
                applied.CpuPowerLimitW = null;
            }

            _applied = applied;

            string summary = string.Join(" ", messages);
            return failures.Count > 0
                ? OperationResult.Ok(summary + " Not applied: " + string.Join(" ", failures))
                : OperationResult.Ok(summary.Length > 0 ? summary : "Profile applied.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Which GPU sub-settings an apply got through to the driver; null means the profile did not ask for it.</summary>
    private sealed class GpuApplyOutcome
    {
        public bool? Offsets { get; set; }
        public bool? Power { get; set; }
        public bool? Temp { get; set; }
        public bool? Fan { get; set; }
        public bool AnySucceeded => Offsets == true || Power == true || Temp == true || Fan == true;
    }

    /// <summary>Sends every GPU setting of the profile to the tuner and reports, per sub-setting, what succeeded.</summary>
    private async Task<GpuApplyOutcome> ApplyGpuAsync(OverclockProfile profile, List<string> messages, List<string> failures, CancellationToken cancellationToken)
    {
        var outcome = new GpuApplyOutcome();
        await Task.Run(() =>
        {
            var offsets = _tuner!.ApplyOffsets(profile.GpuCoreOffsetMhz, profile.GpuMemoryOffsetMhz);
            outcome.Offsets = offsets.Success;
            (offsets.Success ? messages : failures).Add(offsets.Message);

            var power = _tuner.SetPowerLimit(profile.GpuPowerLimitPercent);
            outcome.Power = power.Success;
            if (!power.Success || profile.GpuPowerLimitPercent != 100)
            {
                (power.Success ? messages : failures).Add(power.Message);
            }

            if (profile.GpuTempLimitC is { } temp)
            {
                var t = _tuner.SetTempLimit(temp);
                outcome.Temp = t.Success;
                (t.Success ? messages : failures).Add(t.Message);
            }
            else if (_applied.GpuTempLimitC is not null)
            {
                // The previous profile set a limit and this one does not: return it to the driver default.
                var t = _tuner.ResetTempLimit();
                outcome.Temp = t.Success;
                (t.Success ? messages : failures).Add(t.Message);
            }
        }, cancellationToken).ConfigureAwait(false);

        if (profile.GpuFanPercent is { } fan)
        {
            var f = await SetGpuFanAsync(fan, cancellationToken).ConfigureAwait(false);
            outcome.Fan = f.Success;
            (f.Success ? messages : failures).Add(f.Message);
        }
        else if (_applied.GpuFanPercent is not null)
        {
            var f = await SetGpuFanAsync(null, cancellationToken).ConfigureAwait(false);
            outcome.Fan = f.Success;
            (f.Success ? messages : failures).Add(f.Message);
        }

        return outcome;
    }

    /// <summary>
    /// Fixes the GPU fans at <paramref name="percent"/> (null: automatic). When the fans are driven through the shared
    /// hardware monitor, the request goes through the Fans module, which is the single owner of fan mode, the crash
    /// marker and the thermal safety net; the Fans page then shows the fan as Manual and its curve loop never fights
    /// the overclock profile. The vendor tuner is used only when the host exposes no GPU fan.
    /// </summary>
    private async Task<OperationResult> SetGpuFanAsync(int? percent, CancellationToken cancellationToken)
    {
        if (!_fanViaHost)
        {
            return await Task.Run(() => _tuner!.SetFan(percent), cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<FanDevice> fans;
        try
        {
            fans = await _fans.GetFansAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The fan service could not list fans; driving the GPU fans through the host directly.");
            fans = [];
        }

        var gpuFans = fans.Where(f => f.Hardware == HardwareKind.Gpu && f.Controllable).ToList();
        if (gpuFans.Count == 0)
        {
            return HostFanControl.Set(_host, percent);
        }

        int changed = 0;
        var problems = new List<string>();
        foreach (var fan in gpuFans)
        {
            var result = percent is { } p
                ? await _fans.SetManualAsync(fan.Id, p, cancellationToken).ConfigureAwait(false)
                : await _fans.SetAutoAsync(fan.Id, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                changed++;
            }
            else
            {
                problems.Add($"{fan.Name}: {result.Message}");
            }
        }

        if (changed == 0)
        {
            return OperationResult.Fail("GPU fan control failed: " + string.Join("; ", problems));
        }

        string text = percent is null
            ? $"{changed} GPU fan(s) returned to automatic control (tracked on the Fans page)."
            : $"{changed} GPU fan(s) fixed at {percent} % (shown as Manual on the Fans page).";
        return OperationResult.Ok(problems.Count == 0 ? text : text + " Not changed: " + string.Join("; ", problems));
    }

    private static bool SameGpuSettings(OverclockProfile a, OverclockProfile b) =>
        a.GpuCoreOffsetMhz == b.GpuCoreOffsetMhz
        && a.GpuMemoryOffsetMhz == b.GpuMemoryOffsetMhz
        && a.GpuPowerLimitPercent == b.GpuPowerLimitPercent
        && a.GpuTempLimitC == b.GpuTempLimitC
        && a.GpuFanPercent == b.GpuFanPercent;

    private async Task<OperationResult> ApplyCpuAsync(OverclockProfile profile, OverclockSession session, CancellationToken cancellationToken)
    {
        switch (_cpuTool.Method)
        {
            case CpuControlMethod.IntelXtuCli when _xtu is not null:
            {
                var list = await _xtu.ListTunablesAsync(cancellationToken).ConfigureAwait(false);
                if (!list.Success || list.Value is null)
                {
                    return OperationResult.Fail(list.Message);
                }

                var done = new List<string>();
                if (profile.CpuAllCoreRatio is { } ratio)
                {
                    var r = await _xtu.SetByNameAsync(list.Value, XtuOutputParser.CoreRatioNames, IntelXtuCli.FormatRatio(ratio), cancellationToken).ConfigureAwait(false);
                    if (!r.Success)
                    {
                        return r.AsUntyped();
                    }

                    session.XtuTunableIds.Add(r.Value);
                    done.Add(r.Message);
                }

                if (profile.CpuVoltageOffsetMv is { } mv)
                {
                    var r = await _xtu.SetByNameAsync(list.Value, XtuOutputParser.VoltageOffsetNames, IntelXtuCli.FormatVoltageOffset(mv), cancellationToken).ConfigureAwait(false);
                    if (!r.Success)
                    {
                        return r.AsUntyped();
                    }

                    session.XtuTunableIds.Add(r.Value);
                    done.Add(r.Message);
                }

                if (profile.CpuPowerLimitW is { } w)
                {
                    var r = await _xtu.SetByNameAsync(list.Value, XtuOutputParser.PowerLimitNames, IntelXtuCli.FormatWatts(w), cancellationToken).ConfigureAwait(false);
                    if (!r.Success)
                    {
                        return r.AsUntyped();
                    }

                    session.XtuTunableIds.Add(r.Value);
                    done.Add(r.Message);
                }

                return OperationResult.Ok(string.Join(" ", done));
            }

            case CpuControlMethod.RyzenAdj when _cpuTool.ToolPath is not null:
            {
                if (profile.CpuAllCoreRatio is not null || profile.CpuVoltageOffsetMv is not null)
                {
                    return OperationResult.Fail("ryzenadj can only change power limits. Set the ratio and voltage in the BIOS (PBO / Curve Optimizer).");
                }

                if (profile.CpuPowerLimitW is not { } watts)
                {
                    return OperationResult.Ok("No CPU change requested.");
                }

                var run = await PowerShellRunner.RunAsync($"& '{_cpuTool.ToolPath.Replace("'", "''")}' {CpuToolLocator.RyzenAdjArguments(watts)}", TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                return run.Success
                    ? OperationResult.Ok($"CPU power limits set to {watts} W through ryzenadj. They stay in place until the next reboot; Reset cannot undo them.")
                    : OperationResult.Fail("ryzenadj failed: " + (run.StdErr.Length > 0 ? run.StdErr : run.StdOut));
            }

            case CpuControlMethod.RyzenMaster:
            case CpuControlMethod.RyzenMasterCli:
                return OperationResult.Fail("AMD does not publish a CPU tuning API. Open AMD Ryzen Master from this page to apply CPU settings.");

            default:
                return OperationResult.Fail("No CPU tuning tool is installed; change CPU settings in the BIOS.");
        }
    }

    private async Task<OperationResult> ResetCoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var messages = new List<string>();
            var failures = new List<string>();
            var session = OverclockSessionStore.Load(SessionPath);

            if (_tuner is not null)
            {
                var gpu = await Task.Run(() => _tuner.Reset(), cancellationToken).ConfigureAwait(false);
                (gpu.Success ? messages : failures).Add(gpu.Message);
            }

            if (_fanViaHost && _applied.GpuFanPercent is not null)
            {
                // Only fans this profile fixed go back to automatic; fans the Fans page owns are left to it.
                var fan = await SetGpuFanAsync(null, cancellationToken).ConfigureAwait(false);
                if (!fan.Success)
                {
                    failures.Add(fan.Message);
                }
            }

            if (session is { CpuTouched: true } && _xtu is not null && session.XtuTunableIds.Count > 0)
            {
                var cpu = await _xtu.ResetAsync(session.XtuTunableIds, cancellationToken).ConfigureAwait(false);
                (cpu.Success ? messages : failures).Add(cpu.Message);
            }

            if (session is { CpuTouched: true, CpuMethod: CpuControlMethod.RyzenAdj })
            {
                // ryzenadj has no reset command and the previous limits are not readable, so be honest about it.
                messages.Add("CPU power limits set through ryzenadj stay in place until the next reboot; restart the PC to return to the BIOS values.");
            }

            OverclockSessionStore.Delete(SessionPath);
            _applied = OverclockProfile.Stock();
            _settings.Current.Overclock.LastSessionClean = true;
            _settings.Current.Overclock.ActiveProfileName = null;
            await _settings.SaveAsync().ConfigureAwait(false);

            if (failures.Count > 0)
            {
                return OperationResult.Fail(string.Join(" ", failures) + (messages.Count > 0 ? " " + string.Join(" ", messages) : string.Empty));
            }

            return OperationResult.Ok(messages.Count > 0 ? string.Join(" ", messages) : "Nothing was applied; everything is already at stock.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private OperationResult? CheckTemperatures(OverclockSettings settings)
    {
        var snapshot = _sensors.Latest;
        return OverclockSafetyValidator.CheckTemperatures(SensorPicker.MaxCpuTemp(snapshot), SensorPicker.MaxGpuTemp(snapshot), settings);
    }

    private GpuTunerDetection DetectGpuTuner(GpuVendor vendor, string? gpuName)
    {
        var order = vendor switch
        {
            GpuVendor.Nvidia => new[] { GpuVendor.Nvidia },
            GpuVendor.Amd => [GpuVendor.Amd],
            GpuVendor.Intel => [GpuVendor.Intel],
            _ => [GpuVendor.Nvidia, GpuVendor.Amd, GpuVendor.Intel],
        };

        var notes = new List<string>();
        foreach (var candidate in order)
        {
            IGpuTuner tuner = candidate switch
            {
                GpuVendor.Nvidia => new NvApiGpuTuner(_logger, gpuName),
                GpuVendor.Amd => new AmdAdlGpuTuner(_logger, gpuName),
                _ => new IntelIgclGpuTuner(_logger, gpuName),
            };

            GpuTunerDetection detection;
            try
            {
                detection = tuner.Detect();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Vendor} tuner threw during detection.", candidate);
                detection = GpuTunerDetection.Unavailable($"{GpuVendorDetector.DisplayName(candidate)} detection failed: {ex.Message}");
            }

            if (detection.Method != GpuControlMethod.None)
            {
                _tuner = tuner;
                return detection with { Notes = notes.Concat(detection.Notes).ToList() };
            }

            tuner.Dispose();
            notes.AddRange(vendor == GpuVendor.Unknown ? detection.Notes.Select(n => GpuVendorDetector.DisplayName(candidate) + ": " + n) : detection.Notes);
        }

        notes.Add(vendor switch
        {
            GpuVendor.Nvidia => "Monitoring still works. For tuning, use the NVIDIA app (Automatic Tuning).",
            GpuVendor.Amd => "Monitoring still works. For tuning, use AMD Software: Adrenalin Edition → Performance → Tuning.",
            GpuVendor.Intel => "Monitoring still works. For tuning, use Intel Graphics Software.",
            _ => "Monitoring still works; GPU tuning is unavailable.",
        });
        return new GpuTunerDetection(GpuControlMethod.None, gpuName, null, notes);
    }

    private async Task<HardwareInventory?> GetInventoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return _hardware.Last ?? await _hardware.ScanAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hardware inventory unavailable for overclock detection; falling back to WMI.");
            return null;
        }
    }
}
