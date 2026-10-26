namespace PcChecker.Core.Common;

/// <summary>
/// Shared facts about the sample machine every Demo* service describes, plus a small artificial delay so busy
/// states are visible in screenshots. Pure .NET; usable anywhere.
/// </summary>
public static class DemoData
{
    public const string ComputerName = "GAMING-DESKTOP";
    public const string CpuName = "Intel Core i7-13700K";
    public const string GpuName = "NVIDIA GeForce RTX 4070";
    public const long GpuMemoryBytes = 12L * 1024 * 1024 * 1024;
    public const string MotherboardName = "ASUS ROG STRIX Z790-E GAMING WIFI";
    public const string MemoryKit = "G.Skill Trident Z5 DDR5-6000 (2 x 16 GB)";
    public const long MemoryModuleBytes = 16L * 1024 * 1024 * 1024;
    public const long TotalMemoryBytes = 2 * MemoryModuleBytes;
    public const string NvmeModel = "Samsung SSD 990 PRO 2TB";
    public const long NvmeBytes = 2_000_398_934_016;
    public const string HddModel = "WDC WD40EZAZ-00SF3B0";
    public const long HddBytes = 4_000_787_030_016;
    public const string OsName = "Microsoft Windows 11 Pro";
    public const string OsVersion = "24H2 (build 26100)";
    public const string MonitorName = "ASUS ROG SWIFT PG279QM";
    public const string KeyboardName = "Corsair K70 RGB PRO";
    public const string MouseName = "Logitech G PRO X SUPERLIGHT";

    /// <summary>Deterministic pseudo-random source so sample data looks alive but stays repeatable.</summary>
    public static Random CreateRandom(int seed = 4070) => new(seed);

    /// <summary>Waits a short, cancellable moment (100–600 ms) so busy overlays render in demo mode.</summary>
    public static Task DelayAsync(CancellationToken cancellationToken, int minMs = 100, int maxMs = 600)
    {
        int ms = Math.Clamp(Random.Shared.Next(minMs, maxMs + 1), 0, 5000);
        return Task.Delay(ms, cancellationToken);
    }

    /// <summary>Adds a small wobble to a base value (for live-looking sensor data).</summary>
    public static double Jitter(Random random, double value, double amplitude) =>
        value + (random.NextDouble() * 2 - 1) * amplitude;
}
