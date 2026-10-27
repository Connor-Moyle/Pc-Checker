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

    /// <summary>
    /// GPU use per process id the way Task Manager shows it: the busiest engine's utilisation (not the sum of all
    /// engines), with that engine's name, "GPU 0 - 3D". Graphics adapters are numbered in the order of their LUIDs
    /// (idle ones included, so the numbers do not change between samples). Instances without a process id and idle
    /// engines are otherwise ignored.
    /// </summary>
    public static IReadOnlyDictionary<int, (double Percent, string Engine)> BusiestGpuEngineByPid(IEnumerable<KeyValuePair<string, double>> engineValues)
    {
        ArgumentNullException.ThrowIfNull(engineValues);
        var parsed = new List<(int Pid, string Luid, string Type, double Value)>();
        var luids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in engineValues)
        {
            if (!ProcessMetrics.TryParseGpuEnginePid(instance, out int pid))
            {
                continue;
            }

            // Every adapter counts for the numbering, idle or not, so "GPU 1" stays "GPU 1" while "GPU 0" rests.
            string luid = Part(instance, "_luid_", "_phys_") ?? string.Empty;
            luids.Add(luid);
            if (double.IsNaN(value) || value <= 0)
            {
                continue;
            }

            parsed.Add((pid, luid, Part(instance, "_engtype_", null) ?? "Engine", value));
        }

        var adapters = luids.Order(StringComparer.OrdinalIgnoreCase)
            .Select((luid, index) => (luid, index)).ToDictionary(x => x.luid, x => x.index, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<int, (double Percent, string Engine)>();
        foreach (var item in parsed)
        {
            if (!result.TryGetValue(item.Pid, out var current) || item.Value > current.Percent)
            {
                string engine = string.Create(CultureInfo.InvariantCulture, $"GPU {adapters[item.Luid]} - {item.Type}");
                result[item.Pid] = (Math.Min(100, item.Value), engine);
            }
        }

        return result;
    }

    /// <summary>"0 MB/s", "0.1 MB/s", "12.4 MB/s": disk traffic as Task Manager writes it.</summary>
    public static string FormatMegabytesPerSecond(double bytesPerSecond)
    {
        double mb = double.IsFinite(bytesPerSecond) ? Math.Max(0, bytesPerSecond) / (1024.0 * 1024.0) : 0;
        if (mb < 0.05)
        {
            return "0 MB/s";
        }

        return mb.ToString(mb < 100 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB/s";
    }

    private static string? Part(string instance, string start, string? end)
    {
        int index = instance.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        int from = index + start.Length;
        int to = end is null ? -1 : instance.IndexOf(end, from, StringComparison.OrdinalIgnoreCase);
        string value = to < 0 ? instance[from..] : instance[from..to];
        return value.Length == 0 ? null : value;
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
