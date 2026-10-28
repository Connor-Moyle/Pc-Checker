using System.Globalization;

using static PcChecker.Core.Crashes.CrashText;

namespace PcChecker.Core.Crashes;

/// <summary>
/// Turns parsed event XML (<see cref="EventXmlRecord"/>) into <see cref="CrashEvent"/>s. Only event ids, provider
/// names and EventData values are used, never the message text, so it works the same on every Windows language.
/// </summary>
public static class CrashEventMapper
{
    public const string BugCheckProvider = "Microsoft-Windows-WER-SystemErrorReporting";
    public const string KernelPowerProvider = "Microsoft-Windows-Kernel-Power";
    public const string EventLogProvider = "EventLog";
    public const string DisplayProvider = "Display";
    public const string AppErrorProvider = "Application Error";
    public const string AppHangProvider = "Application Hang";

    /// <summary>The XPath filters the Windows service runs, one per log; <paramref name="days"/> back from now.</summary>
    public static string SystemQuery(int days) =>
        $"*[System[(" +
        $"(Provider[@Name='{BugCheckProvider}'] and EventID=1001) or " +
        $"(Provider[@Name='{KernelPowerProvider}'] and EventID=41) or " +
        $"(Provider[@Name='{EventLogProvider}'] and EventID=6008) or " +
        $"(Provider[@Name='{DisplayProvider}'] and EventID=4101)" +
        $") and TimeCreated[timediff(@SystemTime) <= {WindowMs(days)}]]]";

    public static string ApplicationQuery(int days) =>
        $"*[System[(" +
        $"(Provider[@Name='{AppErrorProvider}'] and EventID=1000) or " +
        $"(Provider[@Name='{AppHangProvider}'] and EventID=1002)" +
        $") and TimeCreated[timediff(@SystemTime) <= {WindowMs(days)}]]]";

    private static long WindowMs(int days) => Math.Max(1, days) * 86_400_000L;

    /// <summary>The crash this event describes, or null for an event this page does not show.</summary>
    public static CrashEvent? Map(EventXmlRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return (record.Provider, record.EventId) switch
        {
            (BugCheckProvider, 1001) => FromBugCheck(record),
            (KernelPowerProvider, 41) => FromKernelPower(record),
            (EventLogProvider, 6008) => FromUnexpectedShutdown(record),
            (DisplayProvider, 4101) => FromDisplayReset(record),
            (AppErrorProvider, 1000) => FromAppError(record),
            (AppHangProvider, 1002) => FromAppHang(record),
            _ => null,
        };
    }

    /// <summary>System 1001 (BugCheck): param1 = "0x… (p1, p2, p3, p4)", param2 = dump path, param3 = report id.</summary>
    public static CrashEvent? FromBugCheck(EventXmlRecord r)
    {
        if (BugCheckFormat.ParseBugCheckLine(r.Get("param1") ?? r.At(0)) is not { } parsed)
        {
            return null;
        }

        var dump = r.Get("param2") ?? r.At(1);
        return new CrashEvent
        {
            Id = MakeId("bsod", r),
            Kind = CrashKind.BlueScreen,
            Time = r.Time,
            Source = T("System log, event 1001 (BugCheck)"),
            LogName = "System",
            EventId = 1001,
            BugCheckCode = parsed.Code,
            Parameters = parsed.Parameters,
            DumpPath = string.IsNullOrWhiteSpace(dump) ? null : dump,
            ReportId = r.Get("param3") ?? r.At(2),
        };
    }

    /// <summary>
    /// System 41 (Kernel-Power): BugcheckCode is decimal; non-zero means it was a blue screen (merged with the BugCheck
    /// event when there is one), zero means the PC lost power or stopped responding.
    /// </summary>
    public static CrashEvent FromKernelPower(EventXmlRecord r)
    {
        ulong code = BugCheckFormat.ParseNumber(r.Get("BugcheckCode"), decimalWithoutPrefix: true) ?? 0;
        var parameters = new List<ulong>();
        if (code != 0)
        {
            for (int i = 1; i <= 4; i++)
            {
                parameters.Add(BugCheckFormat.ParseNumber(r.Get($"BugcheckParameter{i}")) ?? 0);
            }
        }

        bool held = BugCheckFormat.ParseNumber(r.Get("PowerButtonTimestamp"), decimalWithoutPrefix: true) is > 0
            || string.Equals(r.Get("LongPowerButtonPressDetected"), "true", StringComparison.OrdinalIgnoreCase);
        var details = KernelPowerRules.Parse(r);
        return new CrashEvent
        {
            Id = MakeId("kp41", r),
            Kind = code != 0 ? CrashKind.BlueScreen : CrashKind.SuddenShutdown,
            Time = r.Time,
            Source = T("System log, event 41 (Kernel-Power)"),
            LogName = "System",
            EventId = 41,
            BugCheckCode = code != 0 ? (uint)(code & 0xFFFFFFFF) : null,
            Parameters = parameters,
            PowerButtonHeld = held,
            Power = details,
        };
    }

    /// <summary>System 6008 (EventLog): "the previous shutdown was unexpected". Its date text is localized, so only the event time is used.</summary>
    public static CrashEvent FromUnexpectedShutdown(EventXmlRecord r) => new()
    {
        Id = MakeId("ev6008", r),
        Kind = CrashKind.SuddenShutdown,
        Time = r.Time,
        Source = T("System log, event 6008 (EventLog)"),
        LogName = "System",
        EventId = 6008,
    };

    /// <summary>System 4101 (Display): the first value is the driver that was reset (nvlddmkm, amdkmdag, igfx…).</summary>
    public static CrashEvent FromDisplayReset(EventXmlRecord r)
    {
        var driver = r.At(0) ?? r.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
        return new CrashEvent
        {
            Id = MakeId("tdr", r),
            Kind = CrashKind.DriverReset,
            Time = r.Time,
            Source = T("System log, event 4101 (Display)"),
            LogName = "System",
            EventId = 4101,
            Driver = driver,
        };
    }

    /// <summary>
    /// Application 1000 (Application Error). Newer Windows names the fields (AppName, ExceptionCode…); older Windows only
    /// orders them: app, version, timestamp, module, module version, module timestamp, exception code, offset, pid,
    /// start time, app path, module path, report id.
    /// </summary>
    public static CrashEvent? FromAppError(EventXmlRecord r)
    {
        var app = r.Field("AppName", 0);
        if (string.IsNullOrWhiteSpace(app))
        {
            return null;
        }

        return new CrashEvent
        {
            Id = MakeId("app", r),
            Kind = CrashKind.AppCrash,
            Time = r.Time,
            Source = T("Application log, event 1000 (Application Error)"),
            LogName = "Application",
            EventId = 1000,
            AppName = app,
            AppVersion = r.Field("AppVersion", 1),
            ModuleName = r.Field("ModuleName", 3),
            ModuleVersion = r.Field("ModuleVersion", 4),
            ExceptionCode = BugCheckFormat.ParseNumber(r.Field("ExceptionCode", 6)) is { } ex ? (uint)(ex & 0xFFFFFFFF) : null,
            FaultOffset = r.Field("FaultingOffset", 7),
            AppPath = r.Field("AppPath", 10),
            ModulePath = r.Field("ModulePath", 11),
            ReportId = r.Field("IntegratorReportId", 12) ?? r.Get("ReportId"),
        };
    }

    /// <summary>Application 1002 (Application Hang): app, version, pid, start time, hang time, app path, report id.</summary>
    public static CrashEvent? FromAppHang(EventXmlRecord r)
    {
        var app = r.Field("AppName", 0);
        if (string.IsNullOrWhiteSpace(app))
        {
            return null;
        }

        return new CrashEvent
        {
            Id = MakeId("hang", r),
            Kind = CrashKind.AppHang,
            Time = r.Time,
            Source = T("Application log, event 1002 (Application Hang)"),
            LogName = "Application",
            EventId = 1002,
            AppName = app,
            AppVersion = r.Field("AppVersion", 1),
            AppPath = r.Field("AppPath", 5),
            ReportId = r.Field("ReportID", 6),
        };
    }

    public const string WheaProvider = "Microsoft-Windows-WHEA-Logger";
    public const string KernelGeneralProvider = "Microsoft-Windows-Kernel-General";
    public const string PowerTroubleshooterProvider = "Microsoft-Windows-Power-Troubleshooter";
    public const string ServiceControlManagerProvider = "Service Control Manager";

    private static readonly string[] DiskProviders = ["disk", "Ntfs", "Microsoft-Windows-Ntfs", "storahci", "stornvme", "storport"];

    /// <summary>
    /// The System-log filter for the events around crashes: WHEA errors, disk and file-system errors, sleep and wake,
    /// Windows starts and new kernel driver services, <paramref name="days"/> back from now.
    /// </summary>
    /// <summary>
    /// The context events except corrected WHEA errors (fatal WHEA, disk, wake, boot, driver installs). Corrected WHEA
    /// errors can number thousands on an unstable PC, so they are read by <see cref="CorrectedWheaQuery"/> with their own
    /// limit and cannot crowd the rest out.
    /// </summary>
    public static string ContextQuery(int days) =>
        $"*[System[(" +
        $"(Provider[@Name='{WheaProvider}'] and (EventID=18 or EventID=20)) or " +
        $"(Provider[@Name='disk'] and (EventID=7 or EventID=11 or EventID=51 or EventID=153)) or " +
        $"(Provider[@Name='Ntfs' or @Name='Microsoft-Windows-Ntfs'] and EventID=55) or " +
        $"(Provider[@Name='{KernelPowerProvider}'] and (EventID=42 or EventID=107)) or " +
        $"(Provider[@Name='{PowerTroubleshooterProvider}'] and EventID=1) or " +
        $"(Provider[@Name='{KernelGeneralProvider}'] and EventID=12) or " +
        $"(Provider[@Name='{ServiceControlManagerProvider}'] and EventID=7045)" +
        $") and TimeCreated[timediff(@SystemTime) <= {WindowMs(days)}]]]";

    /// <summary>Corrected WHEA errors (17 PCI Express, 19 processor, 47 memory), read separately from <see cref="ContextQuery"/>.</summary>
    public static string CorrectedWheaQuery(int days) =>
        $"*[System[Provider[@Name='{WheaProvider}'] and (EventID=17 or EventID=19 or EventID=47) and TimeCreated[timediff(@SystemTime) <= {WindowMs(days)}]]]";

    /// <summary>The context event this record is, or null for anything else (including 7045 for a non-driver service).</summary>
    public static CrashSystemEvent? MapSystemEvent(EventXmlRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Provider == WheaProvider)
        {
            var whea = WheaRecord.FromEvent(r);
            if (whea is null)
            {
                return null;
            }

            return new CrashSystemEvent(r.Time, whea.Fatal ? CrashSystemEventKind.WheaFatal : CrashSystemEventKind.WheaCorrected) { Whea = whea, EventId = r.EventId };
        }

        if (DiskProviders.Contains(r.Provider, StringComparer.OrdinalIgnoreCase) && r.EventId is 7 or 11 or 51 or 55 or 153)
        {
            // The first value is the device ("\Device\Harddisk1\DR1") or the volume for Ntfs; never the message.
            var device = r.Get("DeviceName", "DriveName") ?? r.At(0);
            return new CrashSystemEvent(r.Time, CrashSystemEventKind.DiskError, device) { EventId = r.EventId };
        }

        return (r.Provider, r.EventId) switch
        {
            (KernelPowerProvider, 42) => new CrashSystemEvent(r.Time, CrashSystemEventKind.Sleep) { EventId = 42 },
            (KernelPowerProvider, 107) => new CrashSystemEvent(r.Time, CrashSystemEventKind.Wake) { EventId = 107 },
            (PowerTroubleshooterProvider, 1) => new CrashSystemEvent(r.Time, CrashSystemEventKind.Wake) { EventId = 1 },
            (KernelGeneralProvider, 12) => new CrashSystemEvent(r.Time, CrashSystemEventKind.Boot) { EventId = 12 },
            (ServiceControlManagerProvider, 7045) => FromServiceInstall(r),
            _ => null,
        };
    }

    /// <summary>System 7045: a new service; kept only when its file is a driver (.sys). Detail = the file name.</summary>
    private static CrashSystemEvent? FromServiceInstall(EventXmlRecord r)
    {
        var image = r.Get("ImagePath") ?? r.At(1);
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        var file = image.Trim().Trim('"');
        int slash = file.LastIndexOfAny(['\\', '/']);
        file = slash >= 0 ? file[(slash + 1)..] : file;
        int space = file.IndexOf(' ');
        if (space > 0)
        {
            file = file[..space];
        }

        return file.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
            ? new CrashSystemEvent(r.Time, CrashSystemEventKind.DriverInstalled, file) { EventId = 7045 }
            : null;
    }

    private static string MakeId(string prefix, EventXmlRecord r) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}-{r.Time.UtcTicks}-{r.RecordId ?? 0}");
}
