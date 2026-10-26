using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Health;

/// <summary>
/// Demo health report for the sample gaming desktop: a healthy NVMe, an HDD with a few reallocated sectors, one
/// device without a driver, a handful of logged errors and a clean crash history. Pure .NET; runs anywhere.
/// </summary>
public sealed class DemoHealthService : IHealthService
{
    public HealthReport? Last { get; private set; }

    public async Task<HealthReport> AnalyzeAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        string[] stages =
        [
            "Drive health (SMART)", "Drive health (reliability counters)", "Battery", "Device problems", "Event log errors",
            "Reliability history", "Crash dumps", "Memory diagnostic", "Windows Update", "Free space", "Temperatures", "Security basics",
        ];
        for (int i = 0; i < stages.Length; i++)
        {
            progress?.Report(new ProgressUpdate(stages[i], 100.0 * i / stages.Length));
            await DemoData.DelayAsync(cancellationToken, 60, 160).ConfigureAwait(false);
        }

        var now = DateTimeOffset.Now;
        var report = Build(now);
        Last = report;
        progress?.Report(new ProgressUpdate("Done", 100));
        return report;
    }

    /// <summary>Builds the sample report deterministically for the given moment (also used by tests).</summary>
    public static HealthReport Build(DateTimeOffset now)
    {
        var monitoring = new MonitoringSettings();
        var items = new List<HealthItem>();

        // Drives: the NVMe is pristine, the HDD has remapped a few sectors.
        var nvmeAttributes = new List<SmartAttribute>
        {
            new(9, "Power-On Hours", 100, 100, 0, 4120, false),
            new(12, "Power Cycle Count", 100, 100, 0, 611, false),
            new(177, "Wear Leveling Count", 97, 97, 0, 42, false),
            new(194, "Temperature", 59, 52, 0, 41, false),
            new(199, "UltraDMA CRC Error Count", 100, 100, 0, 0, false),
            new(241, "Total LBAs Written", 100, 100, 0, 38_654_705_664, false),
        };
        var hddAttributes = new List<SmartAttribute>
        {
            new(1, "Raw Read Error Rate", 200, 200, 51, 0, false),
            new(3, "Spin-Up Time", 180, 177, 21, 3966, false),
            new(4, "Start/Stop Count", 100, 100, 0, 612, false),
            new(5, "Reallocated Sectors Count", 199, 199, 140, 8, false),
            new(7, "Seek Error Rate", 200, 200, 0, 0, false),
            new(9, "Power-On Hours", 80, 80, 0, 15230, false),
            new(10, "Spin Retry Count", 100, 100, 0, 0, false),
            new(12, "Power Cycle Count", 100, 100, 0, 598, false),
            new(192, "Power-Off Retract Count", 200, 200, 0, 41, false),
            new(193, "Load Cycle Count", 196, 196, 0, 12_804, false),
            new(194, "Temperature", 114, 98, 0, 36, false),
            new(196, "Reallocation Event Count", 200, 200, 0, 8, false),
            new(197, "Current Pending Sector Count", 200, 200, 0, 0, false),
            new(198, "Offline Uncorrectable Sector Count", 100, 253, 0, 0, false),
            new(199, "UltraDMA CRC Error Count", 200, 200, 0, 0, false),
        };
        var nvmeDraft = new DiskHealth(DemoData.NvmeModel, "S6Z2NJ0R512345X", "4B2QJXD7", DemoData.NvmeBytes, "SSD", HealthStatus.Unknown, "Healthy",
            41, 4120, 0, 0, 0, 3, 18_420, nvmeAttributes);
        var hddDraft = new DiskHealth(DemoData.HddModel, "WD-WX12D80K7E42", "80.00A80", DemoData.HddBytes, "HDD", HealthStatus.Unknown, "Healthy",
            36, 15_230, 8, 0, 0, null, null, hddAttributes);
        var disks = new List<DiskHealth>();
        foreach (var draft in new[] { nvmeDraft, hddDraft })
        {
            var verdict = SmartRules.Evaluate(draft, monitoring.DiskWarnC, monitoring.DiskCritC);
            var disk = draft with { Status = verdict.Status };
            disks.Add(disk);
            items.Add(HealthItemRules.Disk(disk, verdict));
        }

        // No battery: a desktop.
        var batteries = new List<BatteryHealth>();

        // One ACPI device without a driver.
        var problem28 = DeviceProblemCodes.Describe(28);
        var problems = new List<DeviceProblem>
        {
            new("ASUS ACPI Driver device", @"ACPI\ASUS2021\2&DABA3FF&0", 28, problem28.Title, problem28.Suggestion),
        };
        items.Add(HealthItemRules.DeviceProblems(problems));

        // A week of ordinary event-log noise.
        var errors = new List<SystemErrorEntry>
        {
            new(now.AddHours(-3), "Application", "Application Error", Severity.High, 1000, "Faulting application name: Discord.exe, version 1.0.9165.0, faulting module name: discord_voice.node, exception code 0xc0000005."),
            new(now.AddHours(-9), "System", "DistributedCOM", Severity.High, 10016, "The application-specific permission settings do not grant Local Activation permission for the COM Server application with CLSID {2593F8B9-4EAF-457C-B68A-50F6B8EA6B54} to the user DESKTOP\\connor SID (S-1-5-21-…)."),
            new(now.AddDays(-1).AddHours(-2), "System", "Service Control Manager", Severity.High, 7000, "The Intel(R) Management Engine WMI Provider service failed to start due to the following error: The system cannot find the file specified."),
            new(now.AddDays(-1).AddHours(-6), "System", "DistributedCOM", Severity.High, 10016, "The application-specific permission settings do not grant Local Activation permission for the COM Server application with CLSID {D63B10C5-BB46-4990-A94F-E40B9D520160}."),
            new(now.AddDays(-2).AddHours(-3), "System", "disk", Severity.High, 153, "The IO operation at logical block address 0x1a2b3c for Disk 1 (PDO name: \\Device\\0000003b) was retried."),
            new(now.AddDays(-2).AddHours(-7), "Application", "ESENT", Severity.High, 455, "svchost (1234,R,98) TILEREPOSITORYS-1-5-18: Error -1023 (0xfffffc01) occurred while opening logfile C:\\WINDOWS\\system32\\config\\systemprofile\\AppData\\Local\\TileDataLayer\\Database\\EDB.log."),
            new(now.AddDays(-3).AddHours(-1), "System", "DistributedCOM", Severity.High, 10016, "The application-specific permission settings do not grant Local Activation permission for the COM Server application with CLSID {6B3B8D23-FA8D-40B9-8DBD-B950333E2C52}."),
            new(now.AddDays(-4).AddHours(-5), "System", "Microsoft-Windows-WHEA-Logger", Severity.High, 17, "A corrected hardware error has occurred. Component: PCI Express Root Port. Error Source: Advanced Error Reporting (PCI Express)."),
            new(now.AddDays(-5).AddHours(-2), "Application", "Windows Error Reporting", Severity.High, 1001, "Fault bucket 2147942405, type 5. Event Name: APPCRASH. Response: Not available. P1: Discord.exe."),
            new(now.AddDays(-6).AddHours(-4), "System", "DistributedCOM", Severity.High, 10016, "The application-specific permission settings do not grant Local Activation permission for the COM Server application with CLSID {2593F8B9-4EAF-457C-B68A-50F6B8EA6B54}."),
            new(now.AddDays(-6).AddHours(-8), "System", "Microsoft-Windows-Kernel-PnP", Severity.High, 219, "The driver \\Driver\\WudfRd failed to load for the device SWD\\WPDBUSENUM\\_??_USBSTOR#Disk&Ven_SanDisk&Prod_Cruzer_Glide."),
        };
        items.Add(HealthItemRules.SystemErrors(errors, WindowsHealthService.ErrorWindowDays));

        var reliability = new List<ReliabilityEvent>
        {
            new(now.AddHours(-3), "Discord", ReliabilityClassifier.ApplicationFailure, "Discord.exe stopped working (APPCRASH, faulting module discord_voice.node)."),
            new(now.AddDays(-2), "Windows Update", ReliabilityClassifier.WindowsUpdate, "Installation successful: 2024-10 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5044284)."),
            new(now.AddDays(-3), "Microsoft Defender Antivirus", ReliabilityClassifier.WindowsUpdate, "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.421.164.0) installed."),
            new(now.AddDays(-8), "NVIDIA App", ReliabilityClassifier.ApplicationInstall, "NVIDIA Graphics Driver 566.03 installed successfully."),
            new(now.AddDays(-11), "Steam", ReliabilityClassifier.ApplicationInstall, "Steam client update installed."),
            new(now.AddDays(-16), "Windows Update", ReliabilityClassifier.WindowsUpdate, "Installation successful: Update for Microsoft Defender Antivirus antimalware platform - KB4052623."),
            new(now.AddDays(-24), "Adobe Acrobat Reader", ReliabilityClassifier.ApplicationUninstall, "Adobe Acrobat Reader DC was removed."),
        };

        var dumps = new List<CrashDumpInfo>
        {
            new(@"C:\Windows\Minidump\081425-9312-01.dmp", now.AddDays(-73), 1_784_120),
        };
        items.Add(HealthItemRules.CrashDumps(dumps, now));

        items.Add(HealthItemRules.MemoryDiagnostic(1201, now.AddDays(-12), "The Windows Memory Diagnostic tested the computer's memory and detected no errors.", now));
        items.Add(HealthItemRules.WindowsUpdate(now.AddDays(-2).AddHours(-1), false, now));
        items.AddRange(FreeSpaceRules.Items(
        [
            new FreeSpaceRules.DriveSpace("C:", 612L * ByteSize.Gigabyte, 1862L * ByteSize.Gigabyte),
            new FreeSpaceRules.DriveSpace("D:", 1_084L * ByteSize.Gigabyte, 3_726L * ByteSize.Gigabyte),
        ]));
        items.Add(new HealthItem("Thermal", HealthStatus.Good, "Temperatures are in the normal range", "CPU 58 °C, GPU 46 °C.", null,
            new Dictionary<string, string> { ["CPU"] = "58 °C", ["GPU"] = "46 °C" }));
        items.Add(SecurityRules.AntivirusItem([new SecurityRules.AntivirusProduct("Windows Defender", 0x061000)]));
        items.Add(SecurityRules.FirewallItem(new Dictionary<string, bool> { ["Domain"] = true, ["Private"] = true, ["Public"] = true }));
        items.Add(SecurityRules.BitLockerItem([("C:", 0), ("D:", 0)], isPortable: false));

        int score = HealthScoring.Score(items);
        return new HealthReport(score, items, disks, batteries, errors, problems, reliability, dumps, now, []);
    }
}
