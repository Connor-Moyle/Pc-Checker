namespace PcChecker.Core.Overclocking;

/// <summary>Overdrive8 feature ids from the public ADL SDK (adl_defines.h, ADLOD8SettingId).</summary>
public static class Od8Feature
{
    public const int GfxClkFmax = 0;
    public const int GfxClkFmin = 1;
    public const int UclkFmax = 8;
    public const int PowerPercentage = 9;
    public const int FanMinSpeed = 10;
    public const int FanAcousticLimit = 11;
    public const int FanTargetTemp = 12;
    public const int OperatingTempMax = 13;
    public const int FanZeroRpmControl = 15;
    public const int FanCurveFirst = 19;
    public const int FanCurveLast = 28;
    public const int WsAutoFanAcousticLimit = 29;
    public const int UclkFmin = 34;
    public const int FanZeroRpmStopTemp = 35;

    /// <summary>GPU voltage offset in mV (RDNA3/RDNA4 undervolt; typically -200..0).</summary>
    public const int OdVoltage = 37;

    /// <summary>Overdriven interface id: internal, the SDK says "do not expose in ADL".</summary>
    public const int OverdriveInterfaceId = 74;

    /// <summary>Fan curve point n (1..5): temperature and speed feature ids.</summary>
    public static int FanCurveTemperature(int point) => FanCurveFirst + (2 * (point - 1));

    public static int FanCurveSpeed(int point) => FanCurveFirst + (2 * (point - 1)) + 1;

    /// <summary>Upper bound used for buffer sizes; the driver's own count is always honoured when smaller.</summary>
    public const int MaxCount = 256;
}

/// <summary>Overdrive8 capability bits returned by <c>ADL2_Overdrive8_Init_SettingX2_Get</c> (adl_defines.h, ADL_OD8_*).</summary>
[Flags]
public enum Od8Capability
{
    None = 0,
    GfxClkLimits = 1 << 0,
    GfxClkCurve = 1 << 1,
    UclkMax = 1 << 2,
    PowerLimit = 1 << 3,
    AcousticLimitSclk = 1 << 4,
    FanSpeedMin = 1 << 5,
    TemperatureFan = 1 << 6,
    TemperatureSystem = 1 << 7,
    MemoryTimingTune = 1 << 8,
    FanZeroRpmControl = 1 << 9,
    AutoUvEngine = 1 << 10,
    AutoOcEngine = 1 << 11,
    AutoOcMemory = 1 << 12,
    FanCurve = 1 << 13,
    WsAutoFanAcousticLimit = 1 << 14,
    GfxClkQuadraticCurve = 1 << 15,
    OptimizedGpuPowerMode = 1 << 16,
    OdVoltageLimit = 1 << 17,
}

/// <summary>One Overdrive8 feature's range as reported by <c>ADL2_Overdrive8_Init_SettingX2_Get</c>.</summary>
public sealed record Od8Range(int FeatureId, int Min, int Max, int Default)
{
    /// <summary>Range-only check, kept for drivers that report no capability mask; prefer <see cref="AdlValueRules.IsSupported"/>.</summary>
    public bool IsSupported => Max > Min || (Min == Max && Max != 0);

    /// <summary>True when the driver reports a real range to choose from.</summary>
    public bool IsAdjustable => Max > Min;
}

/// <summary>One entry of <c>ADLOD8SetSetting</c>: the driver only acts on entries flagged <see cref="Requested"/>.</summary>
public readonly record struct Od8SetEntry(int Value, bool Requested, bool Reset);

/// <summary>Pure validation and mapping between PC Checker profiles and Overdrive8 absolute values.</summary>
public static class AdlValueRules
{
    public static Od8Range? Find(IEnumerable<Od8Range> ranges, int featureId) => ranges.FirstOrDefault(r => r.FeatureId == featureId);

    /// <summary>Converts the driver's absolute clock range into an offset range relative to the default.</summary>
    public static (int MinOffset, int MaxOffset) OffsetRange(Od8Range range)
    {
        ArgumentNullException.ThrowIfNull(range);
        return (range.Min - range.Default, range.Max - range.Default);
    }

    /// <summary>Absolute value for a requested offset, clamped into the driver range.</summary>
    public static int OffsetToAbsolute(Od8Range range, int offset)
    {
        ArgumentNullException.ThrowIfNull(range);
        return Math.Clamp(range.Default + offset, Math.Min(range.Min, range.Max), Math.Max(range.Min, range.Max));
    }

    /// <summary>Overdrive8 power is a percentage delta (e.g. -50..+15); PC Checker uses 100-based percent.</summary>
    public static int PowerPercentToDelta(int percent) => percent - 100;

    public static int PowerDeltaToPercent(int delta) => 100 + delta;

    /// <summary>True when <paramref name="value"/> lies inside the driver's reported range (inclusive).</summary>
    public static bool IsWithin(Od8Range range, int value)
    {
        ArgumentNullException.ThrowIfNull(range);
        return value >= Math.Min(range.Min, range.Max) && value <= Math.Max(range.Min, range.Max);
    }

    /// <summary>The capability bit that governs a feature id, or null for ids PC Checker never touches (internal, AC timing…).</summary>
    public static Od8Capability? CapabilityFor(int featureId) => featureId switch
    {
        Od8Feature.GfxClkFmax or Od8Feature.GfxClkFmin => Od8Capability.GfxClkLimits,
        >= 2 and <= 7 => Od8Capability.GfxClkCurve,
        Od8Feature.UclkFmax or Od8Feature.UclkFmin => Od8Capability.UclkMax,
        Od8Feature.PowerPercentage => Od8Capability.PowerLimit,
        Od8Feature.FanMinSpeed => Od8Capability.FanSpeedMin,
        Od8Feature.FanAcousticLimit => Od8Capability.AcousticLimitSclk,
        Od8Feature.FanTargetTemp => Od8Capability.TemperatureFan,
        Od8Feature.OperatingTempMax => Od8Capability.TemperatureSystem,
        Od8Feature.FanZeroRpmControl or Od8Feature.FanZeroRpmStopTemp => Od8Capability.FanZeroRpmControl,
        >= Od8Feature.FanCurveFirst and <= Od8Feature.FanCurveLast => Od8Capability.FanCurve,
        Od8Feature.WsAutoFanAcousticLimit => Od8Capability.WsAutoFanAcousticLimit,
        Od8Feature.OdVoltage => Od8Capability.OdVoltageLimit,
        _ => null,
    };

    /// <summary>
    /// True when the driver supports a feature: its capability bit is set and it reports a real range (Max &gt; Min).
    /// A capability mask of <see cref="Od8Capability.None"/> (the driver reported none) falls back to the range alone.
    /// </summary>
    public static bool IsSupported(Od8Range? range, Od8Capability caps)
    {
        if (range is null || !range.IsAdjustable)
        {
            return false;
        }

        if (caps == Od8Capability.None)
        {
            return true;
        }

        return CapabilityFor(range.FeatureId) is { } bit && caps.HasFlag(bit);
    }

    /// <summary>Builds <see cref="GpuLimits"/> from the init settings; unsupported features collapse to a single value.</summary>
    public static GpuLimits BuildLimits(IReadOnlyList<Od8Range> ranges, Od8Capability caps = Od8Capability.None)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var core = Find(ranges, Od8Feature.GfxClkFmax);
        var mem = Find(ranges, Od8Feature.UclkFmax);
        var power = Find(ranges, Od8Feature.PowerPercentage);
        var temp = Find(ranges, Od8Feature.OperatingTempMax);
        var volt = Find(ranges, Od8Feature.OdVoltage);

        var (coreMin, coreMax) = IsSupported(core, caps) ? OffsetRange(core!) : (0, 0);
        var (memMin, memMax) = IsSupported(mem, caps) ? OffsetRange(mem!) : (0, 0);
        int powMin = IsSupported(power, caps) ? PowerDeltaToPercent(power!.Min) : 100;
        int powMax = IsSupported(power, caps) ? PowerDeltaToPercent(power!.Max) : 100;
        int tMin = IsSupported(temp, caps) ? temp!.Min : 0;
        int tMax = IsSupported(temp, caps) ? temp!.Max : 0;
        bool fanCurve = caps.HasFlag(Od8Capability.FanCurve) && Enumerable.Range(1, 5).All(p => IsSupported(Find(ranges, Od8Feature.FanCurveSpeed(p)), caps));
        return new GpuLimits(coreMin, coreMax, memMin, memMax, powMin, powMax, tMin, tMax, fanCurve)
        {
            VoltageOffsetMinMv = IsSupported(volt, caps) ? volt!.Min : 0,
            VoltageOffsetMaxMv = IsSupported(volt, caps) ? volt!.Max : 0,
            FanCurve = fanCurve,
        };
    }

    /// <summary>The features PC Checker writes itself, and so resets and checks after a reset.</summary>
    public static IReadOnlyList<int> ManagedFeatures { get; } =
        [Od8Feature.GfxClkFmax, Od8Feature.GfxClkFmin, Od8Feature.UclkFmax, Od8Feature.PowerPercentage, Od8Feature.OperatingTempMax, Od8Feature.OdVoltage];

    /// <summary>The smallest gap the driver needs between the minimum and maximum core clock.</summary>
    public const int MinFminGapMhz = 100;

    /// <summary>
    /// When a lower maximum core clock would come within <see cref="MinFminGapMhz"/> of the current minimum, the
    /// minimum that keeps the gap (clamped to its range); null when the minimum can stay. RDNA3/4 reject (or become
    /// unstable with) a minimum close to the maximum.
    /// </summary>
    public static int? FminFor(int newFmax, int currentFmin, Od8Range? fminRange)
    {
        if (currentFmin <= newFmax - MinFminGapMhz)
        {
            return null;
        }

        int target = newFmax - MinFminGapMhz;
        return fminRange is null ? target : Math.Clamp(target, Math.Min(fminRange.Min, fminRange.Max), Math.Max(fminRange.Min, fminRange.Max));
    }

    /// <summary>
    /// A flat 5-point fan curve for "fixed N %": every point at <paramref name="percent"/> (clamped per point), the
    /// default temperatures kept, and zero-RPM switched off so the fan really spins. RDNA3/4 accept only a curve plus
    /// minimum PWM, not a fixed duty.
    /// </summary>
    public static IReadOnlyDictionary<int, int> FlatFanCurve(IReadOnlyList<Od8Range> ranges, Od8Capability caps, int percent)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var values = new Dictionary<int, int>();
        for (int point = 1; point <= 5; point++)
        {
            var speed = Find(ranges, Od8Feature.FanCurveSpeed(point));
            var temp = Find(ranges, Od8Feature.FanCurveTemperature(point));
            if (IsSupported(speed, caps))
            {
                values[speed!.FeatureId] = Math.Clamp(percent, speed.Min, speed.Max);
            }

            if (IsSupported(temp, caps))
            {
                values[temp!.FeatureId] = temp.Default;
            }
        }

        if (IsSupported(Find(ranges, Od8Feature.FanZeroRpmControl), caps))
        {
            values[Od8Feature.FanZeroRpmControl] = percent > 0 ? 0 : 1;
        }

        return values;
    }

    /// <summary>Fan features (minimum speed, acoustic limit, target temperature, zero RPM and the fan curve).</summary>
    public static bool IsFanFeature(int featureId) =>
        featureId is Od8Feature.FanMinSpeed or Od8Feature.FanAcousticLimit or Od8Feature.FanTargetTemp or Od8Feature.FanZeroRpmControl or Od8Feature.WsAutoFanAcousticLimit
        || featureId is >= Od8Feature.FanCurveFirst and <= Od8Feature.FanCurveLast;

    /// <summary>A set table that changes only <paramref name="values"/> (feature id to value); every other entry is left alone.</summary>
    public static Od8SetEntry[] BuildSetEntries(int featureCount, IReadOnlyDictionary<int, int> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var entries = new Od8SetEntry[Math.Clamp(featureCount, 1, Od8Feature.MaxCount)];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = values.TryGetValue(i, out int v) ? new Od8SetEntry(v, true, false) : default;
        }

        return entries;
    }

    /// <summary>
    /// A set table that returns every supported feature to its default. Each entry is flagged requested as well as
    /// reset with the default as its value: the driver ignores entries that are not requested. Fan features are left
    /// alone unless <paramref name="includeFans"/> (the Fans page owns them otherwise).
    /// </summary>
    public static Od8SetEntry[] BuildResetEntries(int featureCount, IReadOnlyList<Od8Range> ranges, bool includeFans, Od8Capability caps = Od8Capability.None)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var entries = new Od8SetEntry[Math.Clamp(featureCount, 1, Od8Feature.MaxCount)];
        foreach (var id in ResetFeatureIds(ranges, includeFans, caps).Where(id => id < entries.Length))
        {
            entries[id] = new Od8SetEntry(Find(ranges, id)!.Default, true, true);
        }

        return entries;
    }

    /// <summary>
    /// The features a reset touches: only the ones PC Checker manages (plus the fan features when
    /// <paramref name="includeFans"/>), each only when its capability bit is set and it has a real range. AC timing,
    /// auto-tuning engines, curve coefficients, per-zone voltages and the internal interface id are never sent: one
    /// rejected entry makes the whole batch fail on RDNA3/4 drivers.
    /// </summary>
    public static IReadOnlyList<int> ResetFeatureIds(IReadOnlyList<Od8Range> ranges, bool includeFans, Od8Capability caps)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        return ranges
            .Where(r => (ManagedFeatures.Contains(r.FeatureId) || (includeFans && IsFanFeature(r.FeatureId)))
                && IsSupported(r, caps) && IsWithin(r, r.Default))
            .Select(r => r.FeatureId)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    /// <summary>A set table that resets only <paramref name="featureId"/> (for the one-by-one retry after a failed batch).</summary>
    public static Od8SetEntry[] BuildSingleResetEntry(int featureCount, Od8Range range)
    {
        ArgumentNullException.ThrowIfNull(range);
        var entries = new Od8SetEntry[Math.Clamp(featureCount, 1, Od8Feature.MaxCount)];
        if (range.FeatureId < entries.Length)
        {
            entries[range.FeatureId] = new Od8SetEntry(range.Default, true, true);
        }

        return entries;
    }

    /// <summary>The requested features whose read-back value differs from what was sent.</summary>
    public static IReadOnlyList<int> NotTaken(IReadOnlyDictionary<int, int> requested, IReadOnlyList<int> current)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(current);
        return requested.Where(kv => kv.Key >= current.Count || current[kv.Key] != kv.Value).Select(kv => kv.Key).OrderBy(k => k).ToList();
    }

    /// <summary>The managed features whose current value (indexed by feature id) is not the driver default.</summary>
    public static IReadOnlyList<int> FeaturesNotAtDefault(IReadOnlyList<Od8Range> ranges, IReadOnlyList<int> current, Od8Capability caps = Od8Capability.None)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(current);
        return ManagedFeatures
            .Where(f => Find(ranges, f) is { } range && IsSupported(range, caps) && (f >= current.Count || current[f] != range.Default))
            .ToList();
    }

    public static string DescribeFeature(int featureId) => featureId switch
    {
        Od8Feature.GfxClkFmax => "core clock",
        Od8Feature.GfxClkFmin => "minimum core clock",
        Od8Feature.UclkFmax => "memory clock",
        Od8Feature.OdVoltage => "voltage offset",
        >= Od8Feature.FanCurveFirst and <= Od8Feature.FanCurveLast => "fan curve",
        Od8Feature.FanZeroRpmControl => "zero RPM",
        Od8Feature.PowerPercentage => "power limit",
        Od8Feature.OperatingTempMax => "temperature limit",
        _ => $"feature {featureId}",
    };

    /// <summary>Human-readable ADL status codes (adl_defines.h).</summary>
    public static string DescribeError(int code) => code switch
    {
        0 => "OK",
        1 => "OK (warning)",
        -1 => "generic failure",
        -2 => "ADL not initialised",
        -3 => "ADL already initialised",
        -4 => "function not supported by the driver",
        -5 => "invalid parameter",
        -6 => "invalid parameter size",
        -7 => "invalid adapter index",
        -8 => "not supported by this card",
        -9 => "null pointer",
        -10 => "disabled adapter",
        -11 => "invalid callback",
        -12 => "invalid display or window handle",
        -20 => "unsupported ADL version",
        -21 => "the previous Overdrive setting has not finished applying",
        _ => $"ADL error {code}",
    };
}
