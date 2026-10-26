using PcChecker.Core.Common;
using PcChecker.Core.Startup;

namespace PcChecker.Core.Performance;

/// <summary>Demo performance report: a fast PC with too many startup apps and the Balanced power plan.</summary>
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
        string[] stages = ["Startup items", "Services", "Processes", "Memory and disks", "Power and settings"];
        for (int i = 0; i < stages.Length; i++)
        {
            progress?.Report(new ProgressUpdate(stages[i], 100.0 * i / stages.Length));
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        var startup = await _startup.GetStartupItemsAsync(cancellationToken).ConfigureAwait(false);
        var services = await _startup.GetAutoStartServicesAsync(cancellationToken).ConfigureAwait(false);
        var processes = new List<ProcessInfo>
        {
            new(4312, "Discord", 2.1, 480_000_000, @"C:\Users\Alex\AppData\Local\Discord\app-1.0.9170\Discord.exe", "Discord Inc."),
            new(9120, "chrome", 4.6, 1_900_000_000, @"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google LLC"),
            new(2210, "steam", 0.8, 310_000_000, @"C:\Program Files (x86)\Steam\steam.exe", "Valve Corporation"),
            new(1180, "ArmouryCrate.Service", 1.9, 220_000_000, @"C:\Program Files\ASUS\ARMOURY CRATE Service\ArmouryCrate.Service.exe", "ASUSTeK"),
            new(880, "MsMpEng", 1.2, 260_000_000, @"C:\ProgramData\Microsoft\Windows Defender\Platform\MsMpEng.exe", "Microsoft Corporation"),
        };
        var findings = new List<SlowdownFinding>
        {
            new("startup.many", FindingCategory.Startup, Severity.Medium, "9 apps start with Windows", "Steam, Discord, Epic Games Launcher, NVIDIA App, Armoury Crate, OneDrive, G HUB, iCUE and more all start at logon.", "Slower logon and more memory in use.", [], false),
            new("power.balanced", FindingCategory.Power, Severity.Low, "Balanced power plan is active", "The Balanced plan lowers CPU clocks when idle, which can add small delays in games.", "Minor; High performance keeps clocks up.", ["power.highperformance"], false),
            new("memory.xmp", FindingCategory.Memory, Severity.Low, "RAM runs below its rated speed", "DDR5-6000 modules are running at 4800 MT/s because XMP is off in the BIOS.", "Up to 10% lower performance in memory-heavy games.", [], false),
            new("storage.hdd", FindingCategory.Storage, Severity.Info, "Games on a hard disk load slowly", "D: is a 5400 rpm hard disk. Games installed there load much slower than on the NVMe drive.", "Longer loading screens.", [], false),
        };
        Last = new PerformanceReport(82, findings, startup, services, processes, 18.4, 42, DemoData.TotalMemoryBytes, "Balanced",
            [("C:", 742_000_000_000, DemoData.NvmeBytes - 800_000_000, "SSD"), ("D:", 1_210_000_000_000, DemoData.HddBytes - 16_000_000, "HDD")], DateTimeOffset.Now, []);
        progress?.Report(new ProgressUpdate("Done", 100));
        return Last;
    }
}
