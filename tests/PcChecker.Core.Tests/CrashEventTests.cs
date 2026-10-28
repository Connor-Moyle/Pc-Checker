using PcChecker.Core.Crashes;
using PcChecker.Core.Crashes.Dumps;
using PcChecker.Core.Settings;
using PcChecker.Core.Shell;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Event XML parsing (any Windows language), stop code formatting, catalogs, merging, grouping and advice.</summary>
public class CrashEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static string Event(string provider, int id, string time, string data, string rendering = "", string extraProvider = "") => $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="{provider}" {extraProvider}/>
            <EventID>{id}</EventID>
            <Level>2</Level>
            <TimeCreated SystemTime="{time}"/>
            <EventRecordID>4711</EventRecordID>
            <Channel>System</Channel>
            <Computer>GAMING-DESKTOP</Computer>
          </System>
          {data}
          {rendering}
        </Event>
        """;

    private const string BugCheckData = """
        <EventData>
          <Data Name="param1">0x00000116 (0xffffb30a4c7e8010, 0xfffff8061c9d3a40, 0xffffffffc000009a, 0x0000000000000004)</Data>
          <Data Name="param2">C:\Windows\Minidump\102526-8421-01.dmp</Data>
          <Data Name="param3">a3c1f0e2-6b7d-4d58-9a41-5c2e8f7b9d10</Data>
        </EventData>
        """;

    private const string EnglishRendering = """
        <RenderingInfo Culture="en-US"><Message>The computer has rebooted from a bugcheck. The bugcheck was: 0x00000050 (1, 2, 3, 4).</Message></RenderingInfo>
        """;

    private const string GermanRendering = """
        <RenderingInfo Culture="de-DE"><Message>Der Computer wurde nach einem schwerwiegenden Fehler neu gestartet. Der Fehlercode war: 0x00000050 (1, 2, 3, 4).</Message></RenderingInfo>
        """;

    private static EventXmlRecord Parse(string xml) => Assert.IsType<EventXmlRecord>(EventXmlRecord.Parse(xml));

    // ------------------------------------------------------------------ event XML

    [Theory]
    [InlineData(EnglishRendering)]
    [InlineData(GermanRendering)]
    public void A_BugCheck_event_is_read_from_its_data_never_from_the_message(string rendering)
    {
        var xml = Event(CrashEventMapper.BugCheckProvider, 1001, "2026-10-25T20:16:03.5000000Z", BugCheckData, rendering, "EventSourceName=\"BugCheck\"");

        var crash = CrashEventMapper.Map(Parse(xml))!;

        Assert.Equal(CrashKind.BlueScreen, crash.Kind);
        Assert.Equal(0x116u, crash.BugCheckCode); // The message says 0x50; the data says 0x116.
        Assert.Equal([0xFFFFB30A4C7E8010, 0xFFFFF8061C9D3A40, 0xFFFFFFFFC000009A, 4UL], crash.Parameters);
        Assert.Equal(@"C:\Windows\Minidump\102526-8421-01.dmp", crash.DumpPath);
        Assert.Equal("a3c1f0e2-6b7d-4d58-9a41-5c2e8f7b9d10", crash.ReportId);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 20, 16, 3, 500, TimeSpan.Zero), crash.Time.ToUniversalTime());
    }

    [Fact]
    public void English_and_German_events_parse_to_the_same_crash()
    {
        string data = """
            <EventData>
              <Data Name="AppName">eldenring.exe</Data><Data Name="AppVersion">2.6.1.0</Data><Data Name="AppTimeStamp">67a1b2c3</Data>
              <Data Name="ModuleName">ntdll.dll</Data><Data Name="ModuleVersion">10.0.26100.2454</Data><Data Name="ModuleTimeStamp">5a3b2c1d</Data>
              <Data Name="ExceptionCode">c0000374</Data><Data Name="FaultingOffset">0000000000103e49</Data><Data Name="ProcessId">0x2f10</Data>
              <Data Name="ProcessCreationTime">0x1db27a1c2e3f4a5</Data><Data Name="AppPath">D:\Games\ELDEN RING\Game\eldenring.exe</Data>
              <Data Name="ModulePath">C:\Windows\SYSTEM32\ntdll.dll</Data><Data Name="IntegratorReportId">0d1e2f3a-4b5c-6d7e-8f90-a1b2c3d4e5f6</Data>
              <Data Name="PackageFullName"></Data><Data Name="PackageRelativeAppId"></Data>
            </EventData>
            """;
        var english = CrashEventMapper.Map(Parse(Event(CrashEventMapper.AppErrorProvider, 1000, "2026-10-13T21:48:00Z", data,
            "<RenderingInfo Culture=\"en-US\"><Message>Faulting application name: eldenring.exe</Message></RenderingInfo>")))!;
        var german = CrashEventMapper.Map(Parse(Event(CrashEventMapper.AppErrorProvider, 1000, "2026-10-13T21:48:00Z", data,
            "<RenderingInfo Culture=\"de-DE\"><Message>Name der fehlerhaften Anwendung: eldenring.exe, Version: 2.6.1.0</Message></RenderingInfo>")))!;

        Assert.Equal(
            (english.Kind, english.Time, english.AppName, english.AppVersion, english.ModuleName, english.ExceptionCode, english.FaultOffset, english.AppPath, english.ReportId),
            (german.Kind, german.Time, german.AppName, german.AppVersion, german.ModuleName, german.ExceptionCode, german.FaultOffset, german.AppPath, german.ReportId));
        Assert.Equal(CrashKind.AppCrash, english.Kind);
        Assert.Equal("eldenring.exe", english.AppName);
        Assert.Equal("ntdll.dll", english.ModuleName);
        Assert.Equal(0xC0000374u, english.ExceptionCode);
        Assert.Equal(@"D:\Games\ELDEN RING\Game\eldenring.exe", english.AppPath);
        Assert.Equal("0d1e2f3a-4b5c-6d7e-8f90-a1b2c3d4e5f6", english.ReportId);
    }

    [Fact]
    public void Older_unnamed_event_data_is_read_by_position()
    {
        string data = """
            <EventData>
              <Data>Cyberpunk2077.exe</Data><Data>3.0.78.62467</Data><Data>65f1a2b3</Data><Data>nvwgf2umx.dll</Data><Data>32.0.15.6614</Data>
              <Data>66aabbcc</Data><Data>c0000005</Data><Data>0000000000e4a1b7</Data><Data>3a8c</Data><Data>01db27a1c2e3f4a5</Data>
              <Data>D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe</Data><Data>C:\Windows\System32\nvwgf2umx.dll</Data>
              <Data>11111111-2222-3333-4444-555555555555</Data>
            </EventData>
            """;
        var crash = CrashEventMapper.Map(Parse(Event(CrashEventMapper.AppErrorProvider, 1000, "2026-10-27T20:47:00Z", data)))!;
        Assert.Equal("Cyberpunk2077.exe", crash.AppName);
        Assert.Equal("nvwgf2umx.dll", crash.ModuleName);
        Assert.Equal(0xC0000005u, crash.ExceptionCode);
        Assert.Equal("0000000000e4a1b7", crash.FaultOffset);
        Assert.Equal("11111111-2222-3333-4444-555555555555", crash.ReportId);

        var hang = CrashEventMapper.Map(Parse(Event(CrashEventMapper.AppHangProvider, 1002, "2026-10-22T19:20:00Z",
            "<EventData><Data>Cyberpunk2077.exe</Data><Data>3.0.78.62467</Data><Data>3a8c</Data><Data>01db27a1c2e3f4a5</Data><Data>4294967295</Data><Data>D:\\Games\\Cyberpunk2077.exe</Data><Data>rep-1</Data></EventData>")))!;
        Assert.Equal(CrashKind.AppHang, hang.Kind);
        Assert.Equal("Cyberpunk2077.exe", hang.AppName);
        Assert.Equal(@"D:\Games\Cyberpunk2077.exe", hang.AppPath);
    }

    [Fact]
    public void Kernel_Power_41_tells_blue_screens_from_power_loss()
    {
        string Data(string code, string button = "0") => $"""
            <EventData>
              <Data Name="BugcheckCode">{code}</Data>
              <Data Name="BugcheckParameter1">0xffffb30a4c7e8010</Data><Data Name="BugcheckParameter2">0xfffff8061c9d3a40</Data>
              <Data Name="BugcheckParameter3">0xffffffffc000009a</Data><Data Name="BugcheckParameter4">0x4</Data>
              <Data Name="SleepInProgress">0</Data><Data Name="PowerButtonTimestamp">{button}</Data>
            </EventData>
            """;
        var bsod = CrashEventMapper.Map(Parse(Event(CrashEventMapper.KernelPowerProvider, 41, "2026-10-25T20:15:00Z", Data("278"))))!;
        Assert.Equal(CrashKind.BlueScreen, bsod.Kind);
        Assert.Equal(0x116u, bsod.BugCheckCode); // 278 decimal
        Assert.Equal(4UL, bsod.Parameters[3]);

        var power = CrashEventMapper.Map(Parse(Event(CrashEventMapper.KernelPowerProvider, 41, "2026-10-08T23:05:00Z", Data("0"))))!;
        Assert.Equal(CrashKind.SuddenShutdown, power.Kind);
        Assert.Null(power.BugCheckCode);
        Assert.False(power.PowerButtonHeld);

        var held = CrashEventMapper.Map(Parse(Event(CrashEventMapper.KernelPowerProvider, 41, "2026-10-08T23:05:00Z", Data("0", "133420000000000000"))))!;
        Assert.True(held.PowerButtonHeld);
    }

    [Fact]
    public void Shutdown_6008_and_display_4101_are_read_without_their_text()
    {
        var unexpected = CrashEventMapper.Map(Parse(Event(CrashEventMapper.EventLogProvider, 6008, "2026-10-08T23:06:00Z",
            "<EventData><Data>23:01:12</Data><Data>‎08.‎10.‎2026</Data><Data></Data><Data></Data></EventData>")))!;
        Assert.Equal(CrashKind.SuddenShutdown, unexpected.Kind);
        Assert.Equal(6008, unexpected.EventId);

        var reset = CrashEventMapper.Map(Parse(Event(CrashEventMapper.DisplayProvider, 4101, "2026-10-19T22:31:00Z",
            "<EventData><Data>nvlddmkm</Data><Data></Data></EventData>")))!;
        Assert.Equal(CrashKind.DriverReset, reset.Kind);
        Assert.Equal("nvlddmkm", reset.Driver);
        Assert.Equal("Graphics driver reset (NVIDIA)", CrashAdvice.Title(reset));
    }

    [Fact]
    public void Malformed_or_unrelated_events_are_skipped()
    {
        Assert.Null(EventXmlRecord.Parse(null));
        Assert.Null(EventXmlRecord.Parse("<Event><System>"));
        Assert.Null(EventXmlRecord.Parse("<Event><NotSystem/></Event>"));
        Assert.Null(CrashEventMapper.Map(Parse(Event("Service Control Manager", 7036, "2026-10-08T23:06:00Z", "<EventData/>"))));
        Assert.Null(CrashEventMapper.Map(Parse(Event(CrashEventMapper.BugCheckProvider, 1001, "2026-10-08T23:06:00Z",
            "<EventData><Data Name=\"param1\">not a code</Data></EventData>"))));
        Assert.Null(CrashEventMapper.Map(Parse(Event(CrashEventMapper.AppErrorProvider, 1000, "2026-10-08T23:06:00Z", "<EventData/>"))));
    }

    [Fact]
    public void The_queries_filter_by_provider_id_and_time()
    {
        var system = CrashEventMapper.SystemQuery(90);
        Assert.Contains("EventID=1001", system);
        Assert.Contains("Microsoft-Windows-Kernel-Power", system);
        Assert.Contains("EventID=4101", system);
        Assert.Contains("timediff(@SystemTime) <= 7776000000", system);
        Assert.Contains("Application Hang", CrashEventMapper.ApplicationQuery(7));
    }

    // ------------------------------------------------------------------ formatting

    [Fact]
    public void Stop_codes_and_parameters_are_formatted_like_Windows()
    {
        Assert.Equal("0x00000116", BugCheckFormat.Code(0x116));
        Assert.Equal("0xC000021A", BugCheckFormat.Code(0xC000021A));
        Assert.Equal("0xFFFFF8061C9D3A40", BugCheckFormat.Parameter(0xFFFFF8061C9D3A40));
        Assert.Equal("0x0000000000000004", BugCheckFormat.Parameter(4));
        Assert.Equal("0xc0000005", BugCheckFormat.Exception(0xC0000005));
        Assert.Equal("0x0000000A (0x0000000000000010, 0x0000000000000002)", BugCheckFormat.WithParameters(0xA, [0x10, 2]));
        Assert.Equal("0x0000001A", BugCheckFormat.WithParameters(0x1A, []));
    }

    [Theory]
    [InlineData("0x0000001a (0x0000000000041790, 0xffffe50001234560, 0x0000000000000001, 0x0000000000000000)", 0x1Au, 4)]
    [InlineData("0x00000124", 0x124u, 0)]
    [InlineData("  0x0000009f (0x3, 0xffffc0012345a060)", 0x9Fu, 2)]
    [InlineData("0x1000007e (0xffffffffc0000005, 0, 0, 0)", 0x1000007Eu, 4)]
    [InlineData("0x0000000a (0x10, oops, 0x1)", 0x0Au, 1)]
    public void BugCheck_lines_parse(string text, uint code, int parameterCount)
    {
        var parsed = BugCheckFormat.ParseBugCheckLine(text)!.Value;
        Assert.Equal(code, parsed.Code);
        Assert.Equal(parameterCount, parsed.Parameters.Count);
    }

    [Fact]
    public void Numbers_parse_in_hex_or_decimal_and_reject_junk()
    {
        Assert.Null(BugCheckFormat.ParseBugCheckLine("278"));
        Assert.Null(BugCheckFormat.ParseBugCheckLine(""));
        Assert.Equal(278UL, BugCheckFormat.ParseNumber("278", decimalWithoutPrefix: true));
        Assert.Equal(0x278UL, BugCheckFormat.ParseNumber("278"));
        Assert.Equal(0xC0000005UL, BugCheckFormat.ParseNumber("0xC0000005"));
        Assert.Null(BugCheckFormat.ParseNumber("zz"));
        Assert.Null(BugCheckFormat.ParseNumber("-1", decimalWithoutPrefix: true));
    }

    // ------------------------------------------------------------------ catalogs

    [Fact]
    public void The_stop_code_catalog_is_complete_and_unique()
    {
        var codes = StopCodeCatalog.All.Select(s => s.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal(codes.Count, StopCodeCatalog.All.Select(s => s.Name).Distinct().Count());
        Assert.True(codes.Count >= 40);
        uint[] required = [0x0A, 0x3B, 0x50, 0x1E, 0x133, 0x101, 0x124, 0x116, 0x119, 0x1A, 0xEF, 0xD1, 0x9F, 0x139, 0x7E, 0x24, 0x7B, 0x7A, 0x7F, 0x19];
        Assert.All(required, c => Assert.NotNull(StopCodeCatalog.Find(c)));
        Assert.All(StopCodeCatalog.All, s =>
        {
            Assert.NotEmpty(s.FirstSteps);
            Assert.False(string.IsNullOrWhiteSpace(s.Meaning));
            Assert.Matches("^[A-Z0-9_]+$", s.Name);
            Assert.StartsWith("https://learn.microsoft.com/windows-hardware/drivers/debugger/", s.DocsUrl);
            if (s.DocsSlug is { } slug)
            {
                Assert.StartsWith("bug-check-0x" + s.Code.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "-", slug);
            }
        });
    }

    [Fact]
    public void Stop_code_lookups_handle_variants_and_unknown_codes()
    {
        Assert.Equal("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", StopCodeCatalog.Find(0x1000007E)?.Name);
        Assert.Equal("Stop code 0x000001E0", StopCodeCatalog.NameOf(0x1E0));
        Assert.Equal(StopCodeCatalog.ReferenceIndexUrl, StopCodeCatalog.DocsUrlFor(0x1E0));
        Assert.EndsWith("bug-check-0x116---video-tdr-failure", StopCodeCatalog.DocsUrlFor(0x116));
    }

    [Fact]
    public void The_exception_catalog_explains_the_common_codes()
    {
        var codes = ExceptionCodeCatalog.All.Select(e => e.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        uint[] required = [0xC0000005, 0xC0000409, 0xC0000374, 0x80000003, 0xE0434352, 0xC000001D];
        Assert.All(required, c => Assert.NotNull(ExceptionCodeCatalog.Find(c)));
        Assert.Equal("Access violation (0xc0000005)", ExceptionCodeCatalog.Describe(0xC0000005));
        Assert.Equal("0x12345678", ExceptionCodeCatalog.Describe(0x12345678));
    }

    // ------------------------------------------------------------------ merging and grouping

    private static CrashEvent Bsod(string id, DateTimeOffset time, uint code, string? dump = null, int eventId = 1001) => new()
    {
        Id = id, Kind = CrashKind.BlueScreen, Time = time, LogName = "System", EventId = eventId, BugCheckCode = code, DumpPath = dump,
    };

    private static CrashEvent Shutdown(string id, DateTimeOffset time, int eventId) => new()
    {
        Id = id, Kind = CrashKind.SuddenShutdown, Time = time, LogName = "System", EventId = eventId,
    };

    private static CrashEvent AppCrash(string id, DateTimeOffset time, string app, string module = "game.exe", uint code = 0xC0000005, CrashKind kind = CrashKind.AppCrash) => new()
    {
        Id = id, Kind = kind, Time = time, LogName = "Application", EventId = 1000, AppName = app, ModuleName = module, ExceptionCode = kind == CrashKind.AppCrash ? code : null,
    };

    private static DumpFile Dump(string path, DateTimeOffset time, uint code, bool watchdog = false) =>
        new(path, time, 1024, new CrashDumpInfo
        {
            Format = DumpFormat.Kernel64,
            BugCheckCode = code,
            Parameters = [1, 0xFFFFF8061C200010, 3, 4],
            CrashTime = time,
            OsBuild = 26100,
            Modules = [new CrashModule("nvlddmkm.sys", 0xFFFFF8061C200000, 0x5A40000)],
        }, watchdog);

    [Fact]
    public void Dumps_attach_to_their_BugCheck_event_and_repeats_are_dropped()
    {
        var t = Now.AddDays(-3);
        var events = new[]
        {
            Bsod("e1001", t.AddMinutes(2), 0x116, @"%SystemRoot%\Minidump\102526-8421-01.dmp"),
            Bsod("k41", t.AddMinutes(1), 0x116, eventId: 41),
            Shutdown("e6008", t.AddMinutes(1), 6008),
            Shutdown("power", Now.AddDays(-20), 41),
            Shutdown("power6008", Now.AddDays(-20).AddMinutes(1), 6008),
            Shutdown("lonely6008", Now.AddDays(-40), 6008),
        };
        var dumps = new[]
        {
            Dump(@"C:\Windows\Minidump\102526-8421-01.dmp", t, 0x116),
            Dump(@"C:\Windows\Minidump\100126-1111-01.dmp", Now.AddDays(-27), 0x50),
        };

        var merged = CrashRules.Merge(events, dumps, Now);

        Assert.Equal(["e1001", "power", "dump-C:\\WINDOWS\\MINIDUMP\\100126-1111-01.DMP", "lonely6008"], merged.Select(e => e.Id));
        var bsod = merged[0];
        Assert.Equal(@"C:\Windows\Minidump\102526-8421-01.dmp", bsod.DumpPath);
        Assert.Equal(26100, bsod.OsBuild);
        Assert.Equal("nvlddmkm.sys", bsod.Blame?.Module);
        Assert.Equal(0x50u, merged[2].BugCheckCode);
        Assert.Equal("Memory dump file", merged[2].Source);
    }

    [Fact]
    public void Watchdog_dumps_attach_to_driver_resets_and_old_entries_fall_out()
    {
        var t = Now.AddDays(-9);
        var events = new[]
        {
            new CrashEvent { Id = "tdr", Kind = CrashKind.DriverReset, Time = t, EventId = 4101, LogName = "System", Driver = "nvlddmkm" },
            Bsod("old", Now.AddDays(-120), 0xA),
        };
        var dumps = new[]
        {
            Dump(@"C:\Windows\LiveKernelReports\WATCHDOG\WATCHDOG-1.dmp", t.AddMinutes(1), 0x141, watchdog: true),
            Dump(@"C:\Windows\LiveKernelReports\WATCHDOG\WATCHDOG-2.dmp", Now.AddDays(-30), 0x117, watchdog: true),
        };

        var merged = CrashRules.Merge(events, dumps, Now);

        Assert.Equal(2, merged.Count);
        Assert.All(merged, e => Assert.Equal(CrashKind.DriverReset, e.Kind));
        Assert.Equal(@"C:\Windows\LiveKernelReports\WATCHDOG\WATCHDOG-1.dmp", merged[0].DumpPath);
        Assert.Equal(0x141u, merged[0].BugCheckCode);
        Assert.Equal("nvlddmkm.sys", merged[1].Driver);
    }

    [Fact]
    public void The_timeline_groups_by_day_newest_first()
    {
        var local = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 28)));
        var events = new[]
        {
            AppCrash("a", local.AddHours(-1), "a.exe"),
            AppCrash("b", local.AddDays(-1), "b.exe"),
            AppCrash("c", local.AddHours(-3), "c.exe"),
        };

        var days = CrashRules.ByDay(events);

        Assert.Equal(2, days.Count);
        Assert.Equal(new DateOnly(2026, 10, 28), days[0].Day);
        Assert.Equal(["a", "c"], days[0].Events.Select(e => e.Id));
        Assert.Equal(["b"], days[1].Events.Select(e => e.Id));
    }

    [Fact]
    public void Most_crashing_apps_are_counted_and_ranked()
    {
        var events = new[]
        {
            AppCrash("1", Now.AddDays(-1), "Cyberpunk2077.exe", "nvwgf2umx.dll"),
            AppCrash("2", Now.AddDays(-4), "cyberpunk2077.exe", "nvwgf2umx.dll"),
            AppCrash("3", Now.AddDays(-10), "Cyberpunk2077.exe", "Cyberpunk2077.exe", 0xC0000409),
            AppCrash("4", Now.AddDays(-6), "Cyberpunk2077.exe", kind: CrashKind.AppHang),
            AppCrash("5", Now.AddDays(-2), "eldenring.exe"),
            AppCrash("6", Now.AddDays(-15), "eldenring.exe", "ntdll.dll", 0xC0000374),
            AppCrash("7", Now.AddDays(-3), "notepad.exe"),
            Bsod("x", Now.AddDays(-1), 0x116),
        };

        var groups = CrashRules.AppGroups(events);

        Assert.Equal(["Cyberpunk2077.exe", "eldenring.exe", "notepad.exe"], groups.Select(g => g.AppName));
        Assert.Equal(3, groups[0].Crashes);
        Assert.Equal(1, groups[0].Hangs);
        Assert.Equal(4, groups[0].Total);
        Assert.Equal("nvwgf2umx.dll", groups[0].TopModule);
        Assert.Equal(0xC0000005u, groups[0].TopExceptionCode);
        Assert.Equal(Now.AddDays(-1), groups[0].Latest);
        Assert.Single(CrashRules.AppGroups(events, max: 1));
    }

    [Fact]
    public void Summary_glance_and_filters_count_the_right_window()
    {
        var events = new[]
        {
            Bsod("b1", Now.AddDays(-3), 0x116),
            Bsod("b2", Now.AddDays(-12), 0x124),
            Bsod("b3", Now.AddDays(-45), 0x1A),
            Shutdown("s1", Now.AddDays(-20), 41),
            new CrashEvent { Id = "r1", Kind = CrashKind.DriverReset, Time = Now.AddDays(-9) },
            AppCrash("a1", Now.AddDays(-1), "game.exe"),
            AppCrash("h1", Now.AddDays(-2), "game.exe", kind: CrashKind.AppHang),
        };

        Assert.Equal(new CrashSummary(2, 1, 1, 2), CrashRules.Summarize(events, Now));
        var glance = CrashRules.Glance(events, Now);
        Assert.Equal(1, glance.BlueScreens);
        Assert.Equal(0, glance.SuddenShutdowns);
        Assert.Equal(Now.AddDays(-3), glance.Latest);
        Assert.Null(CrashRules.Glance([], Now).Latest);

        Assert.Equal(3, CrashRules.Filter(events, [CrashKind.BlueScreen], null).Count);
        Assert.Equal(2, CrashRules.Filter(events, [CrashKind.BlueScreen], Now.AddDays(-30)).Count);
        Assert.Equal(7, CrashRules.Filter(events, [], null).Count);
        Assert.Equal(2, CrashRules.Filter(events, [CrashKind.AppCrash, CrashKind.AppHang], null, "GAME.EXE").Count);
    }

    // ------------------------------------------------------------------ advice

    [Fact]
    public void Advice_links_to_the_right_pages()
    {
        static string[] Keys(CrashAdviceText a) => a.Pages.Select(p => p.PageKey).ToArray();

        var whea = CrashAdvice.For(Bsod("w", Now, 0x124), overclockApplied: true);
        Assert.Equal("overclock", Keys(whea)[0]);
        Assert.Contains("health", Keys(whea));
        Assert.DoesNotContain("overclock", Keys(CrashAdvice.For(Bsod("w", Now, 0x124), overclockApplied: false)));
        Assert.Contains("overclock", Keys(CrashAdvice.For(Bsod("c", Now, 0x101), overclockApplied: true)));

        Assert.Contains("memory", Keys(CrashAdvice.For(Bsod("m", Now, 0x1A), false)));
        Assert.Contains("memory", Keys(CrashAdvice.For(Bsod("p", Now, 0x50), false)));
        Assert.Contains("storage", Keys(CrashAdvice.For(Bsod("s", Now, 0x7A), false)));
        Assert.Contains("storage", Keys(CrashAdvice.For(Bsod("n", Now, 0x24), false)));

        var blamed = Bsod("d", Now, 0x1A) with { Blame = new CrashBlame("e2f68.sys", BlameConfidence.Medium, "Stop code parameter 2 points inside this file.", false), DumpPath = "x.dmp" };
        var advice = CrashAdvice.For(blamed, false);
        Assert.Contains("drivers", Keys(advice));
        Assert.Contains("best guess", advice.LikelyCause);
        Assert.Equal("0x0000001A, probably e2f68.sys", advice.Summary);

        Assert.Contains("missing-dlls", Keys(CrashAdvice.For(AppCrash("dotnet", Now, "tool.exe", code: 0xE0434352), false)));
        Assert.Contains("a part of Windows", CrashAdvice.For(AppCrash("nt", Now, "game.exe", "ntdll.dll", 0xC0000374), false).LikelyCause);
    }

    [Fact]
    public void Details_text_and_prompt_carry_the_facts()
    {
        var crash = Bsod("e", Now, 0x116, @"C:\Windows\Minidump\a.dmp") with { Parameters = [1, 2, 3, 4], Blame = new CrashBlame("nvlddmkm.sys", BlameConfidence.Medium, "Stop code parameter 2 points inside this file.", false) };
        var advice = CrashAdvice.For(crash, false);
        var text = CrashAdvice.DetailsText(crash, advice);
        Assert.Contains("VIDEO_TDR_FAILURE 0x00000116 (0x0000000000000001", text);
        Assert.Contains("nvlddmkm.sys", text);
        Assert.Contains(@"C:\Windows\Minidump\a.dmp", text);
        Assert.StartsWith("My PC had this crash.", CrashAdvice.AskPrompt(crash, advice));
    }

    [Fact]
    public void Overclock_state_comes_from_the_active_profile()
    {
        Assert.False(CrashRules.IsOverclockApplied(null));
        Assert.False(CrashRules.IsOverclockApplied(new OverclockSettings()));
        var settings = new OverclockSettings { ActiveProfileName = "Daily", Profiles = [new() { Name = "Daily", GpuCoreOffsetMhz = 120 }] };
        Assert.True(CrashRules.IsOverclockApplied(settings));
        settings.Profiles[0].GpuCoreOffsetMhz = 0;
        Assert.False(CrashRules.IsOverclockApplied(settings));
        Assert.False(CrashRules.IsOverclockApplied(new OverclockSettings { ActiveProfileName = "Stock" }));
    }

    // ------------------------------------------------------------------ demo and dashboard

    [Fact]
    public async Task Demo_crashes_fill_every_part_of_the_page()
    {
        var service = new DemoCrashService();
        var report = await service.ScanAsync(null, CancellationToken.None);

        Assert.Same(report, service.Last);
        Assert.True(report.OverclockApplied);
        var blue = report.Events.Where(e => e.Kind == CrashKind.BlueScreen).ToList();
        Assert.Equal([0x116u, 0x124u], blue.Select(e => e.BugCheckCode!.Value));
        Assert.Equal("nvlddmkm.sys", blue[0].Blame?.Module);
        Assert.Single(report.Events, e => e.Kind == CrashKind.SuddenShutdown);
        Assert.Equal(3, report.Events.Count(e => e.Kind == CrashKind.DriverReset));
        Assert.Equal(2, CrashRules.AppGroups(report.Events).Count);
        Assert.Equal("overclock", CrashAdvice.For(blue[1], report.OverclockApplied).Pages[0].PageKey);

        var glance = await service.GetGlanceAsync(CancellationToken.None);
        Assert.Equal(1, glance.BlueScreens);
    }

    [Fact]
    public void Recent_blue_screens_and_shutdowns_need_attention()
    {
        var facts = new AttentionFacts { Now = Now, LastCheckup = Now };
        Assert.DoesNotContain(AttentionRules.Build(facts), i => i.Id == "crashes.recent");
        Assert.DoesNotContain(AttentionRules.Build(facts with { RecentBlueScreens = 0, RecentSuddenShutdowns = 0 }), i => i.Id == "crashes.recent");

        var blue = Assert.Single(AttentionRules.Build(facts with { RecentBlueScreens = 2, RecentSuddenShutdowns = 1 }), i => i.Id == "crashes.recent");
        Assert.Equal(AttentionLevel.Critical, blue.Level);
        Assert.Equal("crashes", blue.PageKey);
        Assert.Equal(AttentionAction.OpenPage, blue.Action);
        Assert.Equal(2, Assert.Single(blue.TitleArgs));
        Assert.Contains("sudden shutdowns too", blue.Detail);

        var sudden = Assert.Single(AttentionRules.Build(facts with { RecentSuddenShutdowns = 1 }), i => i.Id == "crashes.recent");
        Assert.Equal(AttentionLevel.Warning, sudden.Level);
        Assert.Empty(sudden.TitleArgs);
        Assert.Equal(AttentionRules.CrashDays, CrashRules.AttentionDays);
    }
}
