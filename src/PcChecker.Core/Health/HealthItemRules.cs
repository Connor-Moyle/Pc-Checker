using PcChecker.Core.Common;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Health;

/// <summary>
/// Pure builders that turn collected facts (disks, batteries, dumps, update state, sensors…) into
/// <see cref="HealthItem"/>s with beginner-friendly wording. Everything here is unit-testable on any OS.
/// </summary>
public static class HealthItemRules
{
    public const int CrashWindowDays = 30;
    public const int UpdateStaleDays = 45;
    public const int ErrorFloodPerDay = 25;

    /// <summary>
    /// The drive inventory's Good / Caution / Bad verdict (the one the Storage page shows) as a Health verdict, so both
    /// pages judge a drive the same way.
    /// </summary>
    public static SmartRules.Verdict FromDriveVerdict(Storage.DriveHealthVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var status = verdict.Verdict switch
        {
            Storage.DriveVerdict.Good => HealthStatus.Good,
            Storage.DriveVerdict.Caution => HealthStatus.Warning,
            Storage.DriveVerdict.Bad => HealthStatus.Critical,
            _ => HealthStatus.Unknown,
        };
        var reasons = new List<string>();
        if (verdict.Verdict != Storage.DriveVerdict.Good)
        {
            reasons.Add(verdict.Reason);
            reasons.AddRange(verdict.Findings.Where(f => f != verdict.Reason));
        }

        return new SmartRules.Verdict(status, verdict.Label + ": " + verdict.Reason, reasons);
    }

    /// <summary>Disk item from the drive facts and its SMART verdict.</summary>
    public static HealthItem Disk(DiskHealth disk, SmartRules.Verdict verdict) => Disk(disk, verdict, null);

    /// <summary>
    /// Disk item; with the drive inventory's verdict the title uses its per-drive wording ("Samsung 870 EVO: Caution").
    /// </summary>
    public static HealthItem Disk(DiskHealth disk, SmartRules.Verdict verdict, Storage.DriveHealthVerdict? driveVerdict)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(verdict);
        var metrics = new Dictionary<string, string>();
        if (disk.TemperatureC is { } t)
        {
            metrics["Temperature"] = $"{t:0} °C";
        }

        if (disk.PowerOnHours is { } h)
        {
            metrics["Power-on hours"] = h.ToString("N0");
        }

        if (disk.WearPercent is { } w)
        {
            metrics["Wear"] = $"{w:0}% used";
        }

        if (disk.ReallocatedSectors is { } r)
        {
            metrics["Reallocated sectors"] = r.ToString("N0");
        }

        if (disk.PendingSectors is { } p)
        {
            metrics["Pending sectors"] = p.ToString("N0");
        }

        if (disk.UncorrectableErrors is { } u)
        {
            metrics["Uncorrectable errors"] = u.ToString("N0");
        }

        string title = driveVerdict is { Verdict: not Storage.DriveVerdict.Unknown } dv ? $"{disk.Model}: {dv.Label}" : verdict.Status switch
        {
            HealthStatus.Critical => $"{disk.Model} shows signs of failure",
            HealthStatus.Warning => $"{disk.Model} has early warning signs",
            HealthStatus.Unknown => $"{disk.Model} did not report its health",
            _ => $"{disk.Model} is healthy",
        };
        string detail = verdict.Reasons.Count > 0 ? string.Join(" ", verdict.Reasons) : DescribeHealthyDisk(disk);
        string? recommendation = verdict.Status switch
        {
            HealthStatus.Critical => "Back up your files right away and replace the drive. Run a surface scan from the Storage page to confirm.",
            HealthStatus.Warning => "Keep a current backup and watch this drive. A surface scan from the Storage page shows whether bad spots are spreading.",
            HealthStatus.Unknown => "Some USB enclosures and RAID controllers hide SMART data. Check the drive with the manufacturer's tool.",
            _ => null,
        };
        return new HealthItem("Storage", verdict.Status, title, detail, recommendation, metrics);
    }

    private static string DescribeHealthyDisk(DiskHealth disk)
    {
        var parts = new List<string>();
        if (disk.WearPercent is { } w)
        {
            parts.Add($"{w:0}% of rated endurance used");
        }

        if (disk.PowerOnHours is { } h)
        {
            parts.Add($"{h:N0} hours of use");
        }

        if (disk.TemperatureC is { } t)
        {
            parts.Add($"{t:0} °C");
        }

        return parts.Count == 0 ? "The drive's self-check reports no problems." : "No problems reported. " + string.Join(", ", parts) + ".";
    }

    /// <summary>Battery item from wear and cycle count.</summary>
    public static HealthItem Battery(BatteryHealth battery)
    {
        ArgumentNullException.ThrowIfNull(battery);
        var status = HealthScoring.BatteryStatus(battery.WearPercent);
        var metrics = new Dictionary<string, string>();
        if (battery.DesignCapacityMwh is { } d)
        {
            metrics["Design capacity"] = $"{d:N0} mWh";
        }

        if (battery.FullChargeCapacityMwh is { } f)
        {
            metrics["Full charge capacity"] = $"{f:N0} mWh";
        }

        if (battery.CycleCount is { } c)
        {
            metrics["Charge cycles"] = c.ToString("N0");
        }

        if (battery.ChargePercent is { } p)
        {
            metrics["Charge"] = $"{p}%";
        }

        return status switch
        {
            HealthStatus.Critical => new HealthItem("Battery", status, $"{battery.Name} has lost {battery.WearPercent:0}% of its capacity",
                "The battery holds much less charge than when it was new, so run time on battery is short.",
                "Plan a battery replacement. Keep the laptop plugged in when possible until then.", metrics),
            HealthStatus.Warning => new HealthItem("Battery", status, $"{battery.Name} has worn by {battery.WearPercent:0}%",
                "Normal ageing, but the battery holds noticeably less than its design capacity.",
                "Avoid leaving it at 100% charge for long periods and keep it cool to slow further wear.", metrics),
            HealthStatus.Unknown => new HealthItem("Battery", status, $"{battery.Name} did not report its capacity",
                "Windows could not read the design or full-charge capacity, so wear cannot be calculated.",
                "Run 'powercfg /batteryreport' in a terminal for a detailed report.", metrics),
            _ => new HealthItem("Battery", status, $"{battery.Name} is in good condition",
                $"Wear is {battery.WearPercent:0}%; the battery still holds most of its original capacity.", null, metrics),
        };
    }

    /// <summary>Device Manager problems as one item (none → good).</summary>
    public static HealthItem DeviceProblems(IReadOnlyList<DeviceProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        if (problems.Count == 0)
        {
            return new HealthItem("Devices", HealthStatus.Good, "All devices are working", "Device Manager reports no problem codes.", null, HealthItem.NoMetrics);
        }

        // Code 22 (disabled by the user) and 45 (unplugged) are not faults.
        var real = problems.Where(p => p.ErrorCode is not (22 or 45)).ToList();
        var metrics = UniqueMetrics(problems, p => p.Name, p => $"Code {p.ErrorCode}");
        if (real.Count == 0)
        {
            return new HealthItem("Devices", HealthStatus.Good, $"{problems.Count} device{(problems.Count > 1 ? "s are" : " is")} disabled or unplugged",
                "These entries are not faults: the devices were turned off on purpose or are simply not connected right now.", null, metrics);
        }

        var first = real[0];
        string detail = real.Count == 1
            ? $"{first.Name}: {first.Description} (code {first.ErrorCode})."
            : $"{string.Join(", ", real.Take(3).Select(p => p.Name))}{(real.Count > 3 ? $" and {real.Count - 3} more" : string.Empty)} report problem codes.";
        return new HealthItem("Devices", HealthStatus.Warning,
            real.Count == 1 ? "One device has a problem" : $"{real.Count} devices have problems",
            detail, real.Count == 1 ? first.Suggestion : "Open the Device problems section below for the suggested fix for each device.", metrics);
    }

    /// <summary>
    /// Builds a metrics dictionary where repeated names (several "USB Input Device" entries, two antivirus registrations
    /// with the same display name) get a " (2)", " (3)"… suffix instead of throwing on the duplicate key.
    /// </summary>
    internal static Dictionary<string, string> UniqueMetrics<T>(IEnumerable<T> items, Func<T, string> key, Func<T, string> value)
    {
        var metrics = new Dictionary<string, string>();
        var seen = new Dictionary<string, int>();
        foreach (var item in items)
        {
            string name = key(item) ?? string.Empty;
            string unique = name;
            int count = seen.TryGetValue(name, out var c) ? c : 0;
            while (metrics.ContainsKey(unique))
            {
                count++;
                unique = $"{name} ({count + 1})";
            }

            seen[name] = count;
            metrics[unique] = value(item);
        }

        return metrics;
    }

    /// <summary>Crash dump item: any dump newer than 30 days is a warning, older dumps are mentioned but not penalised.</summary>
    public static HealthItem CrashDumps(IReadOnlyList<CrashDumpInfo> dumps, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(dumps);
        var recent = dumps.Where(d => (now - d.Time).TotalDays <= CrashWindowDays).OrderByDescending(d => d.Time).ToList();
        var metrics = new Dictionary<string, string> { ["Dump files"] = dumps.Count.ToString() };
        if (recent.Count == 0)
        {
            return new HealthItem("Stability", HealthStatus.Good, $"No crashes in the last {CrashWindowDays} days",
                dumps.Count == 0 ? "No blue screen crash dumps were found." : $"No blue screens recently; {dumps.Count} older dump file{(dumps.Count > 1 ? "s" : string.Empty)} on disk.",
                null, metrics);
        }

        metrics["Most recent"] = recent[0].Time.ToString("yyyy-MM-dd HH:mm");
        var status = recent.Count >= 3 ? HealthStatus.Critical : HealthStatus.Warning;
        return new HealthItem("Stability", status,
            recent.Count == 1 ? $"A blue screen happened in the last {CrashWindowDays} days" : $"{recent.Count} blue screens in the last {CrashWindowDays} days",
            $"Windows wrote a crash dump on {recent[0].Time:d MMM yyyy} at {recent[0].Time:HH:mm}. Repeated crashes usually point to a driver, overheating or faulty memory.",
            "Update drivers (Drivers page), check temperatures (Temperatures page) and run the memory test on the Memory page. Share the dump files with support if it keeps happening.",
            metrics);
    }

    /// <summary>Memory diagnostic result (event 1201 = no errors, 1202 = errors found). Null means never run.</summary>
    public static HealthItem MemoryDiagnostic(int? eventId, DateTimeOffset? when, string? message, DateTimeOffset now)
    {
        if (eventId is null)
        {
            return new HealthItem("Memory", HealthStatus.Unknown, "Memory diagnostic has not run yet",
                "Windows Memory Diagnostic finds faulty RAM, a common cause of random crashes and corrupted files.",
                "Run a RAM test from the Memory page, or schedule Windows Memory Diagnostic for the next restart.", HealthItem.NoMetrics);
        }

        var metrics = new Dictionary<string, string>();
        if (when is { } w)
        {
            metrics["Last run"] = w.ToString("yyyy-MM-dd HH:mm");
        }

        string ago = when is { } t ? $" {DescribeAge(now - t)}" : string.Empty;
        if (eventId == 1202 || (message?.Contains("hardware problems", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return new HealthItem("Memory", HealthStatus.Critical, "Memory diagnostic found errors",
                $"Windows Memory Diagnostic detected hardware problems{ago}. Faulty RAM causes crashes and silent file corruption.",
                "Test each memory module on its own to find the bad one, then replace it. Reset any XMP/EXPO overclock to defaults first.", metrics);
        }

        return new HealthItem("Memory", HealthStatus.Good, "Memory diagnostic passed",
            $"Windows Memory Diagnostic found no errors{ago}.", null, metrics);
    }

    /// <summary>Windows Update item from the last successful install time and the pending-reboot flags.</summary>
    public static HealthItem WindowsUpdate(DateTimeOffset? lastSuccess, bool rebootPending, DateTimeOffset now)
    {
        var metrics = new Dictionary<string, string>();
        if (lastSuccess is { } l)
        {
            metrics["Last successful install"] = l.ToString("yyyy-MM-dd HH:mm");
        }

        metrics["Restart pending"] = rebootPending ? "Yes" : "No";

        if (rebootPending)
        {
            return new HealthItem("Windows Update", HealthStatus.Warning, "A restart is needed to finish updates",
                "Windows has installed updates that only take effect after a restart. Until then the PC runs with a half-applied update.",
                "Save your work and restart the PC.", metrics);
        }

        if (lastSuccess is null)
        {
            return new HealthItem("Windows Update", HealthStatus.Unknown, "No update history found",
                "Windows did not record when it last installed updates.", "Open Settings → Windows Update and check for updates.", metrics);
        }

        double days = (now - lastSuccess.Value).TotalDays;
        if (days > UpdateStaleDays)
        {
            return new HealthItem("Windows Update", HealthStatus.Warning, $"No updates installed for {days:0} days",
                "Security fixes arrive monthly; a PC that has not updated for a while is missing them.",
                "Open Settings → Windows Update and install the available updates.", metrics);
        }

        return new HealthItem("Windows Update", HealthStatus.Good, "Windows is up to date",
            $"Updates were last installed {DescribeAge(now - lastSuccess.Value)}; no restart is pending.", null, metrics);
    }

    /// <summary>Summarises event-log errors: a flood of errors is a warning, otherwise informational.</summary>
    public static HealthItem SystemErrors(IReadOnlyList<SystemErrorEntry> errors, int windowDays)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var metrics = new Dictionary<string, string> { ["Errors"] = errors.Count.ToString("N0") };
        int critical = errors.Count(e => e.Level == Severity.Critical);
        if (critical > 0)
        {
            metrics["Critical"] = critical.ToString("N0");
        }

        foreach (var (source, count) in TopSources(errors, 3))
        {
            metrics[source] = count.ToString("N0");
        }

        if (errors.Count == 0)
        {
            return new HealthItem("Event log", HealthStatus.Good, $"No errors in the last {windowDays} days",
                "The System and Application logs contain no critical or error entries.", null, metrics);
        }

        double perDay = (double)errors.Count / Math.Max(1, windowDays);
        if (critical > 0)
        {
            return new HealthItem("Event log", HealthStatus.Warning, $"{critical} critical event{(critical > 1 ? "s" : string.Empty)} in the last {windowDays} days",
                "Critical events usually mean an unexpected shutdown or a kernel-level fault (for example Kernel-Power 41).",
                "Check the Recent system errors section for the source; unexpected shutdowns often come from power, overheating or an unstable overclock.", metrics);
        }

        if (perDay >= ErrorFloodPerDay)
        {
            var top = TopSources(errors, 1).FirstOrDefault();
            return new HealthItem("Event log", HealthStatus.Warning, $"{errors.Count:N0} errors logged in {windowDays} days",
                $"That is about {perDay:0} per day{(top.Source is null ? string.Empty : $", mostly from \"{top.Source}\"")}. A single misbehaving service or driver can flood the log.",
                "Filter the Recent system errors list by the top source and fix or update that component.", metrics);
        }

        return new HealthItem("Event log", HealthStatus.Good, $"{errors.Count:N0} error{(errors.Count > 1 ? "s" : string.Empty)} in the last {windowDays} days",
            "A handful of logged errors is normal; nothing critical was recorded.", null, metrics);
    }

    /// <summary>Counts errors by source, most frequent first.</summary>
    public static IReadOnlyList<(string Source, int Count)> TopSources(IEnumerable<SystemErrorEntry> errors, int take = 10)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return errors.GroupBy(e => e.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Source: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Source, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, take))
            .ToList();
    }

    /// <summary>CPU/GPU temperature items from the latest sensor snapshot and the user's thresholds.</summary>
    public static IReadOnlyList<HealthItem> Thermal(IEnumerable<SensorReading>? readings, MonitoringSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var items = new List<HealthItem>();
        if (readings is null)
        {
            return items;
        }

        // Role-aware: the CPU control temperature (not raw Tctl), the discrete GPU's core (not hot spot, memory or the
        // integrated Radeon), and the Ryzen 7000/9000 thresholds when the user kept the stock values.
        var list = readings.Where(r => r.Value is { } v && !double.IsNaN(v)).ToList();
        var snapshot = new SensorSnapshot(DateTimeOffset.MinValue, list);
        double? cpu = Overclocking.SensorPicker.MaxCpuTemp(snapshot);
        double? gpu = Overclocking.SensorPicker.MaxGpuTemp(snapshot);
        var (cpuWarn, cpuCrit) = TemperatureRoles.CpuThresholds(settings, Overclocking.SensorPicker.CpuName(snapshot));
        if (cpu is null && gpu is null)
        {
            return items;
        }

        var metrics = new Dictionary<string, string>();
        if (cpu is { } c)
        {
            metrics["CPU"] = $"{c:0} °C";
        }

        if (gpu is { } g)
        {
            metrics["GPU"] = $"{g:0} °C";
        }

        if (cpu >= cpuCrit)
        {
            items.Add(new HealthItem("Thermal", HealthStatus.Critical, $"The CPU is very hot ({cpu:0} °C)",
                "The processor is at or above its critical temperature and will slow itself down (thermal throttling) to avoid damage.",
                "Check that the CPU cooler is seated and its fan spins, clean dust filters, and lower any overclock. See the Fans page to raise fan speeds.", metrics));
        }
        else if (cpu >= cpuWarn)
        {
            items.Add(new HealthItem("Thermal", HealthStatus.Warning, $"The CPU is running warm ({cpu:0} °C)",
                "Fine under a heavy load, but a concern if the PC is idle.", "Improve airflow or raise the fan curve on the Fans page.", metrics));
        }

        if (gpu >= settings.GpuCritC)
        {
            items.Add(new HealthItem("Thermal", HealthStatus.Critical, $"The graphics card is very hot ({gpu:0} °C)",
                "The GPU is at or above its critical temperature and will throttle.",
                "Clean the card's fans and heatsink, check case airflow and lower any overclock.", metrics));
        }
        else if (gpu >= settings.GpuWarnC)
        {
            items.Add(new HealthItem("Thermal", HealthStatus.Warning, $"The graphics card is running warm ({gpu:0} °C)",
                "Fine while gaming; a concern when idle.", "Improve airflow or set a more aggressive GPU fan curve.", metrics));
        }

        if (items.Count == 0)
        {
            items.Add(new HealthItem("Thermal", HealthStatus.Good, "Temperatures are in the normal range",
                string.Join(", ", metrics.Select(m => $"{m.Key} {m.Value}")) + ".", null, metrics));
        }

        return items;
    }

    /// <summary>"3 days ago", "2 hours ago", "just now".</summary>
    public static string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return $"{age.TotalMinutes:0} minute{(age.TotalMinutes >= 1.5 ? "s" : string.Empty)} ago";
        }

        if (age.TotalDays < 1)
        {
            return $"{age.TotalHours:0} hour{(age.TotalHours >= 1.5 ? "s" : string.Empty)} ago";
        }

        return $"{age.TotalDays:0} day{(age.TotalDays >= 1.5 ? "s" : string.Empty)} ago";
    }
}
