using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// Intel Arc tuning through the Intel Graphics Control Library (experimental). Integrated Intel graphics are reported
/// as unsupported; any IGCL failure during detection drops the method to <see cref="GpuControlMethod.None"/>.
/// </summary>
public sealed class IntelIgclGpuTuner : IGpuTuner
{
    private readonly ILogger _logger;
    private readonly string? _preferredName;
    private IntPtr _api;
    private IntPtr _device;
    private string? _gpuName;
    private IgclControl _gpuOffset = IgclControl.Unsupported;
    private IgclControl _vramOffset = IgclControl.Unsupported;
    private IgclControl _power = IgclControl.Unsupported;
    private IgclControl _temp = IgclControl.Unsupported;
    private bool _waiverAccepted;

    public IntelIgclGpuTuner(ILogger logger, string? preferredName = null)
    {
        _logger = logger;
        _preferredName = preferredName;
    }

    public GpuVendor Vendor => GpuVendor.Intel;

    public GpuControlMethod Method => _device == IntPtr.Zero ? GpuControlMethod.None : GpuControlMethod.IntelIgcl;

    public GpuLimits? Limits { get; private set; }

    public GpuTunerDetection Detect()
    {
        if (!PlatformGuard.IsWindows)
        {
            return GpuTunerDetection.Unavailable("Intel Arc tuning needs Windows.");
        }

        if (!IntelIgclNative.IsAvailable())
        {
            return GpuTunerDetection.Unavailable("The Intel Graphics Control Library (ControlLib.dll) was not found. Install the Intel Arc driver and Intel Graphics Software to tune this card.");
        }

        var notes = new List<string> { "Intel Arc support through IGCL is experimental. Values are snapped to the driver's own step and range." };
        try
        {
            uint code = IntelIgclNative.Initialize(out _api);
            if (code != IntelIgclNative.Success || _api == IntPtr.Zero)
            {
                return Fail("IGCL could not start (" + IgclValueRules.DescribeResult(code) + ").");
            }

            var (enumCode, devices) = IntelIgclNative.GetDevices(_api);
            if (enumCode != IntelIgclNative.Success || devices.Length == 0)
            {
                return Fail("IGCL listed no Intel graphics devices (" + IgclValueRules.DescribeResult(enumCode) + ").");
            }

            IntPtr chosen = IntPtr.Zero;
            string? chosenName = null;
            bool sawIntegratedOnly = true;
            foreach (var device in devices)
            {
                var props = IntelIgclNative.ReadDeviceProperties(device);
                string name = props?.NameText ?? string.Empty;
                bool integrated = props?.IsIntegrated ?? false;
                if (integrated)
                {
                    continue;
                }

                sawIntegratedOnly = false;
                if (chosen == IntPtr.Zero || (_preferredName is not null && name.Contains(_preferredName, StringComparison.OrdinalIgnoreCase)))
                {
                    chosen = device;
                    chosenName = string.IsNullOrWhiteSpace(name) ? null : name;
                }
            }

            if (chosen == IntPtr.Zero)
            {
                return Fail(sawIntegratedOnly
                    ? "Only integrated Intel graphics were found; integrated GPUs share the CPU's power budget and cannot be tuned here."
                    : "No tunable Intel graphics device was found.");
            }

            var (ocCode, oc) = IntelIgclNative.ReadOcProperties(chosen);
            if (ocCode != IntelIgclNative.Success || oc is null)
            {
                return Fail("IGCL did not report overclock ranges (" + IgclValueRules.DescribeResult(ocCode) + ").");
            }

            if (oc.Value.Supported == 0)
            {
                return Fail("The driver reports that overclocking is not supported on this Intel GPU.");
            }

            _device = chosen;
            _gpuName = chosenName ?? _preferredName ?? "Intel Arc";
            _gpuOffset = oc.Value.GpuFrequencyOffset.ToControl();
            _vramOffset = oc.Value.VramFrequencyOffset.ToControl();
            _power = oc.Value.PowerLimit.ToControl();
            _temp = oc.Value.TemperatureLimit.ToControl();
            Limits = IgclValueRules.BuildLimits(_gpuOffset, _vramOffset, _power, _temp);
            return new GpuTunerDetection(GpuControlMethod.IntelIgcl, _gpuName, Limits, notes);
        }
        catch (DllNotFoundException)
        {
            return Fail("ControlLib.dll could not be loaded.");
        }
        catch (EntryPointNotFoundException ex)
        {
            return Fail("This Intel driver lacks an IGCL overclock entry point (" + ex.Message + ").");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IGCL detection failed.");
            return Fail("IGCL detection failed: " + ex.Message);
        }

        GpuTunerDetection Fail(string why)
        {
            Dispose();
            notes.Add(why);
            return new GpuTunerDetection(GpuControlMethod.None, _gpuName, null, notes);
        }
    }

    public OperationResult<GpuTunerState> GetState()
    {
        if (Method == GpuControlMethod.None)
        {
            return OperationResult<GpuTunerState>.Fail("The Intel tuner is not active.");
        }

        try
        {
            int core = 0, mem = 0, power = 100;
            int? temp = null;
            if (_gpuOffset.Supported && IntelIgclNative.GpuFrequencyOffsetGet(_device, out double c) == IntelIgclNative.Success)
            {
                core = (int)Math.Round(c);
            }

            if (_vramOffset.Supported && IntelIgclNative.VramFrequencyOffsetGet(_device, out double m) == IntelIgclNative.Success)
            {
                mem = (int)Math.Round(m);
            }

            if (_power.Supported && IntelIgclNative.PowerLimitGet(_device, out double p) == IntelIgclNative.Success)
            {
                power = IgclValueRules.MilliwattsToPercent(_power, p);
            }

            if (_temp.Supported && IntelIgclNative.TemperatureLimitGet(_device, out double t) == IntelIgclNative.Success)
            {
                temp = (int)Math.Round(t);
            }

            return OperationResult<GpuTunerState>.Ok(new GpuTunerState(core, mem, power, temp, null, null, null, null, null));
        }
        catch (Exception ex)
        {
            return OperationResult<GpuTunerState>.Fail("Could not read the Intel state: " + ex.Message, ex);
        }
    }

    public OperationResult ApplyOffsets(int coreOffsetMhz, int memoryOffsetMhz)
    {
        var waiver = EnsureWaiver();
        if (!waiver.Success)
        {
            return waiver;
        }

        try
        {
            if (_gpuOffset.Supported)
            {
                double v = IgclValueRules.Snap(_gpuOffset, coreOffsetMhz);
                uint code = IntelIgclNative.GpuFrequencyOffsetSet(_device, v);
                if (code != IntelIgclNative.Success)
                {
                    return OperationResult.Fail("The Intel driver rejected the core offset (" + IgclValueRules.DescribeResult(code) + ").");
                }
            }
            else if (coreOffsetMhz != 0)
            {
                return OperationResult.Fail("This Intel GPU does not expose a core frequency offset.");
            }

            if (_vramOffset.Supported)
            {
                double v = IgclValueRules.Snap(_vramOffset, memoryOffsetMhz);
                uint code = IntelIgclNative.VramFrequencyOffsetSet(_device, v);
                if (code != IntelIgclNative.Success)
                {
                    return OperationResult.Fail("The Intel driver rejected the memory offset (" + IgclValueRules.DescribeResult(code) + ").");
                }
            }
            else if (memoryOffsetMhz != 0)
            {
                return OperationResult.Fail("This Intel GPU does not expose a memory frequency offset.");
            }

            return OperationResult.Ok($"Clock offsets applied ({coreOffsetMhz:+#;-#;0} / {memoryOffsetMhz:+#;-#;0} MHz).");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Applying the Intel clock offsets failed: " + ex.Message, ex);
        }
    }

    public OperationResult SetPowerLimit(int percent)
    {
        if (!_power.Supported)
        {
            return percent == 100 ? OperationResult.Ok("Power limit unchanged (not adjustable on this card).") : OperationResult.Fail("This Intel GPU does not expose a power limit.");
        }

        var waiver = EnsureWaiver();
        if (!waiver.Success)
        {
            return waiver;
        }

        try
        {
            double mw = IgclValueRules.PercentToMilliwatts(_power, percent);
            uint code = IntelIgclNative.PowerLimitSet(_device, mw);
            return code == IntelIgclNative.Success
                ? OperationResult.Ok($"Power limit set to {IgclValueRules.MilliwattsToPercent(_power, mw)} % ({mw / 1000:0} W).")
                : OperationResult.Fail("The Intel driver rejected the power limit (" + IgclValueRules.DescribeResult(code) + ").");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Setting the Intel power limit failed: " + ex.Message, ex);
        }
    }

    public OperationResult SetTempLimit(int tempC)
    {
        if (!_temp.Supported)
        {
            return OperationResult.Fail("This Intel GPU does not expose a temperature limit.");
        }

        var waiver = EnsureWaiver();
        if (!waiver.Success)
        {
            return waiver;
        }

        try
        {
            double v = IgclValueRules.Snap(_temp, tempC);
            uint code = IntelIgclNative.TemperatureLimitSet(_device, v);
            return code == IntelIgclNative.Success
                ? OperationResult.Ok($"Temperature limit set to {v:0} °C.")
                : OperationResult.Fail("The Intel driver rejected the temperature limit (" + IgclValueRules.DescribeResult(code) + ").");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Setting the Intel temperature limit failed: " + ex.Message, ex);
        }
    }

    public OperationResult SetFan(int? percent) =>
        OperationResult.Fail("Intel Arc fan control goes through the Fans page (shared hardware monitor) or Intel Graphics Software.");

    public OperationResult Reset()
    {
        if (Method == GpuControlMethod.None)
        {
            return OperationResult.Fail("The Intel tuner is not active.");
        }

        var problems = new List<string>();
        var offsets = ApplyOffsets(0, 0);
        if (!offsets.Success)
        {
            problems.Add(offsets.Message);
        }

        if (_power.Supported)
        {
            var r = SetPowerLimit(100);
            if (!r.Success)
            {
                problems.Add(r.Message);
            }
        }

        if (_temp.Supported && _temp.Default > 0)
        {
            var r = SetTempLimit((int)Math.Round(_temp.Default));
            if (!r.Success)
            {
                problems.Add(r.Message);
            }
        }

        return problems.Count == 0 ? OperationResult.Ok("The Intel GPU is back at stock settings.") : OperationResult.Fail("Some settings could not be reset: " + string.Join(" ", problems));
    }

    public void Dispose()
    {
        if (_api != IntPtr.Zero)
        {
            try
            {
                IntelIgclNative.Close(_api);
            }
            catch
            {
                // Best effort.
            }

            _api = IntPtr.Zero;
        }

        _device = IntPtr.Zero;
    }

    private OperationResult EnsureWaiver()
    {
        if (Method == GpuControlMethod.None)
        {
            return OperationResult.Fail("The Intel tuner is not active.");
        }

        if (_waiverAccepted)
        {
            return OperationResult.Ok();
        }

        try
        {
            uint code = IntelIgclNative.OverclockWaiverSet(_device);
            if (code != IntelIgclNative.Success)
            {
                return OperationResult.Fail("The Intel overclock waiver could not be set (" + IgclValueRules.DescribeResult(code) + ").");
            }

            _waiverAccepted = true;
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("The Intel overclock waiver could not be set: " + ex.Message, ex);
        }
    }
}
