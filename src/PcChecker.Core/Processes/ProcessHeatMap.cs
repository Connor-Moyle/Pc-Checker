namespace PcChecker.Core.Processes;

/// <summary>The columns that get heat-map shading.</summary>
public enum HeatMetric
{
    Cpu,
    Memory,
    Disk,
    Network,
    Gpu,
    Power,
}

/// <summary>
/// Task Manager style heat map: busier cells get a stronger tint. The level (0 = no tint, 4 = strongest) comes from
/// fixed steps per column; the tint is the theme's orange accent drawn at <see cref="Opacity"/>, which keeps normal
/// text readable (4.5:1 or better) in both themes (the tests check this).
/// </summary>
public static class ProcessHeatMap
{
    public const int MaxLevel = 4;

    private static readonly double[] Opacities = [0, 0.12, 0.2, 0.3, 0.4];

    // Upper bounds of levels 0–3 per metric; values at or above the last bound are level 4.
    private static readonly double[] CpuSteps = [1, 5, 15, 40];
    private static readonly double[] MemoryShareSteps = [1, 3, 8, 20];
    private static readonly double[] DiskMegabyteSteps = [0.1, 1, 5, 20];
    private static readonly double[] NetworkSteps = [1, 5, 20, 50];

    /// <summary>
    /// Level of a cell. CPU and GPU are percentages, Memory is bytes (<paramref name="totalMemoryBytes"/> turns it into a
    /// share of the PC's memory), Disk is bytes per second, Network a connection count and Power a <see cref="PowerUsage"/> value.
    /// </summary>
    public static int Level(HeatMetric metric, double value, long totalMemoryBytes = 0)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return 0;
        }

        return metric switch
        {
            HeatMetric.Cpu or HeatMetric.Gpu => Step(value, CpuSteps),
            HeatMetric.Memory => totalMemoryBytes > 0 ? Step(value * 100.0 / totalMemoryBytes, MemoryShareSteps) : 0,
            HeatMetric.Disk => Step(value / (1024.0 * 1024.0), DiskMegabyteSteps),
            HeatMetric.Network => Step(value, NetworkSteps),
            HeatMetric.Power => Math.Clamp((int)Math.Round(value), 0, MaxLevel),
            _ => 0,
        };
    }

    /// <summary>Opacity of the tint for a level.</summary>
    public static double Opacity(int level) => Opacities[Math.Clamp(level, 0, MaxLevel)];

    private static int Step(double value, double[] steps)
    {
        for (int i = 0; i < steps.Length; i++)
        {
            if (value < steps[i])
            {
                return i;
            }
        }

        return MaxLevel;
    }
}
