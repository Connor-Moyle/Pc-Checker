using System.Globalization;

namespace PcChecker.Core.Gaming;

/// <summary>Pure helpers for the running-programs list and the foreground window check.</summary>
public static class ProcessMetrics
{
    /// <summary>
    /// Reads the process id from a "GPU Engine" performance counter instance name such as
    /// <c>pid_1234_luid_0x00000000_0x0000D1C5_phys_0_eng_0_engtype_3D</c>.
    /// </summary>
    public static bool TryParseGpuEnginePid(string? instanceName, out int processId)
    {
        processId = 0;
        if (string.IsNullOrEmpty(instanceName) || !instanceName.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int end = instanceName.IndexOf('_', 4);
        var digits = end < 0 ? instanceName.AsSpan(4) : instanceName.AsSpan(4, end - 4);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out processId) && processId > 0;
    }

    /// <summary>True for the 3D engine instances (the ones games load).</summary>
    public static bool IsGpu3DEngine(string? instanceName) =>
        instanceName is not null && instanceName.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase);

    /// <summary>CPU use in percent of the whole machine from two total-processor-time readings.</summary>
    public static double CpuPercent(TimeSpan before, TimeSpan after, TimeSpan elapsed, int logicalProcessors)
    {
        if (elapsed <= TimeSpan.Zero || logicalProcessors <= 0 || after < before)
        {
            return 0;
        }

        double percent = (after - before).TotalMilliseconds / (elapsed.TotalMilliseconds * logicalProcessors) * 100.0;
        return Math.Clamp(percent, 0, 100);
    }

    /// <summary>Busiest first: GPU use (unknown counts as 0), then CPU use, then memory.</summary>
    public static IReadOnlyList<RunningApp> Rank(IEnumerable<RunningApp> apps) =>
        apps.OrderByDescending(a => a.GpuPercent ?? 0)
            .ThenByDescending(a => a.CpuPercent)
            .ThenByDescending(a => a.WorkingSetBytes)
            .ThenBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// True when the window rectangle covers the whole monitor (within <paramref name="tolerance"/> pixels):
    /// exclusive full screen or a borderless full-screen window.
    /// </summary>
    public static bool CoversMonitor(int left, int top, int right, int bottom, int monitorLeft, int monitorTop, int monitorRight, int monitorBottom, int tolerance = 1) =>
        monitorRight > monitorLeft && monitorBottom > monitorTop
        && left <= monitorLeft + tolerance && top <= monitorTop + tolerance
        && right >= monitorRight - tolerance && bottom >= monitorBottom - tolerance;
}
