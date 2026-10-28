namespace PcChecker.Core.Upgrades;

/// <summary>
/// Rule-of-thumb power supply sizing. Windows cannot read the PSU, so this adds the processor's peak power (Intel PL2,
/// AMD PPT) from a curated table, the graphics card's typical board power, and about 75 W for the board, memory,
/// drives and fans, then rounds up to a standard size with about 25 % headroom. Always labelled as an estimate.
/// </summary>
public static class PsuEstimator
{
    /// <summary>Board, memory, drives, fans and USB devices.</summary>
    public const int RestWatts = 75;

    /// <summary>Headroom over the peak so the PSU runs in its efficient, quiet range and copes with spikes.</summary>
    public const double Headroom = 1.25;

    /// <summary>Common retail PSU sizes.</summary>
    public static IReadOnlyList<int> StandardSizes { get; } = [450, 550, 650, 750, 850, 1000, 1200, 1600];

    /// <summary>Processor peak power (W) by model, before the family fallbacks.</summary>
    private static readonly (string Model, int Watts)[] CpuTable =
    [
        ("Core i9-14900KS", 253), ("Core i9-14900K", 253), ("Core i9-13900K", 253), ("Core i7-14700K", 253), ("Core i7-13700K", 253),
        ("Core i9-12900K", 241), ("Core i7-12700K", 190), ("Core i5-14600K", 181), ("Core i5-13600K", 181), ("Core i5-12600K", 150),
        ("Core Ultra 9 285K", 250), ("Core Ultra 7 265K", 250), ("Core Ultra 5 245K", 159),
        ("Core i9-11900K", 251), ("Core i7-11700K", 251), ("Core i9-10900K", 250), ("Core i7-10700K", 229), ("Core i5-10600K", 182),
        ("Ryzen 9 9950X3D", 230), ("Ryzen 9 9950X", 230), ("Ryzen 9 9900X", 162), ("Ryzen 9 7950X3D", 162), ("Ryzen 9 7950X", 230),
        ("Ryzen 9 7900X3D", 162), ("Ryzen 9 7900X", 230), ("Ryzen 7 9800X3D", 162), ("Ryzen 7 7800X3D", 162), ("Ryzen 7 9700X", 88),
        ("Ryzen 7 7700X", 142), ("Ryzen 5 9600X", 88), ("Ryzen 5 7600X", 142),
        ("Ryzen 9 5950X", 142), ("Ryzen 9 5900X", 142), ("Ryzen 7 5800X3D", 142), ("Ryzen 7 5700X3D", 142), ("Ryzen 7 5800X", 142),
        ("Ryzen 7 5700X", 76), ("Ryzen 5 5600X", 76),
    ];

    /// <summary>The processor's peak power: the table, else a family default. <paramref name="fromTable"/> says which.</summary>
    public static int CpuPeakWatts(CpuIdentity cpu, out bool fromTable)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        fromTable = false;
        if (cpu.Model is { } model)
        {
            string normalized = model.EndsWith("KF", StringComparison.OrdinalIgnoreCase) ? model[..^1] : model;
            foreach (var (name, watts) in CpuTable)
            {
                if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    fromTable = true;
                    return watts;
                }
            }
        }

        string m = cpu.Model ?? string.Empty;
        if (cpu.IsIntel)
        {
            bool k = m.EndsWith('K') || m.EndsWith("KF", StringComparison.OrdinalIgnoreCase) || m.EndsWith("KS", StringComparison.OrdinalIgnoreCase);
            bool modern = cpu.IsCoreUltra || cpu.IntelGeneration >= 12;
            bool i9OrI7 = m.Contains("i9", StringComparison.OrdinalIgnoreCase) || m.Contains("i7", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Ultra 9", StringComparison.OrdinalIgnoreCase) || m.Contains("Ultra 7", StringComparison.OrdinalIgnoreCase);
            return (modern, k, i9OrI7) switch
            {
                (true, true, true) => 253,
                (true, true, false) => 181,
                (true, false, true) => 219,
                (true, false, false) => 148,
                (false, true, _) => 150,
                _ => 95,
            };
        }

        if (cpu.IsAmd)
        {
            bool x = m.EndsWith('X') || m.EndsWith("X3D", StringComparison.OrdinalIgnoreCase) || m.EndsWith("XT", StringComparison.OrdinalIgnoreCase);
            bool ryzen9 = m.StartsWith("Ryzen 9", StringComparison.OrdinalIgnoreCase);
            return ryzen9 && cpu.Socket == CpuUpgradePaths.Am5 ? 230 : x ? 142 : 88;
        }

        return 150;
    }

    /// <summary>The graphics card's board power: the model table, else the tier default.</summary>
    public static int GpuBoardWatts(GpuClass gpu, out bool fromTable)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        fromTable = gpu.BoardPowerW is not null;
        return gpu.BoardPowerW ?? GpuRules.FallbackBoardPower(gpu.Tier);
    }

    /// <summary>The estimate for a processor and a graphics card.</summary>
    public static PsuEstimate Estimate(CpuIdentity cpu, GpuClass gpu)
    {
        int cpuW = CpuPeakWatts(cpu, out bool cpuKnown);
        int gpuW = GpuBoardWatts(gpu, out bool gpuKnown);
        return new PsuEstimate(cpuW, gpuW, RestWatts, cpuKnown, gpuKnown);
    }

    /// <summary>The smallest standard size at or above <paramref name="watts"/> (the largest size when above all of them).</summary>
    public static int RoundUpToStandard(double watts)
    {
        foreach (var size in StandardSizes)
        {
            if (size >= watts)
            {
                return size;
            }
        }

        return StandardSizes[^1];
    }
}
