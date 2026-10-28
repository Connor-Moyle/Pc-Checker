using System.Globalization;
using PcChecker.Core.Common;
using PcChecker.Core.Crashes.Dumps;
using PcChecker.Core.History;
using PcChecker.Core.Logging;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Crashes;

/// <summary>
/// Sample crashes for demo mode and the screenshots, built through the same merge rules as the real scan, with
/// everything around them so every section of the page is filled: a VIDEO_TDR_FAILURE in nvlddmkm.sys (driver 566.36,
/// a newer one available, crashes only since that driver was installed), a WHEA_UNCORRECTABLE_ERROR machine check 20
/// minutes after PC Checker applied an overclock with the CPU at 96 °C, a sudden power loss, three graphics driver
/// resets, a game crash in the Discord overlay (DiscordHook64.dll) and a .NET program crash (0xe0434352).
/// </summary>
public sealed class DemoCrashService : ICrashService
{
    private readonly ISettingsService? _settings;

    public DemoCrashService()
    {
    }

    public DemoCrashService(ISettingsService settings) => _settings = settings;

    public CrashReport? Last { get; private set; }

    public event EventHandler<CrashReport>? Scanned;

    public string MinidumpFolder => @"C:\Windows\Minidump";

    /// <summary>The demo NVIDIA driver: 566.36 installed (32.0.15.6636), 572.83 offered.</summary>
    public const string DemoDriverVersion = "32.0.15.6636";

    public const string DemoNewerDriver = "572.83";

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

    public Task<CrashGlance> GetGlanceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> solved = _settings?.Current.Crashes?.SolvedIds ?? [];
        return Task.FromResult(CrashRules.Glance((Last ?? Build(DateTimeOffset.Now)).Events, DateTimeOffset.Now, CrashRules.AttentionDays, solved));
    }

    public async Task<CrashContextData> LoadContextAsync(IReadOnlyList<CrashEvent> crashes, CancellationToken cancellationToken)
    {
        await Task.Delay(150, cancellationToken);
        return Context(DateTimeOffset.Now, crashes);
    }

    public async Task<CrashDriverDetails?> InspectDriverAsync(string module, CancellationToken cancellationToken)
    {
        await Task.Delay(120, cancellationToken);
        return DriverDetails(module);
    }

    /// <summary>The sample driver details: NVIDIA's display driver with a newer version available; Windows files otherwise.</summary>
    public static CrashDriverDetails? DriverDetails(string module)
    {
        if (string.IsNullOrWhiteSpace(module))
        {
            return null;
        }

        var file = Path.GetFileName(module.Trim());
        if (file.StartsWith("nvlddmkm", StringComparison.OrdinalIgnoreCase))
        {
            return new CrashDriverDetails
            {
                FileName = "nvlddmkm.sys",
                Path = @"C:\Windows\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_5d0e2c6b1a3f9e44\nvlddmkm.sys",
                Company = "NVIDIA Corporation",
                Description = "NVIDIA Windows Kernel Mode Driver, Version 566.36",
                FileVersion = DemoDriverVersion,
                FileDate = new DateTimeOffset(2024, 12, 5, 0, 0, 0, TimeSpan.Zero),
                Signer = "Microsoft Windows Hardware Compatibility Publisher",
                IsMicrosoft = false,
                DeviceName = DemoData.GpuName,
                DeviceId = @"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043",
                DriverVersion = DemoDriverVersion,
                DriverDate = new DateTime(2024, 12, 5),
                NewerVersion = DemoNewerDriver,
                NewerSource = "NVIDIA",
                UpdatesChecked = true,
            };
        }

        return new CrashDriverDetails
        {
            FileName = file,
            Path = @"C:\Windows\System32\drivers\" + file,
            Company = "Microsoft Corporation",
            FileVersion = "10.0.26100.2454",
            Signer = "Microsoft Windows",
            IsMicrosoft = true,
        };
    }

    private static DateTimeOffset Day(DateTimeOffset now) => new(now.Date, now.Offset);

    private static DateTimeOffset At(DateTimeOffset now, int daysAgo, int hour, int minute) => Day(now).AddDays(-daysAgo).AddHours(hour).AddMinutes(minute);

    /// <summary>The sample report, relative to <paramref name="now"/>.</summary>
    public static CrashReport Build(DateTimeOffset now)
    {
        DateTimeOffset At(int daysAgo, int hour, int minute) => DemoCrashService.At(now, daysAgo, hour, minute);

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
        var tdrDump = new DumpFile(@"C:\Windows\Minidump\" + tdrBsodTime.ToString("MMddyy", CultureInfo.InvariantCulture) + "-8421-01.dmp",
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
        var wheaDump = new DumpFile(@"C:\Windows\Minidump\" + wheaTime.ToString("MMddyy", CultureInfo.InvariantCulture) + "-7031-01.dmp",
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
                Power = new KernelPowerDetails(0x116, 0, 0, false, false),
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
                LogName = "System", EventId = 41, Power = new KernelPowerDetails(0, 0, 0, false, false),
            },
            new()
            {
                Id = "demo-6008", Kind = CrashKind.SuddenShutdown, Time = At(20, 23, 6), Source = "System log, event 6008 (EventLog)",
                LogName = "System", EventId = 6008,
            },
            Reset("demo-tdr-1", At(3, 21, 9), "nvlddmkm"),
            Reset("demo-tdr-2", At(9, 22, 31), "nvlddmkm"),
            Reset("demo-tdr-3", At(25, 20, 3), "nvlddmkm"),
            App("demo-app-1", At(1, 22, 47), "Cyberpunk2077.exe", "3.0.78.62467", "DiscordHook64.dll", "1.0.9173.0", 0xC0000005, "0x0000000000021f3a",
                @"C:\Users\Alex\AppData\Local\Discord\app-1.0.9173\modules\discord_hook-1\discord_hook\DiscordHook64.dll"),
            App("demo-app-2", At(4, 20, 12), "Cyberpunk2077.exe", "3.0.78.62467", "nvwgf2umx.dll", DemoDriverVersion, 0xC0000005, "0x0000000000e4a1b7"),
            App("demo-app-3", At(10, 21, 55), "Cyberpunk2077.exe", "3.0.77.12210", "Cyberpunk2077.exe", "3.0.77.12210", 0xC0000409, "0x0000000004d1c2f0"),
            Hang("demo-hang-1", At(6, 19, 20), "Cyberpunk2077.exe", "3.0.78.62467"),
            App("demo-app-4", At(2, 18, 33), "eldenring.exe", "2.6.1.0", "eldenring.exe", "2.6.1.0", 0xC0000005, "0x0000000001a3f9c4"),
            App("demo-app-5", At(15, 23, 48), "eldenring.exe", "2.6.1.0", "ntdll.dll", "10.0.26100.2454", 0xC0000374, "0x0000000000103e49"),
            App("demo-app-6", At(5, 9, 12), "SaveSync.exe", "2.4.1.0", "KERNELBASE.dll", "10.0.26100.2454", 0xE0434352, "0x00000000000c5f7c"),
        };

        var watchdog = new DumpFile(@"C:\Windows\LiveKernelReports\WATCHDOG\WATCHDOG-" + At(9, 22, 31).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".dmp",
            At(9, 22, 31), 412_160_000, new CrashDumpInfo { Format = DumpFormat.Kernel64, KindText = "Kernel memory dump", BugCheckCode = 0x141, OsBuild = 26100 }, IsGraphicsWatchdog: true);

        var system = SystemEvents(now, wheaTime, tdrBsodTime);
        return new CrashReport
        {
            Captured = now,
            Events = CrashSystemEventRules.AttachWhea(CrashRules.Merge(events, [tdrDump, wheaDump, watchdog], now), system),
            WheaErrors = 6,
            OverclockApplied = true,
            SystemEvents = system,
        };
    }

    /// <summary>WHEA, disk, start and driver-install events around the sample crashes.</summary>
    private static IReadOnlyList<CrashSystemEvent> SystemEvents(DateTimeOffset now, DateTimeOffset wheaTime, DateTimeOffset tdrTime)
    {
        static WheaErrorInfo Corrected(DateTimeOffset t) => new()
        {
            Source = WheaErrorSource.CorrectedMachineCheck, Components = ["Processor"], ApicId = 6, Bank = 5, MciStatus = 0x9C20000000010135, Time = t,
        };

        var list = new List<CrashSystemEvent>
        {
            // The NVIDIA driver 566.36 was installed 27 days ago; no driver resets or blue screens before that.
            new(At(now, 27, 18, 40), CrashSystemEventKind.DriverInstalled, @"PCI\VEN_10DE&DEV_2786&SUBSYS_88AB1043 (nv_dispi.inf 32.0.15.6636)"),
            new(At(now, 3, 18, 2), CrashSystemEventKind.Boot) { EventId = 12 },
            new(At(now, 9, 20, 30), CrashSystemEventKind.Boot) { EventId = 12 },
            new(At(now, 12, 17, 55), CrashSystemEventKind.Boot) { EventId = 12 },
            new(At(now, 20, 20, 10), CrashSystemEventKind.Boot) { EventId = 12 },
            new(At(now, 25, 19, 0), CrashSystemEventKind.Boot) { EventId = 12 },
            new(wheaTime.AddMinutes(-41), CrashSystemEventKind.WheaCorrected) { Whea = Corrected(wheaTime.AddMinutes(-41)), EventId = 19 },
            new(wheaTime.AddMinutes(-18), CrashSystemEventKind.WheaCorrected) { Whea = Corrected(wheaTime.AddMinutes(-18)), EventId = 19 },
            new(wheaTime.AddMinutes(-6), CrashSystemEventKind.WheaCorrected) { Whea = Corrected(wheaTime.AddMinutes(-6)), EventId = 19 },
            new(wheaTime.AddMinutes(4), CrashSystemEventKind.WheaFatal)
            {
                EventId = 18,
                Whea = new WheaErrorInfo
                {
                    Source = WheaErrorSource.MachineCheck, Fatal = true, Components = ["Processor"], ApicId = 6, Bank = 5,
                    MciStatus = 0xBE00000000800400, ProcessorErrorType = "Micro-architecture error", Time = wheaTime.AddMinutes(4),
                },
            },
            new(At(now, 20, 9, 14), CrashSystemEventKind.DiskError, @"\Device\Harddisk1\DR1") { EventId = 153 },
            new(tdrTime.AddHours(-30), CrashSystemEventKind.Wake) { EventId = 107 },
        };
        return list.OrderBy(e => e.Time).ToList();
    }

    /// <summary>History samples, journal and update history around the sample crashes.</summary>
    public static CrashContextData Context(DateTimeOffset now, IReadOnlyList<CrashEvent>? crashes = null)
    {
        var events = crashes is { Count: > 0 } ? crashes : Build(now).Events;
        var samples = new List<HistorySample>();
        void Window(DateTimeOffset crash, Func<int, Dictionary<string, double>> values)
        {
            for (int i = 10; i >= 0; i--)
            {
                samples.Add(new HistorySample(crash.AddMinutes(-i), 60, values(i)));
            }
        }

        foreach (var crash in events.Where(CrashPatternRules.IsSerious))
        {
            if (crash.BugCheckCode == 0x124)
            {
                // A gaming session with a fresh CPU overclock: the CPU climbs to 96 °C.
                Window(crash.Time, i => new()
                {
                    [HistoryMetrics.CpuTemp] = 96 - (i * 1.4),
                    [HistoryMetrics.GpuTemp] = 71 - (i * 0.3),
                    [HistoryMetrics.CpuLoad] = 97 - (i % 3),
                    [HistoryMetrics.GpuLoad] = 62 + (i % 4),
                    [HistoryMetrics.Power] = 468 - (i * 2),
                });
            }
            else if (crash.Kind is CrashKind.BlueScreen or CrashKind.DriverReset)
            {
                // Games: the graphics card at full load, temperatures fine.
                Window(crash.Time, i => new()
                {
                    [HistoryMetrics.CpuTemp] = 68 + (i % 3),
                    [HistoryMetrics.GpuTemp] = 76 - (i % 2),
                    [HistoryMetrics.CpuLoad] = 41 + (i % 5),
                    [HistoryMetrics.GpuLoad] = 98 - (i % 2),
                    [HistoryMetrics.Power] = 512 - i,
                });
            }
        }

        var wheaTime = events.FirstOrDefault(e => e.BugCheckCode == 0x124)?.Time ?? At(now, 12, 19, 42);
        var journal = new List<ActionRecord>
        {
            new(Guid.Parse("0d6f8a2e-5b1c-4e7a-9a3d-2f1e6c8b7a01"), wheaTime.AddMinutes(-20), "Overclock", "Apply profile", "CPU: PBO +200 MHz, Curve Optimizer -30 (all cores)",
                "Stock", "Gaming OC", true, "Reset to defaults on the Overclock page", "Applied", null),
        };
        var updates = new List<UpdateInstallRecord>
        {
            new(At(now, 27, 18, 38), "NVIDIA - Display - 32.0.15.6636", IsDriver: true),
            new(At(now, 14, 3, 12), "2026-10 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5066835)", IsDriver: false),
        };
        return new CrashContextData(samples, journal, updates);
    }

    private static CrashEvent Reset(string id, DateTimeOffset time, string driver) => new()
    {
        Id = id, Kind = CrashKind.DriverReset, Time = time, Source = "System log, event 4101 (Display)", LogName = "System", EventId = 4101, Driver = driver,
    };

    private static CrashEvent App(string id, DateTimeOffset time, string app, string version, string module, string moduleVersion, uint code, string offset,
        string? modulePath = null) => new()
    {
        Id = id, Kind = CrashKind.AppCrash, Time = time, Source = "Application log, event 1000 (Application Error)", LogName = "Application", EventId = 1000,
        AppName = app, AppVersion = version, ModuleName = module, ModuleVersion = moduleVersion, ExceptionCode = code, FaultOffset = offset, ModulePath = modulePath,
        AppPath = app switch
        {
            "Cyberpunk2077.exe" => @"C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe",
            "eldenring.exe" => @"C:\Program Files (x86)\Steam\steamapps\common\ELDEN RING\Game\eldenring.exe",
            _ => @"C:\Program Files\SaveSync\SaveSync.exe",
        },
    };

    private static CrashEvent Hang(string id, DateTimeOffset time, string app, string version) => new()
    {
        Id = id, Kind = CrashKind.AppHang, Time = time, Source = "Application log, event 1002 (Application Hang)", LogName = "Application", EventId = 1002,
        AppName = app, AppVersion = version, AppPath = @"C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe",
    };
}
