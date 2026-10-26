using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
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
    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsOverclockService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IGpuTuner? _tuner;
    private CpuToolDetection _cpuTool = new(CpuControlMethod.None, null, "Not detected yet.");
    private IntelXtuCli? _xtu;
    private OverclockProfile _applied = OverclockProfile.Stock();
    private bool _fanViaHost;

    public WindowsOverclockService(ISettingsService settings, ISensorService sensors, IHardwareMonitorHost host,
        IHardwareInventoryService hardware, IChangeGuard guard, ILogger<WindowsOverclockService> logger)
    {
        _settings = settings;
        _sensors = sensors;
        _host = host;
        _hardware = hardware;
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

        var options = new ChangeOptions(null, null, ModuleName, capabilities.GpuName ?? capabilities.CpuName ?? "System", true, "Reset to defaults on the Overclock page");
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

            bool touchesGpu = profile.GpuCoreOffsetMhz != 0 || profile.GpuMemoryOffsetMhz != 0 || profile.GpuPowerLimitPercent != 100 || profile.GpuTempLimitC is not null || profile.GpuFanPercent is not null || !_applied.IsStock;
            if (touchesGpu && _tuner is not null)
            {
                await Task.Run(() => ApplyGpu(profile, messages, failures), cancellationToken).ConfigureAwait(false);
            }

            bool touchesCpu = profile.CpuAllCoreRatio is not null || profile.CpuVoltageOffsetMv is not null || profile.CpuPowerLimitW is not null;
            if (touchesCpu)
            {
                var cpu = await ApplyCpuAsync(profile, session, cancellationToken).ConfigureAwait(false);
                (cpu.Success ? messages : failures).Add(cpu.Message);
                if (cpu.Success)
                {
                    session.CpuTouched = true;
                    OverclockSessionStore.Save(SessionPath, session);
                }
            }

            _applied = profile.Clone();
            if (failures.Count > 0 && messages.Count == 0)
            {
                return OperationResult.Fail(string.Join(" ", failures));
            }

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

    private void ApplyGpu(OverclockProfile profile, List<string> messages, List<string> failures)
    {
        var offsets = _tuner!.ApplyOffsets(profile.GpuCoreOffsetMhz, profile.GpuMemoryOffsetMhz);
        (offsets.Success ? messages : failures).Add(offsets.Message);

        var power = _tuner.SetPowerLimit(profile.GpuPowerLimitPercent);
        if (!power.Success || profile.GpuPowerLimitPercent != 100)
        {
            (power.Success ? messages : failures).Add(power.Message);
        }

        if (profile.GpuTempLimitC is { } temp)
        {
            var t = _tuner.SetTempLimit(temp);
            (t.Success ? messages : failures).Add(t.Message);
        }

        if (profile.GpuFanPercent is { } fan)
        {
            var f = _fanViaHost ? HostFanControl.Set(_host, fan) : _tuner.SetFan(fan);
            (f.Success ? messages : failures).Add(f.Message);
        }
        else if (_applied.GpuFanPercent is not null)
        {
            var f = _fanViaHost ? HostFanControl.Set(_host, null) : _tuner.SetFan(null);
            (f.Success ? messages : failures).Add(f.Message);
        }
    }

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
                return run.Success ? OperationResult.Ok($"CPU power limits set to {watts} W through ryzenadj.") : OperationResult.Fail("ryzenadj failed: " + (run.StdErr.Length > 0 ? run.StdErr : run.StdOut));
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

            if (_fanViaHost)
            {
                var fan = HostFanControl.Set(_host, null);
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
        double? cpu = SensorPicker.MaxCpuTemp(snapshot);
        double? gpu = SensorPicker.MaxGpuTemp(snapshot);
        if (cpu is { } c && c >= settings.MaxCpuTempC)
        {
            return OperationResult.Fail($"The CPU is already at {c:0} °C, above your {settings.MaxCpuTempC} °C limit. Let it cool down before applying changes.");
        }

        if (gpu is { } g && g >= settings.MaxGpuTempLimitC)
        {
            return OperationResult.Fail($"The GPU is already at {g:0} °C, above your {settings.MaxGpuTempLimitC} °C limit. Let it cool down before applying changes.");
        }

        return null;
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
