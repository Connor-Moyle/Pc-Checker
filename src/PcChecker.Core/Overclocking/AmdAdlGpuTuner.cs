using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// AMD Radeon tuning through ADL Overdrive8 (experimental). Every value is validated against the ranges and the
/// capability mask the driver itself reports; any ADL failure during detection drops the method to
/// <see cref="GpuControlMethod.None"/>. After every change the driver's current values are read back, so a value the
/// driver silently ignored (manual tuning off in AMD Software) is reported as not applied.
/// </summary>
public sealed class AmdAdlGpuTuner : IGpuTuner, IGpuVoltageTuner, IGpuTuningSwitch
{
    private readonly ILogger _logger;
    private readonly string? _preferredName;
    private IntPtr _context;
    private int _adapterIndex = -1;
    private IReadOnlyList<Od8Range> _ranges = [];
    private Od8Capability _caps;
    private string? _gpuName;

    public AmdAdlGpuTuner(ILogger logger, string? preferredName = null)
    {
        _logger = logger;
        _preferredName = preferredName;
    }

    public GpuVendor Vendor => GpuVendor.Amd;

    public GpuControlMethod Method => _adapterIndex >= 0 ? GpuControlMethod.AmdAdl : GpuControlMethod.None;

    public GpuLimits? Limits { get; private set; }

    /// <summary>False when AMD Software reports Overdrive (manual tuning) as switched off.</summary>
    public bool TuningEnabled { get; private set; } = true;

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

        var notes = new List<string> { "AMD Overdrive8 support is experimental. Values are checked against the driver's own limits before they are sent and read back afterwards." };
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

            var (initCode, caps, ranges) = AmdAdlNative.GetInitSettings(_context, picked.AdapterIndex);
            if (initCode != AmdAdlNative.AdlOk || ranges.Length == 0)
            {
                return Fail($"Overdrive8 ranges could not be read ({AdlValueRules.DescribeError(initCode)}).");
            }

            _adapterIndex = picked.AdapterIndex;
            _gpuName = picked.AdapterName;
            _ranges = ranges;
            _caps = (Od8Capability)caps;
            Limits = AdlValueRules.BuildLimits(ranges, _caps);
            TuningEnabled = enabled != 0;
            if (!TuningEnabled)
            {
                notes.Add("Overdrive is switched off in AMD Software → Performance → Tuning. Turn on manual tuning there first; until then the driver ignores the values.");
            }

            if (Limits.VoltageOffsetSupported)
            {
                notes.Add($"This card supports a voltage offset ({Limits.VoltageOffsetMinMv} to {Limits.VoltageOffsetMaxMv} mV): an undervolt is the safest way to lower power and heat.");
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
            var core = Supported(Od8Feature.GfxClkFmax);
            var mem = Supported(Od8Feature.UclkFmax);
            var temp = Supported(Od8Feature.OperatingTempMax);
            int coreOffset = core is not null ? Value(Od8Feature.GfxClkFmax) - core.Default : 0;
            int memOffset = mem is not null ? Value(Od8Feature.UclkFmax) - mem.Default : 0;
            int power = AdlValueRules.PowerDeltaToPercent(Value(Od8Feature.PowerPercentage));
            int? tempLimit = temp is not null ? Value(Od8Feature.OperatingTempMax) : null;
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
        var core = Supported(Od8Feature.GfxClkFmax);
        var mem = Supported(Od8Feature.UclkFmax);
        if (coreOffsetMhz != 0)
        {
            if (core is null)
            {
                return OperationResult.Fail("This card does not expose a maximum frequency through Overdrive8.");
            }

            values[Od8Feature.GfxClkFmax] = AdlValueRules.OffsetToAbsolute(core, coreOffsetMhz);
        }
        else if (core is not null)
        {
            values[Od8Feature.GfxClkFmax] = core.Default;
        }

        // A lower maximum must stay at least 100 MHz above the minimum, or RDNA3/4 reject it or become unstable.
        if (values.TryGetValue(Od8Feature.GfxClkFmax, out int fmax) && Supported(Od8Feature.GfxClkFmin) is { } fminRange)
        {
            var (readCode, current) = AmdAdlNative.GetCurrentSettings(_context, _adapterIndex);
            int currentFmin = readCode == AmdAdlNative.AdlOk && Od8Feature.GfxClkFmin < current.Length ? current[Od8Feature.GfxClkFmin] : fminRange.Default;
            if (AdlValueRules.FminFor(fmax, currentFmin, fminRange) is { } newFmin)
            {
                if (newFmin > fmax - AdlValueRules.MinFminGapMhz)
                {
                    return OperationResult.Fail($"A maximum frequency of {fmax} MHz is too close to the card's minimum frequency; choose a smaller negative offset.");
                }

                values[Od8Feature.GfxClkFmin] = newFmin;
            }
        }

        if (memoryOffsetMhz != 0)
        {
            if (mem is null)
            {
                return OperationResult.Fail("This card does not expose a memory clock limit through Overdrive8.");
            }

            values[Od8Feature.UclkFmax] = AdlValueRules.OffsetToAbsolute(mem, memoryOffsetMhz);
        }
        else if (mem is not null)
        {
            values[Od8Feature.UclkFmax] = mem.Default;
        }

        return Send(values, $"Max frequency and memory targets applied ({coreOffsetMhz:+#;-#;0} / {memoryOffsetMhz:+#;-#;0} MHz from default).");
    }

    public OperationResult SetPowerLimit(int percent)
    {
        var power = Supported(Od8Feature.PowerPercentage);
        if (power is null)
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
        var temp = Supported(Od8Feature.OperatingTempMax);
        if (temp is null)
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
        var temp = Supported(Od8Feature.OperatingTempMax);
        if (temp is null)
        {
            return OperationResult.Ok("No temperature limit to reset.");
        }

        return Send(new Dictionary<int, int> { [Od8Feature.OperatingTempMax] = temp.Default }, $"Temperature limit back to the default {temp.Default} °C.");
    }

    public OperationResult SetVoltageOffset(int millivolts)
    {
        var volt = Supported(Od8Feature.OdVoltage);
        if (volt is null)
        {
            return millivolts == 0 ? OperationResult.Ok("Voltage unchanged (no offset on this card).") : OperationResult.Fail("This card does not offer a voltage offset through Overdrive8.");
        }

        if (!AdlValueRules.IsWithin(volt, millivolts))
        {
            return OperationResult.Fail($"The driver allows voltage offsets from {volt.Min} to {volt.Max} mV.");
        }

        return Send(new Dictionary<int, int> { [Od8Feature.OdVoltage] = millivolts }, millivolts == 0 ? "Voltage offset back to stock." : $"Voltage offset set to {millivolts:+#;-#;0} mV.");
    }

    /// <summary>
    /// Fixed fan speed through the Overdrive8 fan curve: RDNA cards ignore the Overdrive5 fan calls LibreHardwareMonitor
    /// uses, and RDNA3/4 accept only a curve, so "fixed N %" becomes a flat curve. Null returns the fan features to
    /// their defaults (automatic).
    /// </summary>
    public OperationResult SetFan(int? percent)
    {
        if (Limits is not { FanCurve: true })
        {
            return OperationResult.Fail("This card does not expose an Overdrive8 fan curve. Set the fan in AMD Software → Performance → Tuning.");
        }

        if (percent is null)
        {
            var defaults = _ranges.Where(r => AdlValueRules.IsFanFeature(r.FeatureId) && AdlValueRules.IsSupported(r, _caps))
                .ToDictionary(r => r.FeatureId, r => r.Default);
            return Send(defaults, "GPU fan back to automatic (AMD default curve).");
        }

        var curve = AdlValueRules.FlatFanCurve(_ranges, _caps, percent.Value);
        return Send(curve.ToDictionary(kv => kv.Key, kv => kv.Value), $"GPU fan curve set flat at {percent} % (AMD Overdrive8).");
    }

    public OperationResult Reset(bool resetFan)
    {
        if (Method == GpuControlMethod.None)
        {
            return OperationResult.Fail("The AMD tuner is not active.");
        }

        try
        {
            // Only the features PC Checker manages (and the fans when asked) that the capability mask allows: AC
            // timings, auto-tuning engines and internal ids are never sent, so one rejected entry cannot sink the reset.
            var entries = AdlValueRules.BuildResetEntries(_ranges.Count, _ranges, includeFans: resetFan, _caps);
            int code = AmdAdlNative.SetSettings(_context, _adapterIndex, entries);
            if (code != AmdAdlNative.AdlOk)
            {
                _logger.LogWarning("Overdrive8 batch reset failed ({Code}); retrying feature by feature.", code);
                var failed = new List<string>();
                foreach (var id in AdlValueRules.ResetFeatureIds(_ranges, resetFan, _caps))
                {
                    int one = AmdAdlNative.SetSettings(_context, _adapterIndex, AdlValueRules.BuildSingleResetEntry(_ranges.Count, AdlValueRules.Find(_ranges, id)!));
                    if (one != AmdAdlNative.AdlOk)
                    {
                        failed.Add($"{AdlValueRules.DescribeFeature(id)} ({AdlValueRules.DescribeError(one)})");
                    }
                }

                if (failed.Count > 0)
                {
                    return OperationResult.Fail("The AMD driver did not reset: " + string.Join(", ", failed) + ". Use Reset in AMD Software: Adrenalin Edition → Performance → Tuning.");
                }
            }

            // Only report stock once the driver confirms it.
            var (readCode, current) = AmdAdlNative.GetCurrentSettings(_context, _adapterIndex);
            if (readCode != AmdAdlNative.AdlOk)
            {
                return OperationResult.Fail("The Overdrive8 reset was sent, but the current values could not be read back to confirm it (" + AdlValueRules.DescribeError(readCode) + "). Check AMD Software: Adrenalin Edition → Performance → Tuning.");
            }

            var notDefault = AdlValueRules.FeaturesNotAtDefault(_ranges, current, _caps);
            if (notDefault.Count > 0)
            {
                return OperationResult.Fail("The AMD driver accepted the reset but still reports non-default values for: " + string.Join(", ", notDefault.Select(AdlValueRules.DescribeFeature))
                    + ". Use Reset in AMD Software: Adrenalin Edition → Performance → Tuning.");
            }

            _logger.LogInformation("Overdrive8 reset confirmed by read-back.");
            return OperationResult.Ok("The graphics card is back at AMD default settings.");
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

    private Od8Range? Supported(int featureId)
    {
        var range = AdlValueRules.Find(_ranges, featureId);
        return AdlValueRules.IsSupported(range, _caps) ? range : null;
    }

    private OperationResult Send(Dictionary<int, int> values, string success)
    {
        if (Method == GpuControlMethod.None)
        {
            return OperationResult.Fail("The AMD tuner is not active.");
        }

        if (values.Count == 0)
        {
            return OperationResult.Ok(success);
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
            int code = AmdAdlNative.SetSettings(_context, _adapterIndex, AdlValueRules.BuildSetEntries(_ranges.Count, values));
            if (code != AmdAdlNative.AdlOk)
            {
                return OperationResult.Fail("The AMD driver rejected the change (" + AdlValueRules.DescribeError(code) + ").");
            }

            // Read back: with manual tuning off in AMD Software the driver accepts the call and ignores the values.
            var (readCode, current) = AmdAdlNative.GetCurrentSettings(_context, _adapterIndex);
            if (readCode == AmdAdlNative.AdlOk)
            {
                var notTaken = AdlValueRules.NotTaken(values, current);
                if (notTaken.Count > 0)
                {
                    return OperationResult.Fail("The AMD driver did not take: " + string.Join(", ", notTaken.Select(AdlValueRules.DescribeFeature).Distinct())
                        + (TuningEnabled ? "." : ". Turn on manual tuning in AMD Software → Performance → Tuning first."));
                }
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
