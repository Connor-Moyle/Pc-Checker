using PcChecker.Core.Common;

namespace PcChecker.Core.Health;

/// <summary>Demo health report: healthy NVMe, an HDD with a few reallocated sectors, one driver problem.</summary>
public sealed class DemoHealthService : IHealthService
{
    public HealthReport? Last { get; private set; }

    public async Task<HealthReport> AnalyzeAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        string[] stages = ["Drive health (SMART)", "Device problems", "Event log errors", "Reliability history", "Crash dumps"];
        for (int i = 0; i < stages.Length; i++)
        {
            progress?.Report(new ProgressUpdate(stages[i], 100.0 * i / stages.Length));
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.Now;
        var disks = new List<DiskHealth>
        {
            new(DemoData.NvmeModel, "S6Z2NJ0DEMO0001", "4B2QJXD7", DemoData.NvmeBytes, "SSD", HealthStatus.Good, "OK", 41, 4120, 0, 0, 0, 3, 18420,
                [new(5, "Percentage Used", 3, 3, null, 3, false), new(9, "Power On Hours", null, null, null, 4120, false)]),
            new(DemoData.HddModel, "WD-WX12DEMO0002", "80.00A80", DemoData.HddBytes, "HDD", HealthStatus.Warning, "Caution", 36, 15230, 8, 0, 0, null, null,
                [new(5, "Reallocated Sectors Count", 199, 199, 140, 8, false), new(9, "Power-On Hours", 80, 80, 0, 15230, false), new(197, "Current Pending Sector", 200, 200, 0, 0, false)]),
        };
        var items = new List<HealthItem>
        {
            new("Storage", HealthStatus.Good, $"{DemoData.NvmeModel} is healthy", "3% of rated endurance used after 4,120 hours.", null, HealthItem.NoMetrics),
            new("Storage", HealthStatus.Warning, $"{DemoData.HddModel} has reallocated sectors", "8 sectors were remapped. The drive still works, but this is an early warning sign.", "Back up anything important on D: and plan a replacement.", new Dictionary<string, string> { ["Reallocated sectors"] = "8" }),
            new("Devices", HealthStatus.Warning, "One device has no driver", "ASUS ACPI device (problem code 28).", "Install the ASUS chipset / system utilities package from the motherboard support page.", HealthItem.NoMetrics),
            new("Memory", HealthStatus.Good, "Memory diagnostic passed", "Windows Memory Diagnostic found no errors 12 days ago.", null, HealthItem.NoMetrics),
            new("Stability", HealthStatus.Good, "No crashes in the last 30 days", "No blue screens or crash dumps found.", null, HealthItem.NoMetrics),
        };
        var errors = new List<SystemErrorEntry>
        {
            new(now.AddDays(-1).AddHours(-2), "System", "DistributedCOM", Severity.Low, 10016, "The application-specific permission settings do not grant Local Activation permission…"),
            new(now.AddDays(-2).AddHours(-3), "System", "disk", Severity.Medium, 153, "The IO operation at logical block address 0x1a2b3c for Disk 1 was retried."),
        };
        var problems = new List<DeviceProblem> { new("ASUS ACPI device", @"ACPI\ASUS2021", 28, DeviceProblemCodes.Describe(28).Title, DeviceProblemCodes.Describe(28).Suggestion) };
        var reliability = new List<ReliabilityEvent>
        {
            new(now.AddDays(-3), "Windows Update", "Information", "Security Intelligence Update for Microsoft Defender Antivirus installed."),
            new(now.AddDays(-6), "Discord", "Application failure", "Discord.exe stopped working (APPCRASH)."),
        };
        int score = HealthScoring.Score(items);
        Last = new HealthReport(score, items, disks, [], errors, problems, reliability, [], now, []);
        progress?.Report(new ProgressUpdate("Done", 100));
        return Last;
    }
}
