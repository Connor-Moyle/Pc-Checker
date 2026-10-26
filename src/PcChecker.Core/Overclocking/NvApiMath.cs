namespace PcChecker.Core.Overclocking;

/// <summary>Pure unit conversions for the NVIDIA API: kHz clock deltas, per-cent-mille power targets.</summary>
public static class NvApiMath
{
    /// <summary>NVAPI expresses power targets in per-cent-mille: 100000 = 100 %.</summary>
    public const int PcmPerPercent = 1000;

    public static int KhzToMhz(int khz) => (int)Math.Round(khz / 1000.0, MidpointRounding.AwayFromZero);

    public static int KhzToMhz(long khz) => (int)Math.Round(khz / 1000.0, MidpointRounding.AwayFromZero);

    public static int MhzToKhz(int mhz) => checked(mhz * 1000);

    public static int PcmToPercent(uint pcm) => (int)Math.Round(pcm / (double)PcmPerPercent, MidpointRounding.AwayFromZero);

    public static uint PercentToPcm(int percent) => (uint)Math.Max(0, percent) * PcmPerPercent;

    /// <summary>Converts a kHz delta range from the driver into MHz; a degenerate range (min == max == 0) means "not editable".</summary>
    public static (int MinMhz, int MaxMhz, bool Editable) DeltaRangeToMhz(int minKhz, int maxKhz)
    {
        int min = KhzToMhz(minKhz), max = KhzToMhz(maxKhz);
        if (min > max)
        {
            (min, max) = (max, min);
        }

        return (min, max, !(min == 0 && max == 0));
    }

    /// <summary>Clamps a requested MHz offset into the driver's kHz range and returns the kHz value to send.</summary>
    public static int OffsetMhzToDeltaKhz(int offsetMhz, int minKhz, int maxKhz)
    {
        int khz = MhzToKhz(offsetMhz);
        if (minKhz > maxKhz)
        {
            (minKhz, maxKhz) = (maxKhz, minKhz);
        }

        return Math.Clamp(khz, minKhz, maxKhz);
    }

    /// <summary>Builds <see cref="GpuLimits"/> from raw driver ranges; missing policies collapse to a single value and a note.</summary>
    public static GpuLimits BuildLimits(
        (int MinKhz, int MaxKhz)? coreDelta,
        (int MinKhz, int MaxKhz)? memoryDelta,
        (uint MinPcm, uint MaxPcm)? power,
        (int MinC, int MaxC)? thermal,
        bool fanControl)
    {
        var core = coreDelta is { } c ? DeltaRangeToMhz(c.MinKhz, c.MaxKhz) : (0, 0, false);
        var mem = memoryDelta is { } m ? DeltaRangeToMhz(m.MinKhz, m.MaxKhz) : (0, 0, false);
        int powMin = power is { } p ? PcmToPercent(p.MinPcm) : 100;
        int powMax = power is { } p2 ? PcmToPercent(p2.MaxPcm) : 100;
        int tMin = thermal is { } t ? t.MinC : 0;
        int tMax = thermal is { } t2 ? t2.MaxC : 0;
        return new GpuLimits(core.Item1, core.Item2, mem.Item1, mem.Item2, Math.Min(powMin, powMax), Math.Max(powMin, powMax), Math.Min(tMin, tMax), Math.Max(tMin, tMax), fanControl);
    }
}
