using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.Gaming;

namespace PcChecker.Core.Processes;

/// <summary>Pure arithmetic behind the Processes page: CPU share, per-second rates, GPU sums and display text.</summary>
public static class ProcessMath
{
    /// <summary>
    /// CPU use of one process in percent of the whole machine (all logical processors), from two cumulative CPU-time
    /// readings taken <paramref name="elapsed"/> apart. A counter that went backwards (process id reused) gives 0.
    /// </summary>
    public static double CpuPercent(TimeSpan before, TimeSpan after, TimeSpan elapsed, int logicalProcessors) =>
        ProcessMetrics.CpuPercent(before, after, elapsed, logicalProcessors);

    /// <summary>Bytes per second between two cumulative byte counts. A counter reset or a zero interval gives 0.</summary>
    public static double RatePerSecond(long before, long after, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || after < before)
        {
            return 0;
        }

        return (after - before) / elapsed.TotalSeconds;
    }

    /// <summary>
    /// Whole-machine CPU use from the deltas of <c>GetSystemTimes</c>. Windows counts idle time inside kernel time, so
    /// busy = kernel + user − idle.
    /// </summary>
    public static double SystemCpuPercent(long idleDelta, long kernelDelta, long userDelta)
    {
        long total = kernelDelta + userDelta;
        if (total <= 0 || idleDelta < 0)
        {
            return 0;
        }

        double busy = total - idleDelta;
        return Math.Clamp(busy / total * 100.0, 0, 100);
    }

    /// <summary>
    /// Sums "GPU Engine" utilisation per process id from (instance name, value) pairs such as
    /// <c>pid_1234_luid_…_engtype_3D</c>. Every engine of a process is added up and the total is capped at 100 %.
    /// Instances without a process id are ignored.
    /// </summary>
    public static IReadOnlyDictionary<int, double> SumGpuByPid(IEnumerable<KeyValuePair<string, double>> engineValues)
    {
        ArgumentNullException.ThrowIfNull(engineValues);
        var sums = new Dictionary<int, double>();
        foreach (var (instance, value) in engineValues)
        {
            if (double.IsNaN(value) || value <= 0 || !ProcessMetrics.TryParseGpuEnginePid(instance, out int pid))
            {
                continue;
            }

            sums[pid] = sums.GetValueOrDefault(pid) + value;
        }

        foreach (int pid in sums.Keys.ToList())
        {
            sums[pid] = Math.Min(100, sums[pid]);
        }

        return sums;
    }

    /// <summary>"0 B/s", "512 KB/s", "12.4 MB/s".</summary>
    public static string FormatRate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond < 1)
        {
            return "0 B/s";
        }

        return ByteSize.Format((long)Math.Round(bytesPerSecond)) + "/s";
    }

    /// <summary>"0 %", "0.4 %", "12 %" (one decimal below 10).</summary>
    public static string FormatPercent(double percent)
    {
        if (double.IsNaN(percent) || percent < 0.05)
        {
            return "0 %";
        }

        return (percent < 10 ? percent.ToString("0.0", CultureInfo.InvariantCulture) : percent.ToString("0", CultureInfo.InvariantCulture)) + " %";
    }
}
