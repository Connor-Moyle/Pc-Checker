using Microsoft.Extensions.Logging;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;
using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// NVIDIA tuning through NvAPIWrapper: clock offsets via performance states 2.0, power and thermal policies, and
/// fixed fan speeds through the client fan cooler API (with the legacy cooler API as a fallback).
/// </summary>
public sealed class NvApiGpuTuner : IGpuTuner
{
    private readonly ILogger _logger;
    private readonly string? _preferredName;
    private PhysicalGPU? _gpu;
    private (int MinKhz, int MaxKhz)? _coreRange;
    private (int MinKhz, int MaxKhz)? _memoryRange;
    private (uint MinPcm, uint DefaultPcm, uint MaxPcm)? _power;
    private (int MinC, int DefaultC, int MaxC)? _thermal;
    private ThermalController _thermalController = ThermalController.GPU;
    private bool _fanControl;
    private bool _initialized;

    public NvApiGpuTuner(ILogger logger, string? preferredName = null)
    {
        _logger = logger;
        _preferredName = preferredName;
    }

    public GpuVendor Vendor => GpuVendor.Nvidia;

    public GpuControlMethod Method => _gpu is null ? GpuControlMethod.None : GpuControlMethod.NvApi;

    public GpuLimits? Limits { get; private set; }

    public GpuTunerDetection Detect()
    {
        if (!PlatformGuard.IsWindows)
        {
            return GpuTunerDetection.Unavailable("NVIDIA tuning needs Windows.");
        }

        var notes = new List<string>();
        try
        {
            NVIDIA.Initialize();
            _initialized = true;
        }
        catch (DllNotFoundException)
        {
            return GpuTunerDetection.Unavailable("The NVIDIA driver library (nvapi64.dll) was not found. Install the GeForce driver to tune this card.");
        }
        catch (NVIDIAApiException ex)
        {
            return GpuTunerDetection.Unavailable($"The NVIDIA API could not start ({ex.Status}). An NVIDIA driver is required.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NVAPI initialization failed.");
            return GpuTunerDetection.Unavailable("The NVIDIA API could not start: " + ex.Message);
        }

        try
        {
            var gpus = PhysicalGPU.GetPhysicalGPUs();
            if (gpus.Length == 0)
            {
                return GpuTunerDetection.Unavailable("The NVIDIA driver reports no graphics cards.");
            }

            _gpu = gpus.FirstOrDefault(g => _preferredName is not null && SafeName(g).Contains(_preferredName, StringComparison.OrdinalIgnoreCase)) ?? gpus[0];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NVAPI GPU enumeration failed.");
            return GpuTunerDetection.Unavailable("The NVIDIA driver did not list any graphics card: " + ex.Message);
        }

        ReadClockRanges(notes);
        ReadPowerPolicy(notes);
        ReadThermalPolicy(notes);
        ReadFanCapability(notes);

        if (_coreRange is null && _memoryRange is null)
        {
            notes.Add("Clock offsets are locked by the driver on this card (laptop or data-centre cards often lock them). Readouts still work.");
        }

        Limits = NvApiMath.BuildLimits(_coreRange, _memoryRange, _power is { } p ? (p.MinPcm, p.MaxPcm) : null, _thermal is { } t ? (t.MinC, t.MaxC) : null, _fanControl);
        return new GpuTunerDetection(GpuControlMethod.NvApi, SafeName(_gpu!), Limits, notes);
    }

    public OperationResult<GpuTunerState> GetState()
    {
        if (_gpu is null)
        {
            return OperationResult<GpuTunerState>.Fail("The NVIDIA tuner is not active.");
        }

        try
        {
            int core = 0, mem = 0;
            var info = GPUApi.GetPerformanceStates20(_gpu.Handle);
            foreach (var entry in P0Clocks(info))
            {
                if (entry.DomainId == PublicClockDomain.Graphics)
                {
                    core = NvApiMath.KhzToMhz(entry.FrequencyDeltaInkHz.DeltaValue);
                }
                else if (entry.DomainId == PublicClockDomain.Memory)
                {
                    mem = NvApiMath.KhzToMhz(entry.FrequencyDeltaInkHz.DeltaValue);
                }
            }

            int power = 100;
            int? temp = null, fan = null, coreClock = null, memClock = null;
            double? tempC = null;
            Try(() => power = NvApiMath.PcmToPercent(GPUApi.ClientPowerPoliciesGetStatus(_gpu.Handle).PowerPolicyStatusEntries[0].PowerTargetInPCM));
            Try(() => temp = GPUApi.GetThermalPoliciesStatus(_gpu.Handle).ThermalPoliciesStatusEntries[0].TargetTemperature);
            Try(() => fan = _gpu.CoolerInformation.CurrentFanSpeedLevel);
            Try(() =>
            {
                var clocks = _gpu.CurrentClockFrequencies;
                coreClock = clocks.GraphicsClock.IsPresent ? NvApiMath.KhzToMhz((long)clocks.GraphicsClock.Frequency) : null;
                memClock = clocks.MemoryClock.IsPresent ? NvApiMath.KhzToMhz((long)clocks.MemoryClock.Frequency) : null;
            });
            Try(() => tempC = _gpu.ThermalInformation.ThermalSensors.FirstOrDefault()?.CurrentTemperature);
            return OperationResult<GpuTunerState>.Ok(new GpuTunerState(core, mem, power, temp, fan, coreClock, memClock, tempC, null));
        }
        catch (Exception ex)
        {
            return OperationResult<GpuTunerState>.Fail("Could not read the current NVIDIA state: " + ex.Message, ex);
        }
    }

    public OperationResult ApplyOffsets(int coreOffsetMhz, int memoryOffsetMhz)
    {
        if (_gpu is null)
        {
            return OperationResult.Fail("The NVIDIA tuner is not active.");
        }

        if (_coreRange is null && coreOffsetMhz != 0)
        {
            return OperationResult.Fail("The driver locks the core clock offset on this card.");
        }

        if (_memoryRange is null && memoryOffsetMhz != 0)
        {
            return OperationResult.Fail("The driver locks the memory clock offset on this card.");
        }

        try
        {
            var clocks = new List<PerformanceStates20ClockEntryV1>();
            if (_coreRange is { } c)
            {
                clocks.Add(new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, new PerformanceStates20ParameterDelta(NvApiMath.OffsetMhzToDeltaKhz(coreOffsetMhz, c.MinKhz, c.MaxKhz))));
            }

            if (_memoryRange is { } m)
            {
                clocks.Add(new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory, new PerformanceStates20ParameterDelta(NvApiMath.OffsetMhzToDeltaKhz(memoryOffsetMhz, m.MinKhz, m.MaxKhz))));
            }

            if (clocks.Count == 0)
            {
                return OperationResult.Ok("No clock offsets to apply.");
            }

            var state = new PerformanceStates20InfoV1.PerformanceState20(PerformanceStateId.P0_3DPerformance, clocks.ToArray(), []);
            var info = new PerformanceStates20InfoV1([state], (uint)clocks.Count, 0);
            GPUApi.SetPerformanceStates20(_gpu.Handle, info);
            _logger.LogInformation("NVAPI clock offsets applied: core {Core} MHz, memory {Memory} MHz.", coreOffsetMhz, memoryOffsetMhz);
            return OperationResult.Ok($"Clock offsets applied ({coreOffsetMhz:+#;-#;0} / {memoryOffsetMhz:+#;-#;0} MHz).");
        }
        catch (NVIDIAApiException ex)
        {
            return OperationResult.Fail($"The NVIDIA driver rejected the clock offsets ({ex.Status}).", ex);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Applying clock offsets failed: " + ex.Message, ex);
        }
    }

    public OperationResult SetPowerLimit(int percent)
    {
        if (_gpu is null)
        {
            return OperationResult.Fail("The NVIDIA tuner is not active.");
        }

        if (_power is not { } p)
        {
            return percent == 100 ? OperationResult.Ok("Power limit unchanged (not adjustable on this card).") : OperationResult.Fail("The driver does not expose a power limit for this card.");
        }

        try
        {
            uint pcm = Math.Clamp(NvApiMath.PercentToPcm(percent), p.MinPcm, p.MaxPcm);
            GPUApi.ClientPowerPoliciesSetStatus(_gpu.Handle, new PrivatePowerPoliciesStatusV1([new PrivatePowerPoliciesStatusV1.PowerPolicyStatusEntry(pcm)]));
            return OperationResult.Ok($"Power limit set to {NvApiMath.PcmToPercent(pcm)} %.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Setting the power limit failed: " + ex.Message, ex);
        }
    }

    public OperationResult SetTempLimit(int tempC)
    {
        if (_gpu is null)
        {
            return OperationResult.Fail("The NVIDIA tuner is not active.");
        }

        if (_thermal is not { } t)
        {
            return OperationResult.Fail("The driver does not expose a temperature limit for this card.");
        }

        try
        {
            int target = Math.Clamp(tempC, t.MinC, t.MaxC);
            GPUApi.SetThermalPoliciesStatus(_gpu.Handle, new PrivateThermalPoliciesStatusV2([new PrivateThermalPoliciesStatusV2.ThermalPoliciesStatusEntry(_thermalController, target)]));
            return OperationResult.Ok($"Temperature limit set to {target} °C.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Setting the temperature limit failed: " + ex.Message, ex);
        }
    }

    public OperationResult ResetTempLimit()
    {
        if (_thermal is not { } t)
        {
            return OperationResult.Ok("No temperature limit to reset.");
        }

        var r = SetTempLimit(t.DefaultC);
        return r.Success ? OperationResult.Ok($"Temperature limit back to the default {t.DefaultC} °C.") : r;
    }

    public OperationResult SetFan(int? percent)
    {
        if (_gpu is null)
        {
            return OperationResult.Fail("The NVIDIA tuner is not active.");
        }

        if (!_fanControl)
        {
            return OperationResult.Fail("Fan control is not available through the NVIDIA driver on this card. Use the Fans page.");
        }

        // Newer driver path first.
        try
        {
            var control = GPUApi.GetClientFanCoolersControl(_gpu.Handle);
            var entries = control.FanCoolersControlEntries
                .Select(e => percent is { } level
                    ? new PrivateFanCoolersControlV1.FanCoolersControlEntry(e.CoolerId, FanCoolersControlMode.Manual, (uint)Math.Clamp(level, 0, 100))
                    : new PrivateFanCoolersControlV1.FanCoolersControlEntry(e.CoolerId, FanCoolersControlMode.Auto))
                .ToArray();
            if (entries.Length > 0)
            {
                GPUApi.SetClientFanCoolersControl(_gpu.Handle, new PrivateFanCoolersControlV1(entries, control.UnknownUInt));
                return OperationResult.Ok(percent is null ? "Fan returned to automatic control." : $"Fan fixed at {percent} %.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Client fan cooler control unavailable; trying the legacy cooler API.");
        }

        try
        {
            if (percent is null)
            {
                _gpu.CoolerInformation.RestoreCoolerSettingsToDefault();
                return OperationResult.Ok("Fan returned to automatic control.");
            }

            foreach (var cooler in _gpu.CoolerInformation.Coolers)
            {
                _gpu.CoolerInformation.SetCoolerSettings(cooler.CoolerId, Math.Clamp(percent.Value, 0, 100));
            }

            return OperationResult.Ok($"Fan fixed at {percent} %.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Setting the fan speed failed: " + ex.Message, ex);
        }
    }

    public OperationResult Reset(bool resetFan)
    {
        if (_gpu is null)
        {
            return OperationResult.Fail("The NVIDIA tuner is not active.");
        }

        var problems = new List<string>();
        var clocks = ApplyOffsets(0, 0);
        if (!clocks.Success && (_coreRange is not null || _memoryRange is not null))
        {
            problems.Add(clocks.Message);
        }

        if (_power is { } p)
        {
            var r = SetPowerLimit(NvApiMath.PcmToPercent(p.DefaultPcm));
            if (!r.Success)
            {
                problems.Add(r.Message);
            }
        }

        if (_thermal is { } t)
        {
            var r = SetTempLimit(t.DefaultC);
            if (!r.Success)
            {
                problems.Add(r.Message);
            }
        }

        if (resetFan && _fanControl)
        {
            var r = SetFan(null);
            if (!r.Success)
            {
                problems.Add(r.Message);
            }
        }

        return problems.Count == 0
            ? OperationResult.Ok("The graphics card is back at stock settings.")
            : OperationResult.Fail("Some settings could not be reset: " + string.Join(" ", problems));
    }

    public void Dispose()
    {
        if (_initialized)
        {
            try
            {
                NVIDIA.Unload();
            }
            catch
            {
                // Unloading is best effort.
            }

            _initialized = false;
        }
    }

    private static IEnumerable<IPerformanceStates20ClockEntry> P0Clocks(IPerformanceStates20Info info)
    {
        if (info.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out var entries))
        {
            return entries;
        }

        return info.Clocks.Values.FirstOrDefault() ?? [];
    }

    private void ReadClockRanges(List<string> notes)
    {
        try
        {
            var info = GPUApi.GetPerformanceStates20(_gpu!.Handle);
            if (!info.IsEditable)
            {
                notes.Add("The driver marks performance states as read-only on this card.");
            }

            foreach (var entry in P0Clocks(info))
            {
                var range = entry.FrequencyDeltaInkHz.DeltaRange;
                var (_, _, editable) = NvApiMath.DeltaRangeToMhz(range.Minimum, range.Maximum);
                if (!editable)
                {
                    continue;
                }

                if (entry.DomainId == PublicClockDomain.Graphics)
                {
                    _coreRange = (range.Minimum, range.Maximum);
                }
                else if (entry.DomainId == PublicClockDomain.Memory)
                {
                    _memoryRange = (range.Minimum, range.Maximum);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetPerformanceStates20 failed.");
            notes.Add("The driver did not report clock offset ranges (" + ex.Message + ").");
        }
    }

    private void ReadPowerPolicy(List<string> notes)
    {
        try
        {
            var entry = GPUApi.ClientPowerPoliciesGetInfo(_gpu!.Handle).PowerPolicyInfoEntries.FirstOrDefault();
            if (entry.MaximumPowerInPCM > 0 && entry.MaximumPowerInPCM >= entry.MinimumPowerInPCM)
            {
                _power = (entry.MinimumPowerInPCM, entry.DefaultPowerInPCM == 0 ? NvApiMath.PercentToPcm(100) : entry.DefaultPowerInPCM, entry.MaximumPowerInPCM);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Power policy unavailable.");
            notes.Add("Power limit control is not exposed by the driver for this card.");
        }
    }

    private void ReadThermalPolicy(List<string> notes)
    {
        try
        {
            var entry = GPUApi.GetThermalPoliciesInfo(_gpu!.Handle).ThermalPoliciesInfoEntries.FirstOrDefault();
            if (entry.MaximumTemperature > 0 && entry.MaximumTemperature >= entry.MinimumTemperature)
            {
                _thermal = (entry.MinimumTemperature, entry.DefaultTemperature == 0 ? entry.MaximumTemperature : entry.DefaultTemperature, entry.MaximumTemperature);
                _thermalController = entry.Controller;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Thermal policy unavailable.");
            notes.Add("Temperature limit control is not exposed by the driver for this card.");
        }
    }

    private void ReadFanCapability(List<string> notes)
    {
        try
        {
            _fanControl = GPUApi.GetClientFanCoolersControl(_gpu!.Handle).FanCoolersControlEntries.Length > 0;
        }
        catch
        {
            try
            {
                _fanControl = _gpu!.CoolerInformation.Coolers.Any();
            }
            catch
            {
                _fanControl = false;
            }
        }

        if (!_fanControl)
        {
            notes.Add("The driver did not expose fan control (passively cooled or laptop GPU).");
        }
    }

    private static string SafeName(PhysicalGPU gpu)
    {
        try
        {
            return gpu.FullName;
        }
        catch
        {
            return "NVIDIA GPU";
        }
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Optional NVAPI readout failed.");
        }
    }
}
