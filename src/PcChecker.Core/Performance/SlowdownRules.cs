using PcChecker.Core.Common;
using PcChecker.Core.Power;

namespace PcChecker.Core.Performance;

/// <summary>Pure rules that turn <see cref="PerformanceFacts"/> into prioritized findings and a score.</summary>
public static class SlowdownRules
{
    public const int ManyStartupItems = 8;
    public const int TooManyStartupItems = 15;
    public const double HighMemoryPercent = 85;
    public const double CriticalMemoryPercent = 95;
    public const double LowDiskPercent = 10;
    public const double CriticalDiskPercent = 5;
    public const double SlowBootSeconds = 60;
    public const double VerySlowBootSeconds = 120;
    public const long HungryProcessBytes = 2L * 1024 * 1024 * 1024;
    public const double HungryProcessCpu = 25;
    public const double UptimeRestartDays = 7;
    public const long SmallPageFileBytes = 1024L * 1024 * 1024;

    private static readonly string[] NonEssentialServiceNames =
    [
        "SysMain", "WSearch", "DiagTrack", "dmwappushservice", "RemoteRegistry", "Fax", "MapsBroker", "RetailDemo", "XblAuthManager",
        "XblGameSave", "XboxNetApiSvc", "XboxGipSvc", "WMPNetworkSvc", "PrintNotify", "Spooler", "TabletInputService", "wisvc",
    ];

    public static IReadOnlyList<SlowdownFinding> Evaluate(PerformanceFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var findings = new List<SlowdownFinding>();

        // Startup
        int enabledStartup = facts.StartupItems.Count(s => s.Enabled);
        if (enabledStartup >= TooManyStartupItems)
        {
            findings.Add(new("startup.many", FindingCategory.Startup, Severity.High, $"{enabledStartup} programs start with Windows",
                "Every program that starts automatically makes sign-in slower and uses memory in the background.",
                "Slower startup, less free memory", ["startup.review"], false));
        }
        else if (enabledStartup >= ManyStartupItems)
        {
            findings.Add(new("startup.several", FindingCategory.Startup, Severity.Medium, $"{enabledStartup} programs start with Windows",
                "Some of these may not be needed right at startup.", "Slower startup", ["startup.review"], false));
        }

        // Memory
        if (facts.MemoryUsedPercent >= CriticalMemoryPercent)
        {
            findings.Add(new("memory.critical", FindingCategory.Memory, Severity.Critical, "Memory is almost full",
                $"About {facts.MemoryUsedPercent:0}% of RAM is in use. Windows will start swapping to disk, which is much slower.",
                "Stutter, freezes", ["processes.review", "startup.review"], false));
        }
        else if (facts.MemoryUsedPercent >= HighMemoryPercent)
        {
            findings.Add(new("memory.high", FindingCategory.Memory, Severity.High, "Memory is running high",
                $"About {facts.MemoryUsedPercent:0}% of RAM is in use.", "Slowdowns when opening more programs", ["processes.review"], false));
        }

        if (facts.MemoryTotalBytes > 0 && facts.MemoryTotalBytes < 8L * 1024 * 1024 * 1024)
        {
            findings.Add(new("memory.small", FindingCategory.Memory, Severity.Medium, "Less than 8 GB of RAM",
                $"This PC has {ByteSize.Format(facts.MemoryTotalBytes)} of memory, which is tight for modern Windows and browsers.",
                "General sluggishness", ["visuals.performance", "startup.review"], false));
        }

        // Processes
        foreach (var p in facts.TopProcesses)
        {
            if (p.WorkingSetBytes >= HungryProcessBytes || p.CpuPercent >= HungryProcessCpu)
            {
                findings.Add(new($"process.{p.Pid}", FindingCategory.Processes, p.CpuPercent >= HungryProcessCpu ? Severity.Medium : Severity.Low,
                    $"{p.Name} is using a lot of resources",
                    $"{p.Name} is using {p.CpuPercent:0}% CPU and {ByteSize.Format(p.WorkingSetBytes)} of memory.",
                    "Less headroom for other programs", ["processes.review"], false));
            }
        }

        // Storage
        foreach (var (drive, free, total, mediaType) in facts.Drives)
        {
            if (total <= 0)
            {
                continue;
            }

            double freePercent = 100.0 * free / total;
            if (freePercent <= CriticalDiskPercent)
            {
                findings.Add(new($"disk.full.{drive}", FindingCategory.Storage, Severity.Critical, $"Drive {drive} is almost full",
                    $"Only {ByteSize.Format(free)} ({freePercent:0.0}%) is free. Windows needs free space for updates and temporary files.",
                    "Updates fail, programs crash", ["tempfiles.clean", "storagesense.enable"], false));
            }
            else if (freePercent <= LowDiskPercent)
            {
                findings.Add(new($"disk.low.{drive}", FindingCategory.Storage, Severity.High, $"Drive {drive} is running out of space",
                    $"{ByteSize.Format(free)} ({freePercent:0.0}%) is free.", "Slower file operations", ["tempfiles.clean", "storagesense.enable"], false));
            }

            if (mediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase) && drive.StartsWith("C", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new("disk.hdd.system", FindingCategory.Storage, Severity.High, "Windows is installed on a hard disk drive",
                    "A mechanical drive is many times slower than an SSD. Moving Windows to an SSD is the single biggest upgrade for an older PC.",
                    "Slow startup and program loading", ["defrag.optimize"], false));
            }
        }

        if (facts.SystemDriveIsSsd == true && facts.TrimEnabled == false)
        {
            findings.Add(new("disk.trim", FindingCategory.Storage, Severity.Medium, "TRIM is turned off for the SSD",
                "TRIM lets the SSD clean up deleted data so it stays fast.", "SSD slows down over time", ["trim.enable"], false));
        }

        // Power
        if (PowerPlanParser.IsPlan(facts.ActivePowerPlanId, facts.ActivePowerPlan, PowerPlanParser.PowerSaver, "saver") && facts.OnBattery != true)
        {
            findings.Add(new("power.saver", FindingCategory.Power, Severity.Medium, "Power saver plan is active while plugged in",
                "The Power saver plan limits CPU speed to save energy.", "Lower performance", ["power.balanced", "power.highperformance"], false));
        }

        if (facts.FastStartupEnabled == false && facts.SystemDriveIsSsd != true)
        {
            findings.Add(new("faststartup.off", FindingCategory.Power, Severity.Low, "Fast startup is turned off",
                "Fast startup lets Windows boot quicker after a shutdown.", "Slower boot", ["faststartup.enable"], false));
        }

        // Visuals
        if (facts.VisualEffectsBestAppearance == true && facts.MemoryTotalBytes > 0 && facts.MemoryTotalBytes < 8L * 1024 * 1024 * 1024)
        {
            findings.Add(new("visuals.heavy", FindingCategory.Visuals, Severity.Low, "Visual effects are set for best appearance",
                "Animations and transparency use GPU and memory; on a small PC they can make Windows feel slower.",
                "Slightly slower UI", ["visuals.performance", "transparency.disable", "animations.disable"], false));
        }

        if (facts.TransparencyEnabled == true && facts.VisualEffectsBestAppearance != true
            && facts.MemoryTotalBytes > 0 && facts.MemoryTotalBytes < 8L * 1024 * 1024 * 1024)
        {
            findings.Add(new("visuals.transparency", FindingCategory.Visuals, Severity.Info, "Transparency effects are on",
                "The see-through Start menu and taskbar are redrawn by the GPU every frame, which an older or integrated GPU notices.",
                "Slightly slower UI", ["transparency.disable"], false));
        }

        // Page file
        if (facts.PageFileAutomatic == false && facts.PageFileBytes is { } pageFile)
        {
            if (pageFile <= 0)
            {
                findings.Add(new("pagefile.off", FindingCategory.Memory, Severity.Medium, "The page file is turned off",
                    "Without a page file Windows cannot move idle data out of RAM, and programs fail outright when memory runs out instead of slowing down.",
                    "Crashes and \"out of memory\" errors under load", [], false));
            }
            else if (pageFile < SmallPageFileBytes && facts.MemoryTotalBytes > 0 && facts.MemoryTotalBytes < 16L * 1024 * 1024 * 1024)
            {
                findings.Add(new("pagefile.small", FindingCategory.Memory, Severity.Low, "The page file is set very small",
                    $"The page file is fixed at {ByteSize.Format(pageFile)}. Letting Windows manage its size avoids out-of-memory errors on a PC with {ByteSize.Format(facts.MemoryTotalBytes)} of RAM.",
                    "Programs may fail when memory is full", [], false));
            }
        }

        // Services
        var heavyServices = facts.Services.Where(s => NonEssentialServiceNames.Contains(s.Name, StringComparer.OrdinalIgnoreCase)
            && s.State.Equals("Running", StringComparison.OrdinalIgnoreCase)).ToList();
        if (heavyServices.Count >= 4)
        {
            findings.Add(new("services.nonessential", FindingCategory.Services, Severity.Low, $"{heavyServices.Count} optional services are running",
                "Services such as " + string.Join(", ", heavyServices.Take(4).Select(s => s.DisplayName)) + " can be stopped if you do not use them.",
                "Small background load", ["services.review"], true));
        }

        // Boot
        if (facts.LastBootSeconds is { } boot)
        {
            if (boot >= VerySlowBootSeconds)
            {
                findings.Add(new("boot.veryslow", FindingCategory.Startup, Severity.High, "Last startup took over two minutes",
                    $"Windows took about {boot:0} seconds to start.", "Long wait at sign-in", ["startup.review", "faststartup.enable"], false));
            }
            else if (boot >= SlowBootSeconds)
            {
                findings.Add(new("boot.slow", FindingCategory.Startup, Severity.Medium, "Startup is slow",
                    $"Windows took about {boot:0} seconds to start.", "Wait at sign-in", ["startup.review"], false));
            }
        }

        // Thermal
        if (facts.MaxCpuTempC is >= 90)
        {
            findings.Add(new("thermal.cpu", FindingCategory.Thermal, Severity.High, "The CPU is running very hot",
                $"The CPU reached {facts.MaxCpuTempC:0} °C. Hot CPUs slow themselves down to stay safe.", "Throttling, lower performance", [], false));
        }

        if (facts.MaxGpuTempC is >= 88)
        {
            findings.Add(new("thermal.gpu", FindingCategory.Thermal, Severity.High, "The graphics card is running very hot",
                $"The GPU reached {facts.MaxGpuTempC:0} °C.", "Throttling, lower frame rates", [], false));
        }

        // Updates / configuration
        if (facts.PendingReboot == true)
        {
            findings.Add(new("updates.reboot", FindingCategory.Updates, Severity.Medium, "A restart is pending",
                "Windows has updates waiting for a restart to finish installing.", "Updates incomplete", [], false));
        }

        if (facts.DaysSinceLastUpdateCheck is >= 30)
        {
            findings.Add(new("updates.stale", FindingCategory.Updates, Severity.Low, "Windows Update has not checked for updates recently",
                $"It has been about {facts.DaysSinceLastUpdateCheck} days since the last check.", "Missing fixes", [], false));
        }

        if (facts.BackgroundAppsEnabled == true && facts.MemoryTotalBytes > 0 && facts.MemoryTotalBytes < 8L * 1024 * 1024 * 1024)
        {
            findings.Add(new("backgroundapps.on", FindingCategory.Configuration, Severity.Low, "Store apps can run in the background",
                "Background apps use memory and CPU even when closed.", "Small background load", ["backgroundapps.disable"], false));
        }

        if (facts.StorageSenseEnabled == false)
        {
            findings.Add(new("storagesense.off", FindingCategory.Configuration, Severity.Info, "Storage Sense is turned off",
                "Storage Sense automatically clears temporary files and the recycle bin.", "Disk fills up over time", ["storagesense.enable"], false));
        }

        // Desktop-specific power choices
        // A dual-CCD Ryzen 9 X3D needs Balanced (and Game Bar) for AMD's driver to keep games on the V-Cache cores.
        bool dualX3d = Hardware.CpuTraits.IsDualCcdX3D(facts.CpuName);
        if (facts.HasBattery == false && !dualX3d && PowerPlanParser.IsPlan(facts.ActivePowerPlanId, facts.ActivePowerPlan, PowerPlanParser.Balanced, "Balanced"))
        {
            findings.Add(new("power.balanced.desktop", FindingCategory.Power, Severity.Low, "Balanced power plan on a desktop PC",
                "The Balanced plan lowers CPU clocks when idle and takes a moment to ramp back up. A desktop does not need to save battery.",
                "Small delays in games and heavy apps", ["power.highperformance"], false));
        }

        if (facts.HasBattery == false && facts.FastStartupEnabled == true && facts.SystemDriveIsSsd == true)
        {
            findings.Add(new("faststartup.on.desktop", FindingCategory.Power, Severity.Info, "Fast startup is on",
                "On a desktop with an SSD, fast startup saves only a second or two, but it stops Windows from fully restarting and can hide driver problems after updates.",
                "Occasional odd behaviour until a full restart", ["faststartup.disable"], false));
        }

        if (facts.GameBarEnabled == true && !dualX3d)
        {
            findings.Add(new("gamebar.on", FindingCategory.Configuration, Severity.Info, "Xbox Game Bar background recording is on",
                "The Game Bar keeps an overlay and its capture features ready while you play. Turning it off frees a little GPU and memory in games.",
                "Small overhead in games", ["gamebar.disable"], false));
        }

        if (facts.GameModeEnabled == false)
        {
            findings.Add(new("gamemode.off", FindingCategory.Configuration, Severity.Info, "Game Mode is turned off",
                "Game Mode tells Windows to hold back updates and background work while a game is running.",
                "Background tasks can interrupt games", ["gamemode.enable"], false));
        }

        // Uptime
        if (facts.UptimeDays is >= UptimeRestartDays)
        {
            findings.Add(new("system.uptime", FindingCategory.Configuration, Severity.Low, $"Windows has been running for {facts.UptimeDays:0} days",
                "A restart clears leaked memory, finishes pending updates and resets drivers. Fast startup does not count as a full restart.",
                "Gradual slowdowns, stuck updates", [], false));
        }

        // Third-party services known to sit in the background
        var hogs = facts.Services
            .Where(s => ServiceClassifier.IsAutomatic(s.StartMode) && ServiceClassifier.KnownHogReason(s) is not null)
            .ToList();
        if (hogs.Count > 0)
        {
            findings.Add(new("services.thirdparty", FindingCategory.Services, Severity.Info, $"{hogs.Count} third-party helper service{(hogs.Count == 1 ? string.Empty : "s")} start automatically",
                string.Join(", ", hogs.Take(5).Select(s => s.DisplayName)) + (hogs.Count > 5 ? " and more" : string.Empty)
                + ". These updaters and helpers are usually safe to set to Manual so they only run when needed.",
                "Small background load at startup", ["services.review"], false));
        }

        // Security
        if (facts.ThirdPartyAntivirusCount is > 1)
        {
            findings.Add(new("security.multipleav", FindingCategory.Configuration, Severity.Medium, "More than one antivirus product is installed",
                $"{facts.ThirdPartyAntivirusCount} antivirus products are registered. They scan the same files twice and can fight each other.",
                "Slower file access and startup", [], false));
        }

        // Commit charge (RAM + page file)
        if (facts.CommitUsedPercent is >= 90 && facts.MemoryUsedPercent < HighMemoryPercent)
        {
            findings.Add(new("memory.commit", FindingCategory.Memory, Severity.Medium, "Memory commit is nearly exhausted",
                $"Programs have reserved {facts.CommitUsedPercent:0}% of the RAM plus page file limit. Windows may refuse to open more programs.",
                "Out-of-memory errors", ["processes.review"], false));
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Category)
            .ToList();
    }

    /// <summary>100 minus penalties per finding (Critical 25, High 15, Medium 8, Low 3, Info 0).</summary>
    public static int Score(IEnumerable<SlowdownFinding> findings)
    {
        int score = 100;
        foreach (var f in findings)
        {
            score -= f.Severity switch
            {
                Severity.Critical => 25,
                Severity.High => 15,
                Severity.Medium => 8,
                Severity.Low => 3,
                _ => 0,
            };
        }

        return Math.Clamp(score, 0, 100);
    }
}
