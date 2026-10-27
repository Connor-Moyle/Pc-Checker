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

    /// <summary>Upper bound used for buffer sizes; the driver's own count is always honoured when smaller.</summary>
    public const int MaxCount = 256;
}

/// <summary>One Overdrive8 feature's range as reported by <c>ADL2_Overdrive8_Init_SettingX2_Get</c>.</summary>
public sealed record Od8Range(int FeatureId, int Min, int Max, int Default)
{
    public bool IsSupported => Max > Min || (Min == Max && Max != 0);
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

    /// <summary>Builds <see cref="GpuLimits"/> from the init settings; unsupported features collapse to a single value.</summary>
    public static GpuLimits BuildLimits(IReadOnlyList<Od8Range> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var core = Find(ranges, Od8Feature.GfxClkFmax);
        var mem = Find(ranges, Od8Feature.UclkFmax);
        var power = Find(ranges, Od8Feature.PowerPercentage);
        var temp = Find(ranges, Od8Feature.OperatingTempMax);

        var (coreMin, coreMax) = core is { IsSupported: true } ? OffsetRange(core) : (0, 0);
        var (memMin, memMax) = mem is { IsSupported: true } ? OffsetRange(mem) : (0, 0);
        int powMin = power is { IsSupported: true } ? PowerDeltaToPercent(power.Min) : 100;
        int powMax = power is { IsSupported: true } ? PowerDeltaToPercent(power.Max) : 100;
        int tMin = temp is { IsSupported: true } ? temp.Min : 0;
        int tMax = temp is { IsSupported: true } ? temp.Max : 0;
        return new GpuLimits(coreMin, coreMax, memMin, memMax, powMin, powMax, tMin, tMax, false);
    }

    /// <summary>The features PC Checker writes itself, and so checks after a reset.</summary>
    public static IReadOnlyList<int> ManagedFeatures { get; } = [Od8Feature.GfxClkFmax, Od8Feature.UclkFmax, Od8Feature.PowerPercentage, Od8Feature.OperatingTempMax];

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
    public static Od8SetEntry[] BuildResetEntries(int featureCount, IReadOnlyList<Od8Range> ranges, bool includeFans)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var entries = new Od8SetEntry[Math.Clamp(featureCount, 1, Od8Feature.MaxCount)];
        for (int i = 0; i < entries.Length; i++)
        {
            var range = Find(ranges, i);
            bool reset = range is { IsSupported: true } && IsWithin(range, range.Default) && (includeFans || !IsFanFeature(i));
            entries[i] = reset ? new Od8SetEntry(range!.Default, true, true) : default;
        }

        return entries;
    }

    /// <summary>The managed features whose current value (indexed by feature id) is not the driver default.</summary>
    public static IReadOnlyList<int> FeaturesNotAtDefault(IReadOnlyList<Od8Range> ranges, IReadOnlyList<int> current)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(current);
        return ManagedFeatures
            .Where(f => Find(ranges, f) is { IsSupported: true } range && (f >= current.Count || current[f] != range.Default))
            .ToList();
    }

    public static string DescribeFeature(int featureId) => featureId switch
    {
        Od8Feature.GfxClkFmax => "core clock",
        Od8Feature.UclkFmax => "memory clock",
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
