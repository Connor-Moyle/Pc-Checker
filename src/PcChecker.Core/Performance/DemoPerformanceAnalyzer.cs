using PcChecker.Core.Common;
using PcChecker.Core.Startup;

namespace PcChecker.Core.Performance;

/// <summary>
/// Demo performance report for the sample gaming desktop: a fast PC with too many startup apps, the Balanced power
/// plan and XMP off. Builds real <see cref="PerformanceFacts"/> and runs <see cref="SlowdownRules"/> so the page
/// behaves exactly like a real scan. Pure .NET.
/// </summary>
public sealed class DemoPerformanceAnalyzer : IPerformanceAnalyzer
{
    private readonly IStartupService _startup;

    public DemoPerformanceAnalyzer(IStartupService startup)
    {
        _startup = startup;
    }

    public PerformanceReport? Last { get; private set; }

    public async Task<PerformanceReport> AnalyzeAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        string[] stages = ["Startup programs", "Services", "Measuring busy processes", "Memory and drives", "Power and Windows settings", "Looking for slowdowns"];
        for (int i = 0; i < stages.Length; i++)
        {
            progress?.Report(new ProgressUpdate(stages[i], 100.0 * i / stages.Length));
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }

        var startup = await _startup.GetStartupItemsAsync(cancellationToken).ConfigureAwait(false);
        var services = (await _startup.GetAutoStartServicesAsync(cancellationToken).ConfigureAwait(false))
            .Concat(ExtraServices)
            .GroupBy(s => s.Name)
            .Select(g => g.First())
            .OrderBy(s => s.IsMicrosoft)
            .ThenBy(s => s.DisplayName)
            .ToList();

        var drives = new List<(string Drive, long Free, long Total, string MediaType)>
        {
            ("C:", 742_000_000_000, DemoData.NvmeBytes - 800_000_000, "SSD"),
            ("D:", 1_210_000_000_000, DemoData.HddBytes - 16_000_000, "HDD"),
        };

        var facts = new PerformanceFacts
        {
            StartupItems = startup,
            Services = services,
            TopProcesses = TopProcesses,
            LastBootSeconds = 18.4,
            MemoryUsedPercent = 42,
            MemoryTotalBytes = DemoData.TotalMemoryBytes,
            CommitUsedPercent = 38,
            ActivePowerPlan = "Balanced",
            FastStartupEnabled = true,
            HibernationEnabled = true,
            VisualEffectsBestAppearance = false,
            GameModeEnabled = true,
            GameBarEnabled = true,
            BackgroundAppsEnabled = true,
            StorageSenseEnabled = true,
            TrimEnabled = true,
            SystemDriveIsSsd = true,
            PendingReboot = false,
            DaysSinceLastUpdateCheck = 2,
            MaxCpuTempC = 71,
            MaxGpuTempC = 66,
            OnBattery = null,
            HasBattery = false,
            UptimeDays = 9.3,
            ThirdPartyAntivirusCount = 0,
            LogicalProcessors = 24,
            Drives = drives,
        };

        var findings = SlowdownRules.Evaluate(facts).ToList();
        findings.Add(new SlowdownFinding("memory.xmp", FindingCategory.Memory, Severity.Low, "RAM runs below its rated speed",
            "The DDR5-6000 modules are running at 4800 MT/s because XMP is off in the BIOS. Turn it on in the BIOS or see the Memory page.",
            "Up to 10% lower performance in memory-heavy games", [], false));
        findings.Add(new SlowdownFinding("storage.games.hdd", FindingCategory.Storage, Severity.Info, "Games on a hard disk load slowly",
            "D: is a 5400 rpm hard disk. Games installed there load much slower than on the NVMe drive.",
            "Longer loading screens", [], false));
        var ordered = findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Category).ToList();

        Last = new PerformanceReport(SlowdownRules.Score(ordered), ordered, startup, services, TopProcesses, 18.4, 42, DemoData.TotalMemoryBytes,
            "Balanced", drives, DateTimeOffset.Now, []);
        progress?.Report(new ProgressUpdate("Done", 100));
        return Last;
    }

    private static readonly IReadOnlyList<ProcessInfo> TopProcesses =
    [
        new(9120, "chrome", 4.6, 1_900_000_000, @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google LLC"),
        new(4312, "Discord", 2.1, 480_000_000, @"C:\Users\Alex\AppData\Local\Discord\app-1.0.9170\Discord.exe", "Discord Inc."),
        new(1180, "ArmouryCrate.Service", 1.9, 220_000_000, @"C:\Program Files\ASUS\ARMOURY CRATE Service\ArmouryCrate.Service.exe", "ASUSTeK COMPUTER INC."),
        new(880, "MsMpEng", 1.2, 260_000_000, @"C:\ProgramData\Microsoft\Windows Defender\Platform\MsMpEng.exe", "Microsoft Corporation"),
        new(2210, "steam", 0.8, 310_000_000, @"C:\Program Files (x86)\Steam\steam.exe", "Valve Corporation"),
        new(6610, "lghub_agent", 0.6, 140_000_000, @"C:\Program Files\LGHUB\lghub_agent.exe", "Logitech"),
        new(3380, "iCUE", 0.5, 190_000_000, @"C:\Program Files\Corsair\CORSAIR iCUE5 Software\iCUE.exe", "Corsair Memory, Inc."),
        new(1024, "dwm", 0.4, 120_000_000, @"C:\Windows\System32\dwm.exe", "Microsoft Corporation"),
        new(7720, "OneDrive", 0.2, 95_000_000, @"C:\Users\Alex\AppData\Local\Microsoft\OneDrive\OneDrive.exe", "Microsoft Corporation"),
        new(5120, "explorer", 0.1, 160_000_000, @"C:\Windows\explorer.exe", "Microsoft Corporation"),
    ];

    private static readonly IReadOnlyList<ServiceInfo> ExtraServices =
    [
        new("AdobeUpdateService", "Adobe Update Service", "Automatic", "Running", @"C:\Program Files (x86)\Common Files\Adobe\Adobe Desktop Common\ElevationManager\AdobeUpdateService.exe", false, "Keeps Adobe applications up to date."),
        new("GoogleUpdaterService", "Google Updater Service", "Automatic", "Stopped", @"C:\Program Files (x86)\Google\GoogleUpdater\updater.exe", false, "Keeps Google software up to date."),
        new("Bonjour Service", "Bonjour Service", "Automatic", "Running", @"C:\Program Files\Bonjour\mDNSResponder.exe", false, "Enables hardware devices and software services to automatically configure themselves on the network."),
        new("LGHUBUpdaterService", "LGHUB Updater Service", "Automatic", "Running", @"C:\Program Files\LGHUB\lghub_updater.exe", false, "Keeps Logitech G HUB up to date."),
        new("CorsairService", "Corsair Service", "Automatic", "Running", @"C:\Program Files\Corsair\CORSAIR iCUE5 Software\Corsair.Service.exe", false, "Corsair iCUE device service."),
        new("Dhcp", "DHCP Client", "Automatic", "Running", @"C:\Windows\system32\svchost.exe -k LocalServiceNetworkRestricted", true, "Registers and updates IP addresses for this computer."),
        new("Spooler", "Print Spooler", "Automatic", "Running", @"C:\Windows\System32\spoolsv.exe", true, "Queues print jobs and handles interaction with the printer."),
        new("DiagTrack", "Connected User Experiences and Telemetry", "Automatic", "Running", @"C:\Windows\System32\svchost.exe -k utcsvc", true, "Collects diagnostic data."),
        new("WinDefend", "Microsoft Defender Antivirus Service", "Automatic", "Running", @"C:\ProgramData\Microsoft\Windows Defender\Platform\MsMpEng.exe", true, "Helps protect users from malware."),
        new("Themes", "Themes", "Automatic", "Running", @"C:\Windows\System32\svchost.exe -k netsvcs", true, "Provides user experience theme management."),
        new("XblAuthManager", "Xbox Live Auth Manager", "Manual", "Stopped", @"C:\Windows\system32\svchost.exe -k netsvcs", true, "Provides authentication and authorization services for interacting with Xbox Live."),
    ];
}
