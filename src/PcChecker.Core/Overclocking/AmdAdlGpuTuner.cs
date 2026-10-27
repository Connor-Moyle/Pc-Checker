using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// AMD Radeon tuning through ADL Overdrive8 (experimental). Every value is validated against the ranges the driver
/// itself reports; any ADL failure during detection drops the method to <see cref="GpuControlMethod.None"/>.
/// </summary>
public sealed class AmdAdlGpuTuner : IGpuTuner
{
    private readonly ILogger _logger;
    private readonly string? _preferredName;
    private IntPtr _context;
    private int _adapterIndex = -1;
    private IReadOnlyList<Od8Range> _ranges = [];
    private string? _gpuName;

    public AmdAdlGpuTuner(ILogger logger, string? preferredName = null)
    {
        _logger = logger;
        _preferredName = preferredName;
    }

    public GpuVendor Vendor => GpuVendor.Amd;

    public GpuControlMethod Method => _adapterIndex >= 0 ? GpuControlMethod.AmdAdl : GpuControlMethod.None;

    public GpuLimits? Limits { get; private set; }

    public GpuTunerDetection Detect()
    {
        if (!PlatformGuard.IsWindows)
        {
            return GpuTunerDetection.Unavailable("AMD tuning needs Windows.");
        }

        if (!AmdAdlNative.IsAvailable())
        {
            return GpuTunerDetection.Unavailable("The AMD driver library (atiadlxx.dll) was not found. Install AMD Software: Adrenalin Edition to tune this card.");
        }

        var notes = new List<string> { "AMD Overdrive8 support is experimental. Values are checked against the driver's own limits before they are sent." };
        try
        {
            int code = AmdAdlNative.MainControlCreate(AmdAdlNative.Malloc, 1, out _context);
            if (code != AmdAdlNative.AdlOk || _context == IntPtr.Zero)
            {
                return Fail($"ADL could not start ({AdlValueRules.DescribeError(code)}).");
            }

            var adapters = AmdAdlNative.GetAdapters(_context)
                .Where(a => a.VendorId == AmdAdlNative.AmdVendorId && a.Present != 0)
                .GroupBy(a => a.BusNumber)
                .Select(g => g.First())
                .ToList();
            if (adapters.Count == 0)
            {
                return Fail("ADL started but listed no active AMD adapters.");
            }

            var picked = adapters.FirstOrDefault(a => _preferredName is not null && (a.AdapterName ?? string.Empty).Contains(_preferredName, StringComparison.OrdinalIgnoreCase));
            if (picked.AdapterName is null)
            {
                picked = adapters[0];
            }

            int supported = 0, enabled = 0, version = 0;
            code = AmdAdlNative.OverdriveCaps(_context, picked.AdapterIndex, out supported, out enabled, out version);
            if (code != AmdAdlNative.AdlOk || supported == 0)
            {
                return Fail($"Overdrive is not supported on {picked.AdapterName} ({AdlValueRules.DescribeError(code)}).");
            }

            if (version < 8)
            {
                return Fail($"{picked.AdapterName} uses Overdrive {version}; PC Checker only implements Overdrive8 (Radeon RX 5000 and newer).");
            }

            var (initCode, _, ranges) = AmdAdlNative.GetInitSettings(_context, picked.AdapterIndex);
            if (initCode != AmdAdlNative.AdlOk || ranges.Length == 0)
            {
                return Fail($"Overdrive8 ranges could not be read ({AdlValueRules.DescribeError(initCode)}).");
            }

            _adapterIndex = picked.AdapterIndex;
            _gpuName = picked.AdapterName;
            _ranges = ranges;
            Limits = AdlValueRules.BuildLimits(ranges);
            if (enabled == 0)
            {
                notes.Add("Overdrive is supported but currently disabled in AMD Software; enable manual tuning there first if changes do not take.");
            }

            return new GpuTunerDetection(GpuControlMethod.AmdAdl, _gpuName, Limits, notes);
        }
        catch (DllNotFoundException)
        {
            return Fail("atiadlxx.dll could not be loaded.");
        }
        catch (EntryPointNotFoundException ex)
        {
            return Fail("This AMD driver lacks the Overdrive8 entry points (" + ex.Message + ").");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ADL detection failed.");
            return Fail("ADL detection failed: " + ex.Message);
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
            return OperationResult<GpuTunerState>.Fail("The AMD tuner is not active.");
        }

        try
        {
            var (code, values) = AmdAdlNative.GetCurrentSettings(_context, _adapterIndex);
            if (code != AmdAdlNative.AdlOk)
            {
                return OperationResult<GpuTunerState>.Fail("Could not read the current Overdrive8 values (" + AdlValueRules.DescribeError(code) + ").");
            }

            int Value(int feature) => feature < values.Length ? values[feature] : 0;
            var core = AdlValueRules.Find(_ranges, Od8Feature.GfxClkFmax);
            var mem = AdlValueRules.Find(_ranges, Od8Feature.UclkFmax);
            var temp = AdlValueRules.Find(_ranges, Od8Feature.OperatingTempMax);
            int coreOffset = core is { IsSupported: true } ? Value(Od8Feature.GfxClkFmax) - core.Default : 0;
            int memOffset = mem is { IsSupported: true } ? Value(Od8Feature.UclkFmax) - mem.Default : 0;
            int power = AdlValueRules.PowerDeltaToPercent(Value(Od8Feature.PowerPercentage));
            int? tempLimit = temp is { IsSupported: true } ? Value(Od8Feature.OperatingTempMax) : null;
            return OperationResult<GpuTunerState>.Ok(new GpuTunerState(coreOffset, memOffset, power, tempLimit, null, null, null, null, null));
        }
        catch (Exception ex)
        {
            return OperationResult<GpuTunerState>.Fail("Could not read the AMD state: " + ex.Message, ex);
        }
    }

    public OperationResult ApplyOffsets(int coreOffsetMhz, int memoryOffsetMhz)
    {
        var values = new Dictionary<int, int>();
        var core = AdlValueRules.Find(_ranges, Od8Feature.GfxClkFmax);
        var mem = AdlValueRules.Find(_ranges, Od8Feature.UclkFmax);
        if (coreOffsetMhz != 0)
        {
            if (core is not { IsSupported: true })
            {
                return OperationResult.Fail("This card does not expose a core clock limit through Overdrive8.");
            }

            values[Od8Feature.GfxClkFmax] = AdlValueRules.OffsetToAbsolute(core, coreOffsetMhz);
        }
        else if (core is { IsSupported: true })
        {
            values[Od8Feature.GfxClkFmax] = core.Default;
        }

        if (memoryOffsetMhz != 0)
        {
            if (mem is not { IsSupported: true })
            {
                return OperationResult.Fail("This card does not expose a memory clock limit through Overdrive8.");
            }

            values[Od8Feature.UclkFmax] = AdlValueRules.OffsetToAbsolute(mem, memoryOffsetMhz);
        }
        else if (mem is { IsSupported: true })
        {
            values[Od8Feature.UclkFmax] = mem.Default;
        }

        return Send(values, $"Clock targets applied ({coreOffsetMhz:+#;-#;0} / {memoryOffsetMhz:+#;-#;0} MHz from default).");
    }

    public OperationResult SetPowerLimit(int percent)
    {
        var power = AdlValueRules.Find(_ranges, Od8Feature.PowerPercentage);
        if (power is not { IsSupported: true })
        {
            return percent == 100 ? OperationResult.Ok("Power limit unchanged (not adjustable on this card).") : OperationResult.Fail("This card does not expose a power limit through Overdrive8.");
        }

        int delta = AdlValueRules.PowerPercentToDelta(percent);
        if (!AdlValueRules.IsWithin(power, delta))
        {
            return OperationResult.Fail($"The driver allows power limits from {AdlValueRules.PowerDeltaToPercent(power.Min)} % to {AdlValueRules.PowerDeltaToPercent(power.Max)} %.");
        }

        return Send(new Dictionary<int, int> { [Od8Feature.PowerPercentage] = delta }, $"Power limit set to {percent} %.");
    }

    public OperationResult SetTempLimit(int tempC)
    {
        var temp = AdlValueRules.Find(_ranges, Od8Feature.OperatingTempMax);
        if (temp is not { IsSupported: true })
        {
            return OperationResult.Fail("This card does not expose a temperature limit through Overdrive8.");
        }

        if (!AdlValueRules.IsWithin(temp, tempC))
        {
            return OperationResult.Fail($"The driver allows temperature limits from {temp.Min} to {temp.Max} °C.");
        }

        return Send(new Dictionary<int, int> { [Od8Feature.OperatingTempMax] = tempC }, $"Temperature limit set to {tempC} °C.");
    }

    public OperationResult ResetTempLimit()
    {
        var temp = AdlValueRules.Find(_ranges, Od8Feature.OperatingTempMax);
        if (temp is not { IsSupported: true })
        {
            return OperationResult.Ok("No temperature limit to reset.");
        }

        return Send(new Dictionary<int, int> { [Od8Feature.OperatingTempMax] = temp.Default }, $"Temperature limit back to the default {temp.Default} °C.");
    }

    public OperationResult SetFan(int? percent) =>
        OperationResult.Fail("AMD fan control goes through the Fans page (shared hardware monitor), not Overdrive8.");

    public OperationResult Reset(bool resetFan)
    {
        if (Method == GpuControlMethod.None)
        {
            return OperationResult.Fail("The AMD tuner is not active.");
        }

        try
        {
            int code = AmdAdlNative.SetSettings(_context, _adapterIndex, _ranges.Count, new Dictionary<int, int>(), resetAll: true);
            return code == AmdAdlNative.AdlOk
                ? OperationResult.Ok("The graphics card is back at AMD default settings.")
                : OperationResult.Fail("Overdrive8 reset failed (" + AdlValueRules.DescribeError(code) + ").");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Overdrive8 reset failed: " + ex.Message, ex);
        }
    }

    public void Dispose()
    {
        if (_context != IntPtr.Zero)
        {
            try
            {
                AmdAdlNative.MainControlDestroy(_context);
            }
            catch
            {
                // Best effort.
            }

            _context = IntPtr.Zero;
        }

        _adapterIndex = -1;
    }

    private OperationResult Send(Dictionary<int, int> values, string success)
    {
        if (Method == GpuControlMethod.None)
        {
            return OperationResult.Fail("The AMD tuner is not active.");
        }

        foreach (var (feature, value) in values)
        {
            var range = AdlValueRules.Find(_ranges, feature);
            if (range is null || !AdlValueRules.IsWithin(range, value))
            {
                return OperationResult.Fail($"Value {value} for Overdrive feature {feature} is outside the driver's range; nothing was changed.");
            }
        }

        try
        {
            int code = AmdAdlNative.SetSettings(_context, _adapterIndex, _ranges.Count, values, resetAll: false);
            if (code != AmdAdlNative.AdlOk)
            {
                return OperationResult.Fail("The AMD driver rejected the change (" + AdlValueRules.DescribeError(code) + ").");
            }

            _logger.LogInformation("Overdrive8 values applied: {Values}", string.Join(", ", values.Select(kv => kv.Key + "=" + kv.Value)));
            return OperationResult.Ok(success);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Applying the AMD change failed: " + ex.Message, ex);
        }
    }
}
