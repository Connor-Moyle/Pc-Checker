namespace PcChecker.Core.History;

/// <summary>
/// Builds believable history for the demo desktop: the PC is on from the morning until late evening with light
/// office use, games most evenings (longer at weekends) and is off one day. A handful of moments are planted so the
/// notable-events list has something to show: a GPU stress test a few hours ago, a CPU hot spell three days ago and
/// a critical one twelve days ago, a large game install, a health check that scored lower, and so on. Every value
/// comes from a hash of the minute, so the same <c>now</c> always gives the same data. Days older than a week are
/// rolled up exactly as the real store would keep them.
/// </summary>
public static class DemoHistoryGenerator
{
    public const int DefaultDays = 30;

    /// <summary>Samples for the <paramref name="days"/> days up to <paramref name="now"/>, oldest first.</summary>
    public static IReadOnlyList<HistorySample> Generate(DateTimeOffset now, int days = DefaultDays, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var end = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 60 * 60);
        var start = end.AddDays(-Math.Clamp(days, 1, 120));
        var stress = (From: end.AddHours(-6), To: end.AddHours(-6).AddMinutes(25));
        var raw = new List<HistorySample>((int)(end - start).TotalMinutes);
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end, zone).DateTime);

        for (var t = start.AddMinutes(1); t <= end; t = t.AddMinutes(1))
        {
            var local = TimeZoneInfo.ConvertTime(t, zone);
            var date = DateOnly.FromDateTime(local.DateTime);
            int daysAgo = localToday.DayNumber - date.DayNumber;
            int minuteOfDay = local.Hour * 60 + local.Minute;
            bool inStress = t > stress.From && t <= stress.To;
            var day = DayPlan.For(date, daysAgo);
            if (!inStress && !day.IsOn(minuteOfDay))
            {
                continue;
            }

            long key = t.ToUnixTimeSeconds() / 60;
            var values = Minute(key, day, minuteOfDay, inStress ? (t - stress.From).TotalMinutes : -1);
            if (local.Minute == 7)
            {
                values[HistoryMetrics.DiskFree("C:")] = FreeSpaceC(daysAgo, minuteOfDay, key);
                values[HistoryMetrics.DiskFree("D:")] = FreeSpaceD(daysAgo, minuteOfDay, key);
            }

            raw.Add(new HistorySample(t, 60, values));
            if (day.HealthScore is { } score && minuteOfDay == day.HealthMinute)
            {
                raw.Add(new HistorySample(t.AddSeconds(20), 0, new Dictionary<string, double> { [HistoryMetrics.Health] = score }));
            }
        }

        // Keep older days at the resolution the real store would have rolled them up to.
        var today = HistoryRetention.DayOf(end);
        var result = new List<HistorySample>(raw.Count);
        foreach (var group in raw.GroupBy(s => HistoryRetention.DayOf(s.Time)).OrderBy(g => g.Key))
        {
            var bucket = HistoryRollup.StorageBucketFor(group.Key, today);
            result.AddRange(bucket is { } b ? HistoryRollup.Compact(group, b) : group);
        }

        return result;
    }

    private static Dictionary<string, double> Minute(long key, DayPlan day, int minuteOfDay, double stressMinute)
    {
        bool gaming = day.IsGaming(minuteOfDay);
        double cpuLoad, gpuLoad, cpuTemp, gpuTemp, ram;
        if (stressMinute >= 0)
        {
            // GPU stress test: load pinned, temperature climbing past the 83 °C warning limit.
            cpuLoad = 18 + 4 * Smooth(key, 1);
            gpuLoad = 99;
            gpuTemp = Math.Min(86.4, 66 + stressMinute * 1.6) + 0.6 * Smooth(key, 2);
            cpuTemp = 48 + 3 * Smooth(key, 3);
            ram = 47 + 2 * Smooth(key, 4);
        }
        else if (gaming)
        {
            cpuLoad = 44 + 10 * Smooth(key, 1);
            gpuLoad = 92 + 5 * Smooth(key, 5);
            cpuTemp = 69 + 5 * Smooth(key, 3) + day.CpuHeat(minuteOfDay);
            gpuTemp = 68 + 3 * Smooth(key, 2);
            ram = 63 + 4 * Smooth(key, 4);
        }
        else
        {
            bool burst = Noise(key, 6) > 0.93;
            cpuLoad = burst ? 55 + 15 * Noise(key, 7) : 9 + 5 * Math.Abs(Smooth(key, 1));
            gpuLoad = 4 + 3 * Math.Abs(Smooth(key, 5));
            cpuTemp = 39 + cpuLoad * 0.28 + 2 * Smooth(key, 3) + day.CpuHeat(minuteOfDay);
            gpuTemp = 36 + 2 * Smooth(key, 2);
            ram = 39 + 3 * Smooth(key, 4);
        }

        double power = 72 + cpuLoad * 1.7 + gpuLoad * 2.3 + 6 * Smooth(key, 8);
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [HistoryMetrics.CpuTemp] = Math.Round(Math.Min(cpuTemp, 99), 1), // CPUs throttle before 100 °C
            [HistoryMetrics.GpuTemp] = Math.Round(gpuTemp, 1),
            [HistoryMetrics.CpuLoad] = Math.Round(Math.Clamp(cpuLoad, 0, 100), 1),
            [HistoryMetrics.GpuLoad] = Math.Round(Math.Clamp(gpuLoad, 0, 100), 1),
            [HistoryMetrics.Power] = Math.Round(power),
            [HistoryMetrics.Ram] = Math.Round(Math.Clamp(ram, 0, 100), 1),
        };
    }

    /// <summary>System drive: slowly filling, a 74 GB game install nine days ago, a clean-up two days ago.</summary>
    private static double FreeSpaceC(int daysAgo, int minuteOfDay, long key)
    {
        double free = 642 - (DefaultDays - daysAgo) * 0.9 - minuteOfDay / 1440.0 * 0.9 + 0.3 * Noise(key, 9);
        if (daysAgo < 9 || (daysAgo == 9 && minuteOfDay >= 20 * 60))
        {
            free -= 74;
        }

        if (daysAgo < 2 || (daysAgo == 2 && minuteOfDay >= 11 * 60))
        {
            free += 31;
        }

        return Math.Round(free, 1);
    }

    /// <summary>Game library drive: a 150 GB copy five days ago.</summary>
    private static double FreeSpaceD(int daysAgo, int minuteOfDay, long key)
    {
        double free = 2405 - (DefaultDays - daysAgo) * 0.4 + 0.2 * Noise(key, 10);
        if (daysAgo < 5 || (daysAgo == 5 && minuteOfDay >= 21 * 60))
        {
            free -= 150;
        }

        return Math.Round(free, 1);
    }

    /// <summary>Value noise in [-1, 1] that changes smoothly over about five minutes.</summary>
    private static double Smooth(long minute, int channel)
    {
        long cell = minute / 5;
        double f = (minute % 5) / 5.0;
        f = f * f * (3 - 2 * f);
        return Noise(cell, channel) * (1 - f) + Noise(cell + 1, channel) * f;
    }

    /// <summary>Deterministic hash noise in [-1, 1] (SplitMix64).</summary>
    private static double Noise(long key, int channel)
    {
        ulong z = unchecked((ulong)key * 0x9E3779B97F4A7C15UL + (ulong)channel * 0xD1B54A32D192ED03UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53)) * 2 - 1;
    }

    /// <summary>When the PC is on and when it games on one calendar day.</summary>
    private sealed record DayPlan(int DaysAgo, int OnFrom, int OnTo, int GameFrom, int GameTo, int AfternoonFrom, int AfternoonTo, int? HealthScore)
    {
        private static readonly Dictionary<int, int> HealthChecks = new() { [28] = 94, [25] = 94, [22] = 93, [19] = 94, [13] = 92, [10] = 92, [6] = 85, [3] = 90, [1] = 92 };

        public static DayPlan For(DateOnly date, int daysAgo)
        {
            if (daysAgo == 16)
            {
                return new DayPlan(daysAgo, -1, -1, -1, -1, -1, -1, null);
            }

            long seed = date.DayNumber;
            bool weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            int onFrom = (weekend ? 10 * 60 : 8 * 60) + (int)(45 + 45 * Noise(seed, 20));
            int onTo = Math.Min(23 * 60 + 59, 23 * 60 + (int)(25 + 30 * Noise(seed, 21)));
            int gameFrom = 19 * 60 + (int)(30 + 30 * Noise(seed, 22));
            int gameTo = gameFrom + (weekend ? 210 : 140) + (int)(30 * Noise(seed, 23));
            bool skipGaming = Noise(seed, 24) > 0.7 && daysAgo != 3 && daysAgo != 12;
            int afternoonFrom = weekend ? 14 * 60 + 10 : -1;
            int afternoonTo = weekend ? 16 * 60 + 40 : -1;
            return new DayPlan(daysAgo, onFrom, Math.Max(onTo, Math.Min(gameTo + 20, 23 * 60 + 59)),
                skipGaming ? -1 : gameFrom, skipGaming ? -1 : gameTo, afternoonFrom, afternoonTo,
                HealthChecks.TryGetValue(daysAgo, out var score) ? score : null);
        }

        /// <summary>A health check runs a little after the PC is switched on, not before 10:15.</summary>
        public int HealthMinute => Math.Max(10 * 60 + 15, OnFrom + 20);

        public bool IsOn(int minute) => OnFrom >= 0 && minute >= OnFrom && minute < OnTo;

        public bool IsGaming(int minute) =>
            (GameFrom >= 0 && minute >= GameFrom && minute < GameTo) || (AfternoonFrom >= 0 && minute >= AfternoonFrom && minute < AfternoonTo);

        /// <summary>Extra CPU heat for the planted hot spells (a long render three days ago, a critical one twelve days ago).</summary>
        public double CpuHeat(int minute)
        {
            int into = minute - (GameFrom >= 0 ? GameFrom : 20 * 60);
            return DaysAgo switch
            {
                3 when into is >= 50 and < 66 => 21,
                12 when into is >= 30 and < 37 => 31,
                _ => 0,
            };
        }
    }
}
