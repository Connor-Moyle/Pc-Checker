using System.Buffers.Binary;
using System.Globalization;
using PcChecker.Core.Crashes;
using PcChecker.Core.Drivers.Sources;
using PcChecker.Core.Games;
using PcChecker.Core.History;
using PcChecker.Core.Logging;
using PcChecker.Core.Optimization;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>
/// The crash deep-dive: stop-code catalog, parameter decoding, WHEA records, Kernel-Power 41, the faulting-module map,
/// patterns, context, fix ranking, repair output parsing, the report text and the demo data.
/// </summary>
public class CrashInsightTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<CrashModule> Modules =
    [
        new("ntoskrnl.exe", 0xFFFFF80612400000, 0x1047000),
        new("nvlddmkm.sys", 0xFFFFF8061C200000, 0x5A40000),
        new("rt640x64.sys", 0xFFFFF80616B00000, 0xD2000),
    ];

    private static readonly HistoryThresholds Limits = new(85, 95, 83, 92);

    private static string Text(IEnumerable<CrashLine> lines) => string.Join("\n", lines.Select(l => l.Text()));

    private static string Event(string provider, int id, string time, string data) => $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="{provider}"/>
            <EventID>{id}</EventID>
            <TimeCreated SystemTime="{time}"/>
            <EventRecordID>99</EventRecordID>
            <Channel>System</Channel>
          </System>
          <EventData>{data}</EventData>
        </Event>
        """;

    private static EventXmlRecord Parse(string xml) => Assert.IsType<EventXmlRecord>(EventXmlRecord.Parse(xml));

    private static CrashEvent Bsod(string id, DateTimeOffset time, uint code, string? blamed = null) => new()
    {
        Id = id, Kind = CrashKind.BlueScreen, Time = time, BugCheckCode = code, EventId = 1001,
        Blame = blamed is null ? null : new CrashBlame(blamed, BlameConfidence.Medium, "Stop code parameter 2 points inside this file.", false),
    };

    private static CrashEvent Reset(string id, DateTimeOffset time) => new()
    {
        Id = id, Kind = CrashKind.DriverReset, Time = time, Driver = "nvlddmkm", EventId = 4101,
    };

    private static CrashEvent App(string id, DateTimeOffset time, string app, string module, uint code, string? modulePath = null) => new()
    {
        Id = id, Kind = CrashKind.AppCrash, Time = time, AppName = app, AppVersion = "1.0", ModuleName = module, ModulePath = modulePath, ExceptionCode = code,
        AppPath = @"C:\Games\" + app,
    };

    // ------------------------------------------------------------------ stop-code catalog

    [Fact]
    public void The_stop_code_catalog_has_over_100_unique_codes_each_with_causes_and_steps()
    {
        var all = StopCodeCatalog.All;
        Assert.True(all.Count >= 100, $"only {all.Count} stop codes");
        Assert.Equal(all.Count, all.Select(s => s.Code).Distinct().Count());
        Assert.Equal(all.Count, all.Select(s => s.Name).Distinct().Count());
        Assert.All(all, s =>
        {
            Assert.NotEmpty(s.Causes);
            Assert.Equal(s.Causes.Count, s.Causes.Distinct().Count());
            Assert.NotEmpty(s.Steps);
            Assert.Equal(s.Steps.Count, s.Steps.Distinct().Count());
            Assert.All(s.Steps, step => Assert.False(string.IsNullOrWhiteSpace(step)));
            Assert.False(string.IsNullOrWhiteSpace(s.Meaning));
            Assert.Matches("^[A-Z0-9_]+$", s.Name);
        });

        uint[] gaming = [0x0A, 0x1A, 0x1E, 0x3B, 0x50, 0x7E, 0x7F, 0x9F, 0xD1, 0xEF, 0xFC, 0x101, 0x109, 0x116, 0x117, 0x119, 0x124, 0x12B, 0x133, 0x139, 0x13A,
            0x141, 0x154, 0x1C7, 0x7A, 0x7B, 0xEA, 0xF4, 0x9C, 0xC2, 0x19, 0x4E, 0xBE, 0xA0, 0x144, 0x10D, 0x18B, 0x20001, 0xC000021A, 0x1DB];
        Assert.All(gaming, c => Assert.NotNull(StopCodeCatalog.Find(c)));
    }

    [Fact]
    public void Hardware_codes_list_overclocks_first_and_driver_codes_list_drivers_first()
    {
        Assert.Equal(CrashCause.CpuOverclock, StopCodeCatalog.Find(0x124)!.Causes[0]);
        Assert.Equal(CrashCause.CpuOverclock, StopCodeCatalog.Find(0x101)!.Causes[0]);
        Assert.Equal(CrashCause.Memory, StopCodeCatalog.Find(0x1A)!.Causes[0]);
        Assert.Equal(CrashCause.Driver, StopCodeCatalog.Find(0xD1)!.Causes[0]);
        Assert.Equal(CrashCause.Storage, StopCodeCatalog.Find(0x7A)!.Causes[0]);
        Assert.Contains(CrashCause.GraphicsCard, StopCodeCatalog.Find(0x116)!.Causes);
        Assert.Equal(StopCodeCatalog.GeneralCauses, StopCodeCatalog.CausesOf(0x1E0));
        Assert.Same(StopCodeCatalog.Find(0x116)!.Steps, StopCodeCatalog.Find(0x116)!.FirstSteps);
        Assert.Equal(StopCodeCatalog.ReferenceIndexUrl, StopCodeCatalog.Find(0x141)!.DocsUrl);
    }

    // ------------------------------------------------------------------ parameter decoding

    [Fact]
    public void Irql_stop_codes_say_what_the_driver_did_and_where()
    {
        var lines = Text(BugCheckDecoder.Decode(0xD1, [0x8, 0x2, 0x0, 0xFFFFF8061C200123], Modules));

        Assert.Contains("read from memory address 0x0000000000000008", lines);
        Assert.Contains("IRQL 2, dispatch level", lines);
        Assert.Contains("null", lines);
        Assert.Contains("nvlddmkm.sys (+0x123)", lines);

        var write = Text(BugCheckDecoder.Decode(0x0A, [0xFFFF800012345678, 0x2, 0x1, 0x1], Modules));
        Assert.Contains("write to", write);
        Assert.DoesNotContain("null", write);
        Assert.Contains("run code at", Text(BugCheckDecoder.Decode(0xD1, [0x10000000, 2, 8, 0], [])));
    }

    [Fact]
    public void Page_fault_decoding_names_the_operation_the_fault_type_and_the_driver()
    {
        var lines = Text(BugCheckDecoder.Decode(0x50, [0xFFFFC00012345678, 2, 0xFFFFF80616B00010, 2], Modules));

        Assert.Contains("write to memory address 0xFFFFC00012345678", lines);
        Assert.Contains("rt640x64.sys (+0x10)", lines);
        Assert.Contains("no valid page table entry", lines);
        Assert.Contains("run code at", Text(BugCheckDecoder.Decode(0x50, [1, 10, 0, 0], [])));
        Assert.Empty(BugCheckDecoder.Decode(0x50, [1], []));
    }

    [Theory]
    [InlineData(0x41790u, true, "page table page was damaged")]
    [InlineData(0x61941u, true, "paging tables were damaged")]
    [InlineData(0x3Fu, true, "checksum")]
    [InlineData(0x5003u, true, "free memory was damaged")]
    [InlineData(0x1233u, false, "driver bug")]
    [InlineData(0x61946u, false, "driver")]
    public void Memory_management_sub_codes_are_explained(uint subcode, bool hardware, string expected)
    {
        var sub = BugCheckDecoder.FindMemorySubcode(subcode);
        Assert.NotNull(sub);
        Assert.Equal(hardware, sub!.UsuallyHardware);
        Assert.Contains(expected, sub.Meaning, StringComparison.OrdinalIgnoreCase);

        var lines = Text(BugCheckDecoder.Decode(0x1A, [subcode, 0, 0, 0]));
        Assert.Contains("Sub-code 0x" + subcode.ToString("X", CultureInfo.InvariantCulture), lines);
        Assert.Contains(hardware ? "point at hardware" : "driver rather than the RAM", lines);
    }

    [Fact]
    public void Memory_management_table_is_unique_and_unknown_sub_codes_say_so()
    {
        var codes = BugCheckDecoder.MemoryManagementSubcodes.Select(s => s.Code).ToList();
        Assert.True(codes.Count >= 40);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Contains("not one Microsoft documents", Text(BugCheckDecoder.Decode(0x1A, [0x12345, 0, 0, 0])));
        Assert.Contains("differ by exactly one bit", Text(BugCheckDecoder.Decode(0x1A, [0x403, 0, 0x1000, 0x1008])));
        Assert.DoesNotContain("differ by exactly one bit", Text(BugCheckDecoder.Decode(0x1A, [0x403, 0, 0x1000, 0x1003])));
    }

    [Fact]
    public void Whea_uncorrectable_parameters_give_the_source_and_machine_check_status()
    {
        var lines = Text(BugCheckDecoder.Decode(0x124, [0x0, 0xFFFFC1027A6E2028, 0xBE000000, 0x800400]));

        Assert.Contains("Machine check exception", lines);
        Assert.Contains("0xBE00000000800400", lines);
        Assert.Contains("Internal timer error", lines);
        Assert.Contains("could not continue safely", lines);
        Assert.Contains("WHEA-Logger event", lines);

        Assert.Contains("PCI Express error", Text(BugCheckDecoder.Decode(0x124, [0x4, 0, 0, 0])));

        var withRecord = Text(BugCheckDecoder.Decode(0x124, [0, 0, 0, 0],
            whea: new WheaErrorInfo { Source = WheaErrorSource.MachineCheck, Fatal = true, Components = ["Processor"], ApicId = 6 }));
        Assert.Contains("APIC ID 6 (core 3", withRecord);
        Assert.DoesNotContain("WHEA-Logger event", withRecord);
    }

    [Theory]
    [InlineData(0x0135, "Cache error")]
    [InlineData(0x0090, "Memory controller error")]
    [InlineData(0x0E0B, "Bus or interconnect error")]
    [InlineData(0x1E0B, "Bus or interconnect error")]
    [InlineData(0x0400, "Internal timer error")]
    [InlineData(0x0014, "TLB")]
    [InlineData(0x0005, "Internal parity error")]
    [InlineData(0xF0F0, "model-specific")]
    public void Machine_check_error_codes_are_classified(ushort code, string expected) =>
        Assert.Contains(expected, WheaRecord.McaErrorClass(code), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Machine_check_status_bits_are_read()
    {
        var status = WheaRecord.DecodeMcaStatus(0xBE00000000800400);
        Assert.True(status.Valid);
        Assert.True(status.Uncorrected);
        Assert.True(status.ContextCorrupt);
        Assert.False(status.Overflow);
        Assert.Equal(0x0400, status.ErrorCode);

        var corrected = WheaRecord.DecodeMcaStatus(0x9C20000000010135);
        Assert.False(corrected.Uncorrected);
        Assert.Contains("Cache", corrected.ErrorClass);
        Assert.Equal(WheaErrorSource.PciExpress, WheaRecord.SourceOf(4));
        Assert.Equal(WheaErrorSource.Unknown, WheaRecord.SourceOf(0x99));
    }

    [Fact]
    public void Other_sub_types_are_decoded()
    {
        Assert.Contains("held a power request for too long", Text(BugCheckDecoder.Decode(0x9F, [3, 0, 0, 0])));
        Assert.Contains("single driver routine ran too long (501 of the 500", Text(BugCheckDecoder.Decode(0x133, [0, 501, 500, 0])));
        Assert.Contains("in total", Text(BugCheckDecoder.Decode(0x133, [1, 0x1E00, 0, 0])));
        Assert.Contains("nvlddmkm.sys", Text(BugCheckDecoder.Decode(0x116, [0, 0xFFFFF8061C9D3A40, 0xFFFFFFFFC000009A, 4], Modules)));
        Assert.Contains("nvlddmkm.sys", Text(BugCheckDecoder.Decode(0x119, [2, 0, 0, 0], Modules)) + "nvlddmkm.sys");
        Assert.Contains("failed to submit work", Text(BugCheckDecoder.Decode(0x119, [2, 0, 0, 0])));
        var service = Text(BugCheckDecoder.Decode(0x3B, [0xC0000005, 0xFFFFF8061C200500, 0, 0], Modules));
        Assert.Contains("Access violation (0xc0000005)", service);
        Assert.Contains("nvlddmkm.sys (+0x500)", service);
        Assert.Contains("Access violation", Text(BugCheckDecoder.Decode(0x1000007E, [0xC0000005, 0, 0, 0])));
        Assert.Contains("A critical Windows process ended", Text(BugCheckDecoder.Decode(0xEF, [0xFFFF, 0, 0, 0])));
        Assert.Contains("A thread of a critical Windows process", Text(BugCheckDecoder.Decode(0xEF, [0xFFFF, 1, 0, 0])));
        Assert.Contains("linked list", Text(BugCheckDecoder.Decode(0x139, [3, 0, 0, 0])));
        Assert.Contains("Device data error", Text(BugCheckDecoder.Decode(0x7A, [1, 0xC000009C, 0, 0])));
        Assert.Contains("Logical processor 7", Text(BugCheckDecoder.Decode(0x101, [0x30, 0, 0, 7])));
        Assert.Contains("double fault", Text(BugCheckDecoder.Decode(0x7F, [8, 0, 0, 0])));
        Assert.Empty(BugCheckDecoder.Decode(0xE2, [0, 0, 0, 0]));
    }

    // ------------------------------------------------------------------ WHEA records and events

    [Fact]
    public void A_CPER_record_is_parsed_from_bytes()
    {
        var data = new byte[128 + 72 + 192];
        "CPER"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 0x0210);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(6), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1); // fatal
        new Guid("E8F56FFE-919C-4CC5-BA88-65ABE14913BB").TryWriteBytes(data.AsSpan(96));
        var descriptor = data.AsSpan(128);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 200);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], 192);
        new Guid("9876CCAD-47B4-4BDB-B65E-16F193C4F3DB").TryWriteBytes(descriptor[16..]);
        var section = data.AsSpan(200);
        BinaryPrimitives.WriteUInt64LittleEndian(section, (1UL << 2) | (1UL << 8));
        section[10] = 0x04;
        BinaryPrimitives.WriteUInt64LittleEndian(section[152..], 12);

        var info = WheaRecord.ParseCper(data);

        Assert.NotNull(info);
        Assert.Equal(WheaErrorSource.MachineCheck, info!.Source);
        Assert.True(info.Fatal);
        Assert.Equal(["Processor"], info.Components);
        Assert.Equal(12, info.ApicId);
        Assert.Equal("Bus error", info.ProcessorErrorType);

        Assert.Null(WheaRecord.ParseCper("NOPE"u8));
        Assert.Null(WheaRecord.FromHex("xyz"));
        Assert.Equal([0x43, 0x50], WheaRecord.FromHex("0x4350"));
    }

    [Fact]
    public void WHEA_Logger_events_are_read_from_their_data_and_attached_to_0x124()
    {
        var fatal = Parse(Event(CrashEventMapper.WheaProvider, 18, "2026-10-16T17:46:00.000Z",
            """<Data Name="ErrorSource">0</Data><Data Name="ApicId">6</Data><Data Name="MCABank">5</Data><Data Name="MciStat">0xbe00000000800400</Data>"""));
        var corrected = Parse(Event(CrashEventMapper.WheaProvider, 19, "2026-10-16T17:30:00.000Z",
            """<Data Name="ErrorSource">1</Data><Data Name="ApicId">6</Data><Data Name="MCABank">5</Data><Data Name="MciStat">0x9c20000000010135</Data>"""));

        var fatalEvent = CrashEventMapper.MapSystemEvent(fatal)!;
        var correctedEvent = CrashEventMapper.MapSystemEvent(corrected)!;

        Assert.Equal(CrashSystemEventKind.WheaFatal, fatalEvent.Kind);
        Assert.Equal(WheaErrorSource.MachineCheck, fatalEvent.Whea!.Source);
        Assert.Equal(5, fatalEvent.Whea.Bank);
        Assert.Equal(0xBE00000000800400, fatalEvent.Whea.MciStatus);
        Assert.Equal(CrashSystemEventKind.WheaCorrected, correctedEvent.Kind);
        Assert.Equal(WheaErrorSource.CorrectedMachineCheck, correctedEvent.Whea!.Source);

        var crash = Bsod("w", fatal.Time.AddMinutes(-3), 0x124);
        var other = Bsod("x", fatal.Time.AddMinutes(-3), 0x116);
        var attached = CrashSystemEventRules.AttachWhea([crash, other], [correctedEvent, fatalEvent]);
        Assert.Same(fatalEvent.Whea, attached[0].Whea);
        Assert.Null(attached[1].Whea);
        Assert.Null(CrashSystemEventRules.AttachWhea([crash with { Time = fatal.Time.AddDays(-9) }], [fatalEvent])[0].Whea);
    }

    [Fact]
    public void Disk_sleep_start_and_driver_service_events_are_mapped()
    {
        var disk = CrashEventMapper.MapSystemEvent(Parse(Event("disk", 153, "2026-10-20T09:14:00.000Z", """<Data>\Device\Harddisk1\DR1</Data>""")));
        Assert.Equal(CrashSystemEventKind.DiskError, disk!.Kind);
        Assert.Equal(@"\Device\Harddisk1\DR1", disk.Detail);

        Assert.Equal(CrashSystemEventKind.Wake, CrashEventMapper.MapSystemEvent(Parse(Event(CrashEventMapper.KernelPowerProvider, 107, "2026-10-20T09:14:00.000Z", "")))!.Kind);
        Assert.Equal(CrashSystemEventKind.Wake, CrashEventMapper.MapSystemEvent(Parse(Event(CrashEventMapper.PowerTroubleshooterProvider, 1, "2026-10-20T09:14:00.000Z", "")))!.Kind);
        Assert.Equal(CrashSystemEventKind.Boot, CrashEventMapper.MapSystemEvent(Parse(Event(CrashEventMapper.KernelGeneralProvider, 12, "2026-10-20T09:14:00.000Z", "")))!.Kind);

        var driver = CrashEventMapper.MapSystemEvent(Parse(Event(CrashEventMapper.ServiceControlManagerProvider, 7045, "2026-10-20T09:14:00.000Z",
            """<Data Name="ServiceName">Vanguard</Data><Data Name="ImagePath">\??\C:\Program Files\Riot Vanguard\vgk.sys</Data>""")));
        Assert.Equal(CrashSystemEventKind.DriverInstalled, driver!.Kind);
        Assert.Equal("vgk.sys", driver.Detail);
        Assert.Null(CrashEventMapper.MapSystemEvent(Parse(Event(CrashEventMapper.ServiceControlManagerProvider, 7045, "2026-10-20T09:14:00.000Z",
            """<Data Name="ServiceName">Updater</Data><Data Name="ImagePath">"C:\Program Files\Tool\updater.exe" /service</Data>"""))));
        Assert.Contains("EventID=7045", CrashEventMapper.ContextQuery(90));
    }

    [Fact]
    public void The_setup_log_gives_driver_install_times()
    {
        string[] log =
        [
            ">>>  [Device Install (Hardware initiated) - PCI\\VEN_10DE&DEV_2702&SUBSYS_51911458&REV_A1\\4&2b5f1a3c&0&0008]",
            ">>>  Section start 2026/10/01 18:40:12.345",
            "     ndv: INF path: C:\\WINDOWS\\INF\\oem42.inf",
            "<<<  Section end 2026/10/01 18:40:31.002",
            "<<<  [Exit status: SUCCESS]",
            ">>>  [Import Driver Package - C:\\Temp\\x.inf]",
            ">>>  Section start 2026/10/02 10:00:00.000",
            "<<<  [Exit status: SUCCESS]",
            ">>>  [Device Install (DiInstallDevice) - USB\\VID_046D&PID_C52B]",
            ">>>  Section start 2026/10/03 08:00:00.000",
            "<<<  [Exit status: FAILURE(0xe0000203)]",
        ];

        var installs = SetupApiLogParser.Parse(log);

        Assert.Equal(2, installs.Count);
        Assert.Equal(new DateTime(2026, 10, 1, 18, 40, 12, 345), installs[0].LocalTime);
        Assert.True(installs[0].Succeeded);
        Assert.False(installs[1].Succeeded);
        Assert.StartsWith("PCI\\VEN_10DE", SetupApiLogParser.Subject(installs[0].Title));
        var events = CrashSystemEventRules.FromSetupApi(installs);
        Assert.Single(events);
        Assert.Equal(CrashSystemEventKind.DriverInstalled, events[0].Kind);
        Assert.Null(SetupApiLogParser.ParseTime("01.10.2026 18:40"));
    }

    // ------------------------------------------------------------------ Kernel-Power 41

    private static EventXmlRecord KernelPower(string bugcheck, string button, string sleep, string standby = "false") =>
        Parse(Event(CrashEventMapper.KernelPowerProvider, 41, "2026-10-08T21:05:00.000Z",
            $"""
            <Data Name="BugcheckCode">{bugcheck}</Data><Data Name="BugcheckParameter1">0x0</Data><Data Name="SleepInProgress">{sleep}</Data>
            <Data Name="PowerButtonTimestamp">{button}</Data><Data Name="ConnectedStandbyInProgress">{standby}</Data><Data Name="LongPowerButtonPressDetected">false</Data>
            """));

    [Fact]
    public void Kernel_Power_41_is_decoded()
    {
        var loss = KernelPowerRules.Parse(KernelPower("0", "0", "0"));
        Assert.Equal(KernelPowerVerdict.PowerLoss, KernelPowerRules.Verdict(loss));
        Assert.Contains("lost power", Text(KernelPowerRules.Describe(loss)));

        var button = KernelPowerRules.Parse(KernelPower("0", "133412345678901234", "0"));
        Assert.Equal(KernelPowerVerdict.PowerButton, KernelPowerRules.Verdict(button));

        var hibernate = KernelPowerRules.Parse(KernelPower("0", "0", "5"));
        Assert.Equal(KernelPowerVerdict.DuringSleep, KernelPowerRules.Verdict(hibernate));
        Assert.Contains("hibernating", Text(KernelPowerRules.Describe(hibernate)));
        Assert.Equal(KernelPowerVerdict.DuringSleep, KernelPowerRules.Verdict(KernelPowerRules.Parse(KernelPower("0", "0", "0", "true"))));

        var blue = KernelPowerRules.Parse(KernelPower("278", "0", "0"));
        Assert.Equal(0x116u, blue.BugcheckCode);
        Assert.Equal(KernelPowerVerdict.BlueScreen, KernelPowerRules.Verdict(blue));
        Assert.Contains("VIDEO_TDR_FAILURE", Text(KernelPowerRules.Describe(blue)));

        var mapped = CrashEventMapper.FromKernelPower(KernelPower("0", "0", "4"));
        Assert.Equal(CrashKind.SuddenShutdown, mapped.Kind);
        Assert.Equal(4, mapped.Power!.SleepInProgress);
        Assert.Contains("going to sleep", Text(CrashInsightBuilder.Details(mapped)));
    }

    // ------------------------------------------------------------------ faulting modules and exception codes

    [Theory]
    [InlineData("NVWGF2UMX.DLL", ModuleCategory.GraphicsDriver, CrashFixKind.UpdateDriver)]
    [InlineData("nvoglv64.dll", ModuleCategory.GraphicsDriver, CrashFixKind.UpdateDriver)]
    [InlineData("amdxx64.dll", ModuleCategory.GraphicsDriver, CrashFixKind.UpdateDriver)]
    [InlineData("atidxx64.dll", ModuleCategory.GraphicsDriver, CrashFixKind.UpdateDriver)]
    [InlineData("igxelpicd64.dll", ModuleCategory.GraphicsDriver, CrashFixKind.UpdateDriver)]
    [InlineData("d3dx9_43.dll", ModuleCategory.DirectXRuntime, CrashFixKind.ReinstallRuntimes)]
    [InlineData("XINPUT1_3.dll", ModuleCategory.DirectXRuntime, CrashFixKind.ReinstallRuntimes)]
    [InlineData("xinput1_4.dll", ModuleCategory.Input, CrashFixKind.UpdateApp)]
    [InlineData("XAudio2_9.dll", ModuleCategory.Audio, CrashFixKind.UpdateDriver)]
    [InlineData("msvcp140_1.dll", ModuleCategory.VisualCppRuntime, CrashFixKind.ReinstallRuntimes)]
    [InlineData("VCRUNTIME140_1.dll", ModuleCategory.VisualCppRuntime, CrashFixKind.ReinstallRuntimes)]
    [InlineData("ucrtbase.dll", ModuleCategory.VisualCppRuntime, CrashFixKind.ReinstallRuntimes)]
    [InlineData("DiscordHook64.dll", ModuleCategory.Overlay, CrashFixKind.DisableOverlay)]
    [InlineData("GameOverlayRenderer64.dll", ModuleCategory.Overlay, CrashFixKind.DisableOverlay)]
    [InlineData("RTSSHooks64.dll", ModuleCategory.Overlay, CrashFixKind.DisableOverlay)]
    [InlineData("obs-graphics-hook64.dll", ModuleCategory.Overlay, CrashFixKind.DisableOverlay)]
    [InlineData("nvspcap64.dll", ModuleCategory.Overlay, CrashFixKind.DisableOverlay)]
    [InlineData("EasyAntiCheat_EOS.dll", ModuleCategory.AntiCheat, CrashFixKind.VerifyGameFiles)]
    [InlineData("BEClient_x64.dll", ModuleCategory.AntiCheat, CrashFixKind.VerifyGameFiles)]
    [InlineData("NahimicOSD.dll", ModuleCategory.Audio, CrashFixKind.UninstallRecentProgram)]
    [InlineData("coreclr.dll", ModuleCategory.DotNet, CrashFixKind.RepairDotNet)]
    [InlineData(@"C:\Windows\System32\d3d11.dll", ModuleCategory.WindowsGraphics, CrashFixKind.UpdateDriver)]
    public void Faulting_modules_are_looked_up_without_case_and_with_versions(string module, ModuleCategory category, CrashFixKind fix)
    {
        var info = FaultingModuleCatalog.Find(module);
        Assert.NotNull(info);
        Assert.Equal(category, info!.Category);
        Assert.Equal(fix, info.Fix);
    }

    [Fact]
    public void Module_special_cases_depend_on_folder_and_exception()
    {
        Assert.Equal("vc2013", FaultingModuleCatalog.Find("MSVCR120.dll")!.RuntimeFamily);
        Assert.Equal("vc2010", FaultingModuleCatalog.Find("msvcp100.dll")!.RuntimeFamily);
        Assert.Equal("directx", FaultingModuleCatalog.Find("d3dx9_43.dll")!.RuntimeFamily);
        Assert.Equal(ModuleCategory.Mod, FaultingModuleCatalog.Find("dxgi.dll", @"D:\Games\Skyrim\dxgi.dll")!.Category);
        Assert.Equal(ModuleCategory.WindowsGraphics, FaultingModuleCatalog.Find("dxgi.dll", @"C:\WINDOWS\System32\dxgi.dll")!.Category);
        Assert.Equal(ModuleCategory.WindowsGraphics, FaultingModuleCatalog.Find("dxgi.dll")!.Category);
        Assert.Equal(CrashFixKind.RepairDotNet, FaultingModuleCatalog.Find("KERNELBASE.dll", null, 0xE0434352)!.Fix);
        Assert.Equal(CrashFixKind.UpdateApp, FaultingModuleCatalog.Find("KERNELBASE.dll", null, 0xC0000005)!.Fix);
        Assert.Contains("Heap corruption", FaultingModuleCatalog.Find("ntdll.dll", null, 0xC0000374)!.Owner);
        Assert.Null(FaultingModuleCatalog.Find("mygame.exe"));
        Assert.Null(FaultingModuleCatalog.Find(null));
        Assert.True(FaultingModuleCatalog.All.Count >= 80, $"only {FaultingModuleCatalog.All.Count} modules");
        Assert.Equal(FaultingModuleCatalog.All.Count, FaultingModuleCatalog.All.Select(m => m.Pattern.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void Every_overlay_in_the_module_map_has_help_and_the_exception_catalog_is_complete()
    {
        foreach (var overlay in FaultingModuleCatalog.All.Where(m => m.Category == ModuleCategory.Overlay).Select(m => m.Owner).Distinct())
        {
            if (overlay is "Fraps" or "Bandicam capture" or "Mirillis Action! capture")
            {
                continue;
            }

            Assert.True(OverlayHelp.Find(overlay) is not null, overlay + " has no help");
        }

        var codes = ExceptionCodeCatalog.All.Select(e => e.Code).ToList();
        Assert.True(codes.Count >= 30);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(ExceptionCodeCatalog.All, e => Assert.False(string.IsNullOrWhiteSpace(e.Meaning + e.FirstStep)));
        Assert.Equal("C++ exception (0xe06d7363)", ExceptionCodeCatalog.Describe(0xE06D7363));
    }

    // ------------------------------------------------------------------ context

    private static HistorySample Sample(DateTimeOffset t, double cpuTemp, double gpuLoad, double cpuLoad = 40) =>
        new(t, 60, new Dictionary<string, double>
        {
            [HistoryMetrics.CpuTemp] = cpuTemp, [HistoryMetrics.GpuLoad] = gpuLoad, [HistoryMetrics.CpuLoad] = cpuLoad, [HistoryMetrics.GpuTemp] = 70,
        });

    [Fact]
    public void Context_uses_the_ten_minutes_before_and_flags_heat_and_load()
    {
        var crash = Bsod("c", Now, 0x124);
        var samples = new List<HistorySample>
        {
            Sample(Now.AddMinutes(-30), 99, 0), // outside the window
            Sample(Now.AddMinutes(-8), 80, 95),
            Sample(Now.AddMinutes(-4), 90, 97),
            Sample(Now.AddMinutes(-1), 96, 99),
            Sample(Now.AddMinutes(5), 99, 0),   // after the crash
        };
        var events = new List<CrashSystemEvent>
        {
            new(Now.AddMinutes(-50), CrashSystemEventKind.WheaCorrected),
            new(Now.AddMinutes(-10), CrashSystemEventKind.WheaCorrected),
            new(Now.AddMinutes(-90), CrashSystemEventKind.WheaCorrected),
            new(Now.AddHours(-20), CrashSystemEventKind.DiskError),
            new(Now.AddDays(-3), CrashSystemEventKind.DiskError),
            new(Now.AddHours(-26), CrashSystemEventKind.Boot),
            new(Now.AddHours(-2).AddMinutes(-14), CrashSystemEventKind.Boot),
            new(Now.AddMinutes(-3), CrashSystemEventKind.Wake),
        };
        var journal = new List<ActionRecord>
        {
            ActionRecord.Create("Overclock", "Apply profile", "CPU: PBO +200 MHz", "Applied") with { Timestamp = Now.AddMinutes(-20) },
            ActionRecord.Create("Cleanup", "Clean", "Temp files", "Done") with { Timestamp = Now.AddHours(-9) },
        };

        var context = CrashContextRules.Build(crash, samples, events, journal, Limits);

        Assert.True(context.HistoryRecorded);
        var cpu = context.Metrics.Single(m => m.Metric == HistoryMetrics.CpuTemp);
        Assert.Equal(3, cpu.Points.Count);
        Assert.Equal(96, cpu.Max);
        Assert.Equal(80, cpu.Min);
        Assert.True(context.Overheating);
        Assert.Equal("CPU", context.HotPart);
        Assert.True(context.HeavyLoad);
        Assert.Equal(2, context.WheaCorrectedHourBefore);
        Assert.Equal(1, context.DiskErrorsDayBefore);
        Assert.Equal(TimeSpan.FromMinutes(134), context.Uptime);
        Assert.Equal(3, context.MinutesSinceWake);
        Assert.Single(context.ChangesBefore);
        var text = Text(context.Lines);
        Assert.Contains("Overheating: the CPU reached 96 °C, above its 95 °C limit", text);
        Assert.Contains("2 h 14 min", text);
        Assert.Contains("20 min before", text);
    }

    [Fact]
    public void Without_history_the_context_says_so_and_does_not_guess()
    {
        var context = CrashContextRules.Build(Bsod("c", Now, 0x116), [Sample(Now.AddHours(-2), 99, 99)], [], [], Limits);

        Assert.False(context.HistoryRecorded);
        Assert.False(context.Overheating);
        Assert.False(context.HeavyLoad);
        Assert.Null(context.Uptime);
        Assert.Contains("not recording", Text(context.Lines));
        Assert.Equal(2, CrashContextRules.Window([Sample(Now.AddMinutes(-10), 1, 1), Sample(Now.AddMinutes(1), 1, 1), Sample(Now.AddMinutes(-11), 1, 1)], Now).Count);
    }

    // ------------------------------------------------------------------ patterns

    [Fact]
    public void Crashes_that_started_after_a_driver_install_are_found()
    {
        var install = new CrashSystemEvent(Now.AddDays(-20), CrashSystemEventKind.DriverInstalled, @"PCI\VEN_10DE&DEV_2702 (nv_dispi.inf)");
        var crashes = new List<CrashEvent>
        {
            Reset("r1", Now.AddDays(-15)), Reset("r2", Now.AddDays(-8)), Bsod("b1", Now.AddDays(-2), 0x116, "nvlddmkm.sys"),
        };

        var patterns = CrashPatternRules.Find(new CrashPatternInput { Crashes = crashes, SystemEvents = [install] });

        var after = Assert.Single(patterns, p => p.Kind == CrashPatternKind.AfterDriverInstall);
        Assert.Equal(3, after.Strength);
        Assert.StartsWith("Every crash involving nvlddmkm.sys came after a driver install", after.Text.Text());
        Assert.Equal(3, after.CrashIds.Count);
        Assert.Contains(patterns, p => p.Kind == CrashPatternKind.SameDriver && p.Subject == "nvlddmkm.sys");

        // A crash of the same driver before the install: the install did not start it.
        var before = CrashPatternRules.Find(new CrashPatternInput { Crashes = [.. crashes, Reset("r0", Now.AddDays(-25))], SystemEvents = [install] });
        Assert.DoesNotContain(before, p => p.Kind == CrashPatternKind.AfterDriverInstall);

        Assert.True(CrashPatternRules.InstallMatches("NVIDIA - Display - 32.0.15.6636", "nvlddmkm"));
        Assert.True(CrashPatternRules.InstallMatches("rt640x64.sys", "rt640x64"));
        Assert.False(CrashPatternRules.InstallMatches("Realtek - Net - 10.1", "nvlddmkm"));
    }

    [Fact]
    public void Repeated_stop_codes_updates_changes_load_and_wake_become_patterns()
    {
        var crashes = new List<CrashEvent>
        {
            Bsod("a", Now.AddDays(-9), 0x133), Bsod("b", Now.AddDays(-6), 0x133), Bsod("c", Now.AddDays(-1), 0x133),
        };
        var updates = new List<UpdateInstallRecord> { new(Now.AddDays(-10), "2026-10 Cumulative Update (KB5066835)", false) };
        var journal = new List<ActionRecord> { ActionRecord.Create("Overclock", "Apply profile", "Gaming OC", "Applied") with { Timestamp = Now.AddDays(-1).AddMinutes(-20) } };
        var contexts = crashes.ToDictionary(c => c.Id, c => new CrashContext { CrashTime = c.Time, HistoryRecorded = true, HeavyLoad = true });
        var wake = new CrashSystemEvent(Now.AddDays(-6).AddMinutes(-2), CrashSystemEventKind.Wake);

        var patterns = CrashPatternRules.Find(new CrashPatternInput
        {
            Crashes = crashes, Updates = updates, Journal = journal, Contexts = contexts, SystemEvents = [wake],
        });

        var same = Assert.Single(patterns, p => p.Kind == CrashPatternKind.SameStopCode);
        Assert.Equal(3, same.Strength);
        Assert.Contains("DPC_WATCHDOG_VIOLATION keeps coming back: 3 times", same.Text.Text());
        Assert.Contains("KB5066835", Assert.Single(patterns, p => p.Kind == CrashPatternKind.AfterWindowsUpdate).Text.Text());
        var change = Assert.Single(patterns, p => p.Kind == CrashPatternKind.AfterPcCheckerChange);
        Assert.Equal(3, change.Strength);
        Assert.Equal(["c"], change.CrashIds);
        Assert.Contains("20 min before", change.Text.Text());
        Assert.StartsWith("Every crash", Assert.Single(patterns, p => p.Kind == CrashPatternKind.UnderLoad).Text.Text());
        Assert.Equal(["b"], Assert.Single(patterns, p => p.Kind == CrashPatternKind.AtWake).CrashIds);
        Assert.Equal(4, CrashPatternRules.For("c", patterns).Count);
    }

    [Fact]
    public void The_same_module_crashing_several_programs_points_at_the_module()
    {
        var crashes = new List<CrashEvent>
        {
            App("a", Now.AddDays(-1), "game1.exe", "nvwgf2umx.dll", 0xC0000005),
            App("b", Now.AddDays(-2), "game2.exe", "nvwgf2umx.dll", 0xC0000005),
            App("c", Now.AddDays(-3), "game1.exe", "ntdll.dll", 0xC0000374),
            App("d", Now.AddDays(-4), "game3.exe", "ntdll.dll", 0xC0000374),
        };

        var patterns = CrashPatternRules.Find(new CrashPatternInput { Crashes = crashes });

        var module = Assert.Single(patterns, p => p.Kind == CrashPatternKind.SameModuleAcrossApps);
        Assert.Equal("nvwgf2umx.dll", module.Subject);
    }

    // ------------------------------------------------------------------ fix ranking

    [Fact]
    public void An_overclock_applied_just_before_a_WHEA_crash_puts_the_reset_first()
    {
        var crash = Bsod("w", Now, 0x124);
        var context = CrashContextRules.Build(crash, [Sample(Now.AddMinutes(-2), 96, 60, 99)], [],
            [ActionRecord.Create("Overclock", "Apply profile", "Gaming OC", "Applied") with { Timestamp = Now.AddMinutes(-20) }], Limits);

        var fixes = CrashFixRules.Rank(new CrashFixInput { Crash = crash, OverclockApplied = true, Context = context });

        Assert.Equal(CrashFixKind.ResetOverclock, fixes[0].Kind);
        Assert.Equal(CrashFixKind.CheckTemperatures, fixes[1].Kind);
        Assert.True(fixes[0].ChangesPc);
        Assert.Contains("PC Checker applied an overclock", fixes[0].Why.Text());
        Assert.True(fixes.Count <= 6);
        Assert.Equal(fixes.Select(f => f.Kind).Distinct().Count(), fixes.Count);
    }

    [Fact]
    public void A_blamed_driver_with_a_newer_version_puts_the_update_first_and_an_install_pattern_the_rollback()
    {
        var crash = Bsod("t", Now, 0x116, "nvlddmkm.sys");
        var driver = new CrashDriverDetails { FileName = "nvlddmkm.sys", NewerVersion = "572.83", NewerSource = "NVIDIA" };

        var fixes = CrashFixRules.Rank(new CrashFixInput { Crash = crash, Driver = driver });
        Assert.Equal(CrashFixKind.UpdateDriver, fixes[0].Kind);
        Assert.Contains("572.83", fixes[0].Why.Text());
        Assert.Equal("nvlddmkm.sys", fixes[0].Target);

        var pattern = new CrashPattern(CrashPatternKind.AfterDriverInstall, CrashLine.Of("x"), 3, ["t"]) { Subject = "nv_dispi.inf" };
        var rollback = CrashFixRules.Rank(new CrashFixInput { Crash = crash, Patterns = [pattern] });
        Assert.Equal(CrashFixKind.RollBackDriver, rollback[0].Kind);
        Assert.Contains("nv_dispi.inf", rollback[0].Why.Text());
    }

    [Fact]
    public void App_crashes_rank_the_fix_of_their_faulting_module_first()
    {
        Assert.Equal(CrashFixKind.DisableOverlay, CrashFixRules.Rank(new CrashFixInput { Crash = App("d", Now, "game.exe", "DiscordHook64.dll", 0xC0000005), IsGame = true })[0].Kind);
        Assert.Equal(CrashFixKind.RepairDotNet, CrashFixRules.Rank(new CrashFixInput { Crash = App("n", Now, "tool.exe", "KERNELBASE.dll", 0xE0434352) })[0].Kind);

        var runtime = CrashFixRules.Rank(new CrashFixInput { Crash = App("r", Now, "old.exe", "d3dx9_43.dll", 0xC0000005) })[0];
        Assert.Equal(CrashFixKind.ReinstallRuntimes, runtime.Kind);
        Assert.Equal("directx", runtime.Target);

        var missing = CrashFixRules.Rank(new CrashFixInput { Crash = App("m", Now, "game.exe", "game.exe", 0xC0000135), IsGame = true });
        Assert.Equal(CrashFixKind.ScanGameFolder, missing[0].Kind);
        Assert.Contains(missing, f => f.Kind == CrashFixKind.VerifyGameFiles);

        var mod = CrashFixRules.Rank(new CrashFixInput { Crash = App("x", Now, "skyrim.exe", "dxgi.dll", 0xC0000005, @"D:\Games\Skyrim\dxgi.dll"), IsGame = true });
        Assert.Equal(CrashFixKind.VerifyGameFiles, mod[0].Kind);
    }

    [Fact]
    public void Sudden_shutdowns_rank_by_what_Kernel_Power_recorded()
    {
        var loss = new CrashEvent { Id = "s", Kind = CrashKind.SuddenShutdown, Time = Now, EventId = 41, Power = new KernelPowerDetails(0, 0, 0, false, false) };
        Assert.Equal(CrashFixKind.CheckPowerSupply, CrashFixRules.Rank(new CrashFixInput { Crash = loss })[0].Kind);
        Assert.Equal(CrashFixKind.ResetOverclock, CrashFixRules.Rank(new CrashFixInput { Crash = loss, OverclockApplied = true })[0].Kind);
        Assert.True(CrashFixRules.Rank(new CrashFixInput { Crash = loss })[0].IsAdviceOnly);

        var sleep = loss with { Power = new KernelPowerDetails(0, 0, 4, false, false) };
        Assert.Equal(CrashFixKind.UpdateDriver, CrashFixRules.Rank(new CrashFixInput { Crash = sleep })[0].Kind);
        Assert.Contains(CrashFixRules.Rank(new CrashFixInput { Crash = sleep }), f => f.Kind == CrashFixKind.TurnOffFastStartup);
    }

    [Fact]
    public void Disk_errors_before_a_crash_move_the_disk_check_up_and_ties_go_to_the_safer_fix()
    {
        var crash = Bsod("k", Now, 0x50);
        var context = CrashContextRules.Build(crash, [], [new CrashSystemEvent(Now.AddHours(-3), CrashSystemEventKind.DiskError)], [], Limits);
        var fixes = CrashFixRules.Rank(new CrashFixInput { Crash = crash, Context = context });
        Assert.Equal(CrashFixKind.CheckDisk, fixes[0].Kind);

        Assert.All(Enum.GetValues<CrashFixKind>(), k => Assert.False(string.IsNullOrWhiteSpace(CrashFixRules.TitleOf(k))));
    }

    // ------------------------------------------------------------------ DISM and sfc output

    [Theory]
    [InlineData("[==========================62.3%==========================]", 62.3)]
    [InlineData("[=====                      10.0%                          ]", 10.0)]
    [InlineData("Verification 45% complete.", 45.0)]
    [InlineData("Vérification à 45 % effectuée.", 45.0)]
    [InlineData("Überprüfung 100 % abgeschlossen.", 100.0)]
    [InlineData("Doğrulama %37 tamamlandı.", 37.0)]
    [InlineData("Verificación completada en un 12,5%.", 12.5)]
    public void Repair_progress_is_read_from_the_number_next_to_the_percent_sign(string line, double expected) =>
        Assert.Equal(expected, RepairOutputParser.Percent(line));

    [Theory]
    [InlineData("Deployment Image Servicing and Management tool")]
    [InlineData("Version: 10.0.26100.1")]
    [InlineData("Image Version: 10.0.26100.2314")]
    [InlineData("Beginning verification phase of system scan.")]
    [InlineData("")]
    [InlineData("250% nonsense")]
    public void Lines_without_progress_give_none(string line) => Assert.Null(RepairOutputParser.Percent(line));

    [Fact]
    public void The_repair_summary_uses_the_sfc_verdict_and_ignores_text_it_cannot_read()
    {
        Assert.Equal(25, RepairOutputParser.Overall(RepairStep.Dism, 50));
        Assert.Equal(75, RepairOutputParser.Overall(RepairStep.Sfc, 50));
        Assert.Equal(100, RepairOutputParser.Overall(RepairStep.Sfc, 140));

        Assert.Contains("No damaged Windows files", RepairOutputParser.Summary(true, SfcOutcome.NoViolations, true).Text());
        Assert.Contains("Restart the PC", RepairOutputParser.Summary(true, SfcOutcome.Repaired, true).Text());
        Assert.Contains("in-place upgrade", RepairOutputParser.Summary(true, SfcOutcome.CouldNotRepair, true).Text());
        Assert.Contains("did not finish", RepairOutputParser.Summary(false, SfcOutcome.Unknown, false).Text());

        // A German console is not understood; the always-English CBS.log lines decide.
        const string german = "Der Windows-Ressourcenschutz hat keine Integritätsverletzungen gefunden.";
        Assert.Equal(SfcOutcome.Unknown, SfcResultParser.FromConsole(german));
        Assert.Equal(SfcOutcome.NoViolations, SfcResultParser.Interpret(german, ["2026-10-28 12:00:00, Info CSI 00000001 [SR] Verify complete"]));
        Assert.Contains("could not be read", RepairOutputParser.Summary(true, SfcOutcome.Unknown, true).Text());
    }

    // ------------------------------------------------------------------ insights, report, summary and demo

    [Fact]
    public void The_demo_fills_every_section()
    {
        var report = DemoCrashService.Build(Now);
        var data = DemoCrashService.Context(Now, report.Events);
        var contexts = report.Events.Where(CrashPatternRules.IsSerious)
            .ToDictionary(c => c.Id, c => CrashContextRules.Build(c, data.Samples, report.SystemEvents, data.Journal, Limits));
        var patterns = CrashPatternRules.Find(new CrashPatternInput
        {
            Crashes = report.Events, SystemEvents = report.SystemEvents, Updates = data.Updates, Journal = data.Journal, Contexts = contexts,
        });
        CrashInsight Insight(CrashEvent e, bool game = false) => CrashInsightBuilder.Build(e, report.OverclockApplied, contexts.GetValueOrDefault(e.Id), patterns,
            DemoCrashService.DriverDetails(e.Blame?.Module ?? string.Empty), game, report.Events);

        // 0x116: nvlddmkm.sys 566.36, a newer driver, crashes only since that driver was installed.
        var tdr = Insight(report.Events.Single(e => e.BugCheckCode == 0x116 && e.Kind == CrashKind.BlueScreen));
        Assert.Equal("572.83", tdr.Driver!.NewerVersion);
        Assert.Contains(tdr.Patterns, p => p.Kind == CrashPatternKind.AfterDriverInstall);
        Assert.Contains("nvlddmkm.sys", Text(tdr.Details));
        Assert.True(tdr.Context!.HeavyLoad);
        Assert.Contains(tdr.Fixes.Take(2), f => f.Kind is CrashFixKind.UpdateDriver or CrashFixKind.RollBackDriver);

        // 0x124: machine check on core 3, an overclock 20 minutes before, the CPU at 96 °C.
        var whea = Insight(report.Events.Single(e => e.BugCheckCode == 0x124));
        Assert.NotNull(whea.Crash.Whea);
        Assert.Contains("core 3", Text(whea.Details));
        Assert.True(whea.Context!.Overheating);
        Assert.Equal(96, whea.Context.HotTemperature);
        Assert.Equal(3, whea.Context.WheaCorrectedHourBefore);
        Assert.Contains(whea.Patterns, p => p.Kind == CrashPatternKind.AfterPcCheckerChange && p.Strength == 3);
        Assert.Equal(CrashFixKind.ResetOverclock, whea.Fixes[0].Kind);
        Assert.Equal(CauseConfidence.VeryLikely, whea.Causes[0].Confidence);

        // The sudden shutdown: power loss with a disk error the morning before and the uptime.
        var shutdown = Insight(report.Events.Single(e => e.Kind == CrashKind.SuddenShutdown));
        Assert.Contains("lost power", Text(shutdown.Details));
        Assert.Equal(1, shutdown.Context!.DiskErrorsDayBefore);
        Assert.NotNull(shutdown.Context.Uptime);

        // The game crash in the Discord overlay and the .NET program crash.
        var discord = Insight(report.Events.Single(e => e.ModuleName == "DiscordHook64.dll"), game: true);
        Assert.Equal(CrashFixKind.DisableOverlay, discord.Fixes[0].Kind);
        Assert.Equal(4, discord.App!.Crashes + discord.App.Hangs);
        Assert.Equal(["3.0.78.62467", "3.0.77.12210"], discord.App.Versions);
        var dotnet = Insight(report.Events.Single(e => e.ExceptionCode == 0xE0434352));
        Assert.Equal(CrashFixKind.RepairDotNet, dotnet.Fixes[0].Kind);

        var card = CrashSummaryRules.Build(report.Events.Select(e => Insight(e)).ToList(), Now);
        Assert.NotNull(card);
        Assert.NotNull(card!.TopFix);
        Assert.Contains("Most likely cause across", card.Headline.Text());
    }

    [Fact]
    public void The_report_text_has_every_section_and_the_prompt_asks_for_safe_steps()
    {
        var report = DemoCrashService.Build(Now);
        var data = DemoCrashService.Context(Now, report.Events);
        var crash = report.Events.Single(e => e.BugCheckCode == 0x124);
        var context = CrashContextRules.Build(crash, data.Samples, report.SystemEvents, data.Journal, Limits);
        var patterns = CrashPatternRules.Find(new CrashPatternInput { Crashes = report.Events, Journal = data.Journal, SystemEvents = report.SystemEvents });
        var insight = CrashInsightBuilder.Build(crash, true, context, patterns);

        var text = CrashReportText.Build(insight);

        Assert.StartsWith("Blue screen: WHEA_UNCORRECTABLE_ERROR", text);
        foreach (var section in new[] { "What happened", "What the details say", "Probably caused by", "Patterns", "Before the crash", "Try these fixes", "More" })
        {
            Assert.Contains("\n" + section + "\n", text);
        }

        Assert.Contains("Stop code: WHEA_UNCORRECTABLE_ERROR 0x00000124 (0x0000000000000000", text);
        Assert.Contains("1. Reset the overclock:", text);
        Assert.Contains("https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x124---whea-uncorrectable-error", text);
        Assert.StartsWith("My PC had this crash.", CrashReportText.AskPrompt(insight));
        Assert.Contains("Versions that crashed: 2.0, 1.0.", string.Join(" ", CrashReportText.AppLines(new AppCrashStats("a.exe", 2, 0, Now, Now, ["2.0", "1.0"], []))));
    }

    [Fact]
    public void Solved_crashes_leave_the_glance_and_the_summary()
    {
        var settings = new CrashSettings();
        Assert.True(settings.SetSolved("b1", true));
        Assert.False(settings.SetSolved("b1", true));
        Assert.True(settings.IsSolved("b1"));
        var crashes = new List<CrashEvent> { Bsod("b1", Now.AddDays(-1), 0x116), Bsod("b2", Now.AddDays(-2), 0x50) };

        Assert.Equal(1, CrashRules.Glance(crashes, Now, 7, settings.SolvedIds).BlueScreens);
        Assert.Equal(2, CrashRules.Glance(crashes, Now, 7).BlueScreens);
        var insights = crashes.Select(c => CrashInsightBuilder.Build(c, false)).ToList();
        Assert.Equal("b2", CrashSummaryRules.Build(insights, Now, settings.SolvedIds)!.CrashId);
        Assert.True(settings.SetSolved("b1", false));
        Assert.Empty(settings.SolvedIds);

        for (int i = 0; i < CrashSettings.MaxSolved + 5; i++)
        {
            settings.SetSolved("id" + i.ToString(CultureInfo.InvariantCulture), true);
        }

        Assert.Equal(CrashSettings.MaxSolved, settings.SolvedIds.Count);
        Assert.DoesNotContain("id0", settings.SolvedIds);
    }

    [Fact]
    public void App_stats_and_game_matching_use_the_program_path()
    {
        var crashes = new List<CrashEvent>
        {
            App("a", Now.AddDays(-1), "game.exe", "nvwgf2umx.dll", 0xC0000005) with { AppVersion = "2.0" },
            App("b", Now.AddDays(-5), "GAME.exe", "nvwgf2umx.dll", 0xC0000005) with { AppVersion = "1.0" },
            App("c", Now.AddDays(-3), "game.exe", "game.exe", 0xC0000409) with { AppVersion = "2.0" },
        };

        var stats = CrashRules.AppStats(crashes, "game.exe")!;
        Assert.Equal(3, stats.Crashes);
        Assert.Equal(["2.0", "1.0"], stats.Versions);
        Assert.Equal(("nvwgf2umx.dll", 2), stats.TopModules[0]);
        Assert.Null(CrashRules.AppStats(crashes, "other.exe"));

        var games = new List<InstalledGame>
        {
            new("s1", GameLauncher.Steam, "1", "Library", @"C:\Games", null, null, null, null, null, null, null, null),
            new("s2", GameLauncher.Steam, "2", "The Game", @"C:\Games\The Game", null, null, null, null, null, null, null, null),
        };
        Assert.Equal("The Game", CrashGameRules.FindGame(@"c:/games/the game/bin/game.exe", games)!.Name);
        Assert.Null(CrashGameRules.FindGame(@"D:\Other\game.exe", games));
        Assert.Equal(@"D:\Other", CrashGameRules.ScanFolder(@"D:\Other\game.exe", null));
    }

    [Fact]
    public void A_newer_driver_from_the_last_search_is_matched_to_the_device()
    {
        var device = new DriverDeviceRef(@"PCI\VEN_10DE&DEV_2702\4&1", "NVIDIA GeForce RTX 4080 SUPER", "Display", "32.0.15.6636", null);
        var offer = new DriverOffer("n1", DriverSourceKind.Nvidia, "GeForce Game Ready Driver", "572.83", DriverVersionScheme.NvidiaRelease, null, DriverOfferAction.VendorPackage)
        {
            Comparison = DriverVersionComparison.Newer, IsNewest = true, SourceName = "NVIDIA",
        };
        var search = new DriverSourcesResult([new DriverDeviceOffers(device, [offer])], [], [], [], Now);

        Assert.Equal(("572.83", "NVIDIA"), CrashDriverRules.NewerOffer(search, @"pci\ven_10de&dev_2702\4&1", null));
        Assert.Equal(("572.83", "NVIDIA"), CrashDriverRules.NewerOffer(search, null, "NVIDIA GeForce RTX 4080 SUPER"));
        Assert.Null(CrashDriverRules.NewerOffer(search, "other", "other"));
        Assert.True(CrashDriverRules.SearchCovered(search, null, "NVIDIA GeForce RTX 4080 SUPER"));
        Assert.True(CrashDriverRules.IsMicrosoft("Microsoft Corporation", null));
        Assert.False(CrashDriverRules.IsMicrosoft("NVIDIA Corporation", "Microsoft Windows Hardware Compatibility Publisher"));
        Assert.Contains("A newer driver is available: 572.83 (NVIDIA).",
            Text(CrashDriverRules.Describe(new CrashDriverDetails { FileName = "nvlddmkm.sys", NewerVersion = "572.83", NewerSource = "NVIDIA" })));
    }
}
