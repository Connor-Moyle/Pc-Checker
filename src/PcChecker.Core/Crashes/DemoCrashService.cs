using PcChecker.Core.Common;
using PcChecker.Core.Crashes.Dumps;

namespace PcChecker.Core.Crashes;

/// <summary>
/// Sample crashes for demo mode and the screenshots: two blue screens (VIDEO_TDR_FAILURE in nvlddmkm.sys and a
/// WHEA_UNCORRECTABLE_ERROR with an overclock applied), one sudden shutdown, three graphics driver resets and app
/// crashes for two games. Built through the same merge rules as the real scan.
/// </summary>
public sealed class DemoCrashService : ICrashService
{
    public CrashReport? Last { get; private set; }

    public event EventHandler<CrashReport>? Scanned;

    public string MinidumpFolder => @"C:\Windows\Minidump";

    public async Task<CrashReport> ScanAsync(IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressUpdate("Reading the System log…", 20));
        await Task.Delay(300, cancellationToken);
        progress?.Report(new ProgressUpdate("Reading crash dump files…", 60));
        await Task.Delay(300, cancellationToken);
        var report = Build(DateTimeOffset.Now);
        Last = report;
        Scanned?.Invoke(this, report);
        return report;
    }

    public Task<CrashGlance> GetGlanceAsync(CancellationToken cancellationToken) =>
        Task.FromResult(CrashRules.Glance((Last ?? Build(DateTimeOffset.Now)).Events, DateTimeOffset.Now));

    /// <summary>The sample report, relative to <paramref name="now"/>.</summary>
    public static CrashReport Build(DateTimeOffset now)
    {
        var day = new DateTimeOffset(now.Date, now.Offset);
        DateTimeOffset At(int daysAgo, int hour, int minute) => day.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute);

        var modules = new List<CrashModule>
        {
            new("ntoskrnl.exe", 0xFFFFF80612400000, 0x1047000),
            new("hal.dll", 0xFFFFF80611E00000, 0x6000),
            new("dxgkrnl.sys", 0xFFFFF80619A00000, 0x3C5000),
            new("watchdog.sys", 0xFFFFF80619900000, 0x1A000),
            new("nvlddmkm.sys", 0xFFFFF8061C200000, 0x5A40000),
            new("storport.sys", 0xFFFFF80613B00000, 0xB2000),
            new("stornvme.sys", 0xFFFFF80613C00000, 0x3B000),
            new("tcpip.sys", 0xFFFFF80615400000, 0x31D000),
            new("e2f68.sys", 0xFFFFF80616A00000, 0x9B000),
            new("rt640x64.sys", 0xFFFFF80616B00000, 0xD2000),
        };

        var tdrBsodTime = At(3, 21, 14);
        var wheaTime = At(12, 19, 42);
        var tdrDump = new DumpFile(@"C:\Windows\Minidump\" + tdrBsodTime.ToString("MMddyy", System.Globalization.CultureInfo.InvariantCulture) + "-8421-01.dmp",
            tdrBsodTime.AddMinutes(1), 2_621_440, new CrashDumpInfo
            {
                Format = DumpFormat.Kernel64,
                KindText = "Small memory dump",
                DumpType = KernelDumpReader.TriageDumpType,
                BugCheckCode = 0x116,
                Parameters = [0xFFFFB30A4C7E8010, 0xFFFFF8061C9D3A40, 0xFFFFFFFFC000009A, 0x4],
                OsBuild = 26100,
                Architecture = "x64",
                CrashTime = tdrBsodTime,
                Modules = modules,
            });
        var wheaDump = new DumpFile(@"C:\Windows\Minidump\" + wheaTime.ToString("MMddyy", System.Globalization.CultureInfo.InvariantCulture) + "-7031-01.dmp",
            wheaTime.AddMinutes(1), 1_835_008, new CrashDumpInfo
            {
                Format = DumpFormat.Kernel64,
                KindText = "Small memory dump",
                DumpType = KernelDumpReader.TriageDumpType,
                BugCheckCode = 0x124,
                Parameters = [0x0, 0xFFFFC1027A6E2028, 0xBE000000, 0x800400],
                OsBuild = 26100,
                Architecture = "x64",
                CrashTime = wheaTime,
                Modules = modules,
            });

        var events = new List<CrashEvent>
        {
            new()
            {
                Id = "demo-bsod-116", Kind = CrashKind.BlueScreen, Time = tdrBsodTime.AddMinutes(2), Source = "System log, event 1001 (BugCheck)",
                LogName = "System", EventId = 1001, BugCheckCode = 0x116, Parameters = tdrDump.Info!.Parameters, DumpPath = tdrDump.Path,
                ReportId = "a3c1f0e2-6b7d-4d58-9a41-5c2e8f7b9d10",
            },
            new()
            {
                Id = "demo-kp41-116", Kind = CrashKind.BlueScreen, Time = tdrBsodTime.AddMinutes(1), Source = "System log, event 41 (Kernel-Power)",
                LogName = "System", EventId = 41, BugCheckCode = 0x116, Parameters = tdrDump.Info!.Parameters,
            },
            new()
            {
                Id = "demo-bsod-124", Kind = CrashKind.BlueScreen, Time = wheaTime.AddMinutes(2), Source = "System log, event 1001 (BugCheck)",
                LogName = "System", EventId = 1001, BugCheckCode = 0x124, Parameters = wheaDump.Info!.Parameters, DumpPath = wheaDump.Path,
                ReportId = "5e9b2d71-0c84-4f3a-b6e2-91d7a8c4f035",
            },
            new()
            {
                Id = "demo-kp41-0", Kind = CrashKind.SuddenShutdown, Time = At(20, 23, 5), Source = "System log, event 41 (Kernel-Power)",
                LogName = "System", EventId = 41,
            },
            new()
            {
                Id = "demo-6008", Kind = CrashKind.SuddenShutdown, Time = At(20, 23, 6), Source = "System log, event 6008 (EventLog)",
                LogName = "System", EventId = 6008,
            },
            Reset("demo-tdr-1", At(3, 21, 9), "nvlddmkm"),
            Reset("demo-tdr-2", At(9, 22, 31), "nvlddmkm"),
            Reset("demo-tdr-3", At(25, 20, 3), "nvlddmkm"),
            App("demo-app-1", At(1, 22, 47), "Cyberpunk2077.exe", "3.0.78.62467", "nvwgf2umx.dll", "32.0.15.6614", 0xC0000005, "0x0000000000e4a1b7"),
            App("demo-app-2", At(4, 20, 12), "Cyberpunk2077.exe", "3.0.78.62467", "nvwgf2umx.dll", "32.0.15.6614", 0xC0000005, "0x0000000000e4a1b7"),
            App("demo-app-3", At(10, 21, 55), "Cyberpunk2077.exe", "3.0.78.62467", "Cyberpunk2077.exe", "3.0.78.62467", 0xC0000409, "0x0000000004d1c2f0"),
            Hang("demo-hang-1", At(6, 19, 20), "Cyberpunk2077.exe", "3.0.78.62467"),
            App("demo-app-4", At(2, 18, 33), "eldenring.exe", "2.6.1.0", "eldenring.exe", "2.6.1.0", 0xC0000005, "0x0000000001a3f9c4"),
            App("demo-app-5", At(15, 23, 48), "eldenring.exe", "2.6.1.0", "ntdll.dll", "10.0.26100.2454", 0xC0000374, "0x0000000000103e49"),
        };

        var watchdog = new DumpFile(@"C:\Windows\LiveKernelReports\WATCHDOG\WATCHDOG-" + At(9, 22, 31).ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture) + ".dmp",
            At(9, 22, 31), 412_160_000, new CrashDumpInfo { Format = DumpFormat.Kernel64, KindText = "Kernel memory dump", BugCheckCode = 0x141, OsBuild = 26100 }, IsGraphicsWatchdog: true);

        return new CrashReport
        {
            Captured = now,
            Events = CrashRules.Merge(events, [tdrDump, wheaDump, watchdog], now),
            WheaErrors = 6,
            OverclockApplied = true,
        };
    }

    private static CrashEvent Reset(string id, DateTimeOffset time, string driver) => new()
    {
        Id = id, Kind = CrashKind.DriverReset, Time = time, Source = "System log, event 4101 (Display)", LogName = "System", EventId = 4101, Driver = driver,
    };

    private static CrashEvent App(string id, DateTimeOffset time, string app, string version, string module, string moduleVersion, uint code, string offset) => new()
    {
        Id = id, Kind = CrashKind.AppCrash, Time = time, Source = "Application log, event 1000 (Application Error)", LogName = "Application", EventId = 1000,
        AppName = app, AppVersion = version, ModuleName = module, ModuleVersion = moduleVersion, ExceptionCode = code, FaultOffset = offset,
        AppPath = app.StartsWith("Cyberpunk", StringComparison.Ordinal)
            ? @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe"
            : @"D:\SteamLibrary\steamapps\common\ELDEN RING\Game\eldenring.exe",
    };

    private static CrashEvent Hang(string id, DateTimeOffset time, string app, string version) => new()
    {
        Id = id, Kind = CrashKind.AppHang, Time = time, Source = "Application log, event 1002 (Application Hang)", LogName = "Application", EventId = 1002,
        AppName = app, AppVersion = version, AppPath = @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe",
    };
}
