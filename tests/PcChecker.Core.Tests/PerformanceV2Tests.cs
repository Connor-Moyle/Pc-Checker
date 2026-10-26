using PcChecker.Core.Common;
using PcChecker.Core.Performance;
using PcChecker.Core.Startup;
using Xunit;

namespace PcChecker.Core.Tests;

public class SlowdownRulesV2Tests
{
    private static PerformanceFacts Healthy() => new()
    {
        MemoryUsedPercent = 40,
        MemoryTotalBytes = 32L * 1024 * 1024 * 1024,
        ActivePowerPlan = "High performance",
        Drives = [("C:", 200L * 1024 * 1024 * 1024, 500L * 1024 * 1024 * 1024, "SSD")],
        LastBootSeconds = 20,
        SystemDriveIsSsd = true,
        TrimEnabled = true,
        HasBattery = false,
        GameModeEnabled = true,
    };

    [Fact]
    public void Long_uptime_suggests_a_restart()
    {
        Assert.DoesNotContain(SlowdownRules.Evaluate(Healthy() with { UptimeDays = 2 }), f => f.Id == "system.uptime");
        var f = Assert.Single(SlowdownRules.Evaluate(Healthy() with { UptimeDays = 9 }), f => f.Id == "system.uptime");
        Assert.Equal(Severity.Low, f.Severity);
    }

    [Fact]
    public void Balanced_plan_is_flagged_on_desktops_but_not_laptops()
    {
        Assert.Contains(SlowdownRules.Evaluate(Healthy() with { ActivePowerPlan = "Balanced" }), f => f.Id == "power.balanced.desktop" && f.ActionIds.Contains("power.highperformance"));
        Assert.DoesNotContain(SlowdownRules.Evaluate(Healthy() with { ActivePowerPlan = "Balanced", HasBattery = true }), f => f.Id == "power.balanced.desktop");
        Assert.DoesNotContain(SlowdownRules.Evaluate(Healthy() with { ActivePowerPlan = "Balanced", HasBattery = null }), f => f.Id == "power.balanced.desktop");
    }

    [Fact]
    public void Game_mode_off_fast_startup_and_multiple_antivirus_are_reported()
    {
        var findings = SlowdownRules.Evaluate(Healthy() with { GameModeEnabled = false, FastStartupEnabled = true, ThirdPartyAntivirusCount = 2 });
        Assert.Contains(findings, f => f.Id == "gamemode.off" && f.ActionIds.Contains("gamemode.enable"));
        Assert.Contains(findings, f => f.Id == "faststartup.on.desktop" && f.ActionIds.Contains("faststartup.disable"));
        Assert.Contains(findings, f => f.Id == "security.multipleav" && f.Severity == Severity.Medium);
        Assert.DoesNotContain(SlowdownRules.Evaluate(Healthy() with { ThirdPartyAntivirusCount = 1 }), f => f.Id == "security.multipleav");
    }

    [Fact]
    public void Known_third_party_helper_services_are_listed_once()
    {
        var services = new List<ServiceInfo>
        {
            new("AdobeUpdateService", "Adobe Update Service", "Automatic", "Running", @"C:\Program Files\Adobe\x.exe", false, null),
            new("Bonjour Service", "Bonjour Service", "Automatic", "Running", @"C:\Program Files\Bonjour\mDNSResponder.exe", false, null),
            new("Steam Client Service", "Steam Client Service", "Manual", "Stopped", @"C:\Program Files (x86)\Common Files\Steam\steamservice.exe", false, null),
            new("Audiosrv", "Windows Audio", "Automatic", "Running", @"C:\Windows\System32\svchost.exe -k LocalServiceNetworkRestricted", true, null),
        };
        var f = Assert.Single(SlowdownRules.Evaluate(Healthy() with { Services = services }), f => f.Id == "services.thirdparty");
        Assert.StartsWith("2 third-party", f.Title);
        Assert.Contains("Adobe Update Service", f.Detail);
        Assert.Equal(Severity.Info, f.Severity);
    }

    [Fact]
    public void Commit_pressure_is_reported_when_ram_itself_looks_fine()
    {
        Assert.Contains(SlowdownRules.Evaluate(Healthy() with { CommitUsedPercent = 93 }), f => f.Id == "memory.commit");
        Assert.DoesNotContain(SlowdownRules.Evaluate(Healthy() with { CommitUsedPercent = 60 }), f => f.Id == "memory.commit");
    }
}

public class StartupApprovedCodecTests
{
    [Fact]
    public void Enabled_and_disabled_values_round_trip()
    {
        var enabled = StartupApprovedCodec.Encode(true);
        Assert.Equal(12, enabled.Length);
        Assert.Equal(0x02, enabled[0]);
        Assert.True(StartupApprovedCodec.IsEnabled(enabled));
        Assert.Null(StartupApprovedCodec.DisabledAt(enabled));

        var when = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var disabled = StartupApprovedCodec.Encode(false, when);
        Assert.Equal(0x03, disabled[0]);
        Assert.False(StartupApprovedCodec.IsEnabled(disabled));
        Assert.Equal(when, StartupApprovedCodec.DisabledAt(disabled));
    }

    [Fact]
    public void Windows_own_flags_are_recognised_and_preserved()
    {
        Assert.True(StartupApprovedCodec.IsEnabled([0x06, 0, 0, 0]));
        Assert.False(StartupApprovedCodec.IsEnabled([0x07, 0, 0, 0]));
        Assert.Null(StartupApprovedCodec.IsEnabled(null));
        Assert.Null(StartupApprovedCodec.IsEnabled([]));
        var toggled = StartupApprovedCodec.Toggle([0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], enabled: false, DateTimeOffset.UtcNow);
        Assert.Equal(0x07, toggled[0]);
        Assert.False(StartupApprovedCodec.IsEnabled(toggled));
        Assert.Equal(0x02, StartupApprovedCodec.Toggle([0x03], enabled: true)[0]);
    }
}

public class PerformanceTextParsersTests
{
    [Theory]
    [InlineData("NTFS DisableDeleteNotify = 0  (Disabled)\r\nReFS DisableDeleteNotify = 0  (Disabled)", true)]
    [InlineData("NTFS DisableDeleteNotify = 1  (Enabled)\nReFS DisableDeleteNotify = 0  (Disabled)", false)]
    [InlineData("DisableDeleteNotify = 0", true)]
    [InlineData("DisableDeleteNotify = 1", false)]
    [InlineData("", null)]
    [InlineData("The FSUTIL utility requires that you have administrative privileges.", null)]
    public void Trim_state_is_read_from_fsutil(string output, bool? expected) =>
        Assert.Equal(expected, PerformanceTextParsers.ParseTrimEnabled(output));

    [Fact]
    public void Boot_time_converts_milliseconds_to_seconds()
    {
        Assert.Equal(18.4, PerformanceTextParsers.BootTimeToSeconds(18_412));
        Assert.Null(PerformanceTextParsers.BootTimeToSeconds(0));
        Assert.Null(PerformanceTextParsers.BootTimeToSeconds(null));
        Assert.True(PerformanceTextParsers.TryParseBootTimeMs("\r\n45120\r\n", out long ms));
        Assert.Equal(45120, ms);
        Assert.False(PerformanceTextParsers.TryParseBootTimeMs("Get-WinEvent : No events were found", out _));
    }

    [Fact]
    public void Update_check_age_and_uptime_are_computed()
    {
        var now = new DateTimeOffset(2025, 10, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(10, PerformanceTextParsers.DaysSinceUpdateCheck("2025-10-10 06:12:11", now));
        Assert.Equal(0, PerformanceTextParsers.DaysSinceUpdateCheck("2025-10-21 06:12:11", now));
        Assert.Null(PerformanceTextParsers.DaysSinceUpdateCheck("never", now));
        Assert.Null(PerformanceTextParsers.DaysSinceUpdateCheck(null, now));
        Assert.Equal(1.5, PerformanceTextParsers.UptimeDays(36 * 60 * 60 * 1000L));
        Assert.Equal("1 day 12 h", PerformanceTextParsers.FormatUptime(TimeSpan.FromHours(36)));
        Assert.Equal("5 h 12 min", PerformanceTextParsers.FormatUptime(new TimeSpan(5, 12, 0)));
        Assert.Equal("SSD", PerformanceTextParsers.MediaTypeName(4));
        Assert.Equal("HDD", PerformanceTextParsers.MediaTypeName(3));
        Assert.Equal("Unknown", PerformanceTextParsers.MediaTypeName(null));
    }
}

public class ServiceClassifierTests
{
    [Theory]
    [InlineData(@"C:\Windows\system32\svchost.exe -k LocalServiceNetworkRestricted -p", true)]
    [InlineData(@"""C:\Windows\System32\SearchIndexer.exe"" /Embedding", true)]
    [InlineData(@"%SystemRoot%\System32\svchost.exe -k netsvcs", true)]
    [InlineData(@"""C:\Program Files\Bonjour\mDNSResponder.exe""", false)]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\nvdisplay.inf_amd64_123\Display.NvContainer\NVDisplay.Container.exe -s NVDisplay.ContainerLocalSystem", false)]
    [InlineData(@"""C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"" --service", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Microsoft_services_are_recognised_by_path(string? path, bool expected) =>
        Assert.Equal(expected, ServiceClassifier.IsMicrosoft(path, @"C:\Windows"));

    [Fact]
    public void Start_modes_normalize_and_map_to_sc_arguments()
    {
        Assert.Equal("Automatic", ServiceClassifier.NormalizeStartMode("Auto", false));
        Assert.Equal("Automatic (Delayed)", ServiceClassifier.NormalizeStartMode("Auto", true));
        Assert.Equal("Manual", ServiceClassifier.NormalizeStartMode("Manual", false));
        Assert.Equal("Disabled", ServiceClassifier.NormalizeStartMode("Disabled", false));
        Assert.Equal("Unknown", ServiceClassifier.NormalizeStartMode(null, false));
        Assert.Equal("auto", ServiceClassifier.ToScArgument("Automatic"));
        Assert.Equal("delayed-auto", ServiceClassifier.ToScArgument("Automatic (Delayed)"));
        Assert.Equal("demand", ServiceClassifier.ToScArgument("Manual"));
        Assert.Equal("disabled", ServiceClassifier.ToScArgument("Disabled"));
        Assert.Null(ServiceClassifier.ToScArgument("Boot"));
        Assert.True(ServiceClassifier.IsAutomatic("Automatic (Delayed)"));
        Assert.False(ServiceClassifier.IsAutomatic("Manual"));
        Assert.True(ServiceClassifier.IsProtected("RpcSs"));
        Assert.False(ServiceClassifier.IsProtected("Bonjour Service"));
    }

    [Fact]
    public void Known_hogs_are_matched_only_for_third_party_services()
    {
        Assert.NotNull(ServiceClassifier.KnownHogReason(new ServiceInfo("AdobeUpdateService", "Adobe Update Service", "Automatic", "Running", null, false, null)));
        Assert.Null(ServiceClassifier.KnownHogReason(new ServiceInfo("Teams", "Microsoft Teams", "Automatic", "Running", @"C:\Windows\x.exe", true, null)));
        Assert.Null(ServiceClassifier.KnownHogReason(new ServiceInfo("Audiosrv", "Windows Audio", "Automatic", "Running", null, false, null)));
    }
}

public class ProcessRulesTests
{
    [Fact]
    public void Cpu_percent_is_relative_to_all_logical_processors()
    {
        Assert.Equal(25, ProcessRules.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 2));
        Assert.Equal(100, ProcessRules.CpuPercent(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), 2));
        Assert.Equal(0, ProcessRules.CpuPercent(TimeSpan.Zero, TimeSpan.FromSeconds(2), 8));
        Assert.Equal(0, ProcessRules.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.Zero, 8));
    }

    [Fact]
    public void Windows_processes_are_protected_and_kill_refuses_them()
    {
        Assert.True(ProcessRules.IsProtected("csrss", 612));
        Assert.True(ProcessRules.IsProtected("svchost.exe", 1200));
        Assert.True(ProcessRules.IsProtected("anything", 4));
        Assert.False(ProcessRules.IsProtected("chrome", 9120));
        var result = ProcessRules.TryKill(4, "System");
        Assert.False(result.Success);
        Assert.Contains("part of Windows", result.Message);
    }

    [Fact]
    public void Top_selection_merges_cpu_and_memory_leaders_without_duplicates()
    {
        var processes = Enumerable.Range(1, 30).Select(i => new ProcessInfo(i, $"p{i}", i, (31 - i) * 10_000_000L, null, null)).ToList();
        var top = ProcessRules.SelectTop(processes, 5);
        Assert.Equal(10, top.Count);
        Assert.Equal(top.Count, top.Select(p => p.Pid).Distinct().Count());
        Assert.Equal(30, top[0].Pid);
        Assert.Contains(top, p => p.Pid == 1);
    }
}

public class PerformanceReportFormatterTests
{
    private static PerformanceReport Sample() => new(72,
        [new("power.saver", FindingCategory.Power, Severity.Medium, "Power saver plan is active", "Detail text", "Lower performance", ["power.balanced"], false)],
        [new("Steam", "steam.exe", "HKCU\\Run", true, "Valve", "Registry")],
        [new("Bonjour Service", "Bonjour Service", "Automatic", "Running", @"C:\Program Files\Bonjour\mDNSResponder.exe", false, null)],
        [new(100, "chrome", 12.5, 2_000_000_000, null, "Google")],
        18.4, 42, 32L * 1024 * 1024 * 1024, "Power saver",
        [("C:", 100L * 1024 * 1024 * 1024, 500L * 1024 * 1024 * 1024, "SSD")], new DateTimeOffset(2025, 10, 20, 9, 30, 0, TimeSpan.Zero), ["note one"]);

    [Fact]
    public void Markdown_contains_score_findings_startup_and_drives()
    {
        var md = PerformanceReportFormatter.BuildMarkdown(Sample());
        Assert.Contains("**72/100**", md);
        Assert.Contains("### [Medium] Power saver plan is active", md);
        Assert.Contains("| Steam | Valve | Registry | yes |", md);
        Assert.Contains("Drive C: (SSD)", md);
        Assert.Contains("Bonjour Service (Running)", md);
        Assert.Contains("chrome (PID 100) | 12.5% |", md);
        Assert.Contains("- note one", md);
    }

    [Fact]
    public void Json_is_valid_and_carries_the_score()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(PerformanceReportFormatter.BuildJson(Sample()));
        Assert.Equal(72, doc.RootElement.GetProperty("Score").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("Findings").GetArrayLength());
        Assert.Equal("Medium", doc.RootElement.GetProperty("Findings")[0].GetProperty("Severity").GetString());
    }

    [Fact]
    public async Task Demo_analyzer_runs_the_real_rules()
    {
        var report = await new DemoPerformanceAnalyzer(new DemoStartupService()).AnalyzeAsync(null, CancellationToken.None);
        Assert.Contains(report.Findings, f => f.Id == "startup.several");
        Assert.Contains(report.Findings, f => f.Id == "power.balanced.desktop");
        Assert.Contains(report.Findings, f => f.Id == "system.uptime");
        Assert.Contains(report.Findings, f => f.Id == "services.thirdparty");
        Assert.Contains(report.Findings, f => f.Id == "memory.xmp");
        Assert.Equal(SlowdownRules.Score(report.Findings), report.Score);
        Assert.InRange(report.Score, 50, 95);
        Assert.True(report.Services.Count >= 10);
        Assert.Equal(report.Findings.OrderByDescending(f => f.Severity).Select(f => f.Severity), report.Findings.Select(f => f.Severity));
    }
}

public class SlowdownRulesFactsTests
{
    private static PerformanceFacts Healthy() => new()
    {
        MemoryUsedPercent = 40,
        MemoryTotalBytes = 32L * 1024 * 1024 * 1024,
        ActivePowerPlan = "High performance",
        Drives = [("C:", 200L * 1024 * 1024 * 1024, 500L * 1024 * 1024 * 1024, "SSD")],
        LastBootSeconds = 20,
        SystemDriveIsSsd = true,
        TrimEnabled = true,
        HasBattery = false,
        GameModeEnabled = true,
        PageFileAutomatic = true,
        PageFileBytes = 4L * 1024 * 1024 * 1024,
    };

    [Fact]
    public void Page_file_off_or_tiny_is_reported_only_when_managed_by_hand()
    {
        var off = Assert.Single(SlowdownRules.Evaluate(Healthy() with { PageFileAutomatic = false, PageFileBytes = 0 }), f => f.Id == "pagefile.off");
        Assert.Equal(Severity.Medium, off.Severity);
        Assert.DoesNotContain(SlowdownRules.Evaluate(Healthy() with { PageFileAutomatic = true, PageFileBytes = 0 }), f => f.Id.StartsWith("pagefile."));
        Assert.DoesNotContain(SlowdownRules.Evaluate(Healthy() with { PageFileAutomatic = null, PageFileBytes = null }), f => f.Id.StartsWith("pagefile."));

        var small = Healthy() with { PageFileAutomatic = false, PageFileBytes = 256L * 1024 * 1024, MemoryTotalBytes = 8L * 1024 * 1024 * 1024 };
        Assert.Contains(SlowdownRules.Evaluate(small), f => f.Id == "pagefile.small" && f.Severity == Severity.Low);
        Assert.DoesNotContain(SlowdownRules.Evaluate(small with { MemoryTotalBytes = 64L * 1024 * 1024 * 1024 }), f => f.Id == "pagefile.small");
    }

    [Fact]
    public void Transparency_and_game_bar_link_to_their_optimizations()
    {
        var smallPc = Healthy() with { MemoryTotalBytes = 4L * 1024 * 1024 * 1024, TransparencyEnabled = true, VisualEffectsBestAppearance = false };
        Assert.Contains(SlowdownRules.Evaluate(smallPc), f => f.Id == "visuals.transparency" && f.ActionIds.Contains("transparency.disable"));
        Assert.DoesNotContain(SlowdownRules.Evaluate(smallPc with { VisualEffectsBestAppearance = true }), f => f.Id == "visuals.transparency");
        Assert.DoesNotContain(SlowdownRules.Evaluate(Healthy() with { TransparencyEnabled = true }), f => f.Id == "visuals.transparency");

        var bar = Assert.Single(SlowdownRules.Evaluate(Healthy() with { GameBarEnabled = true }), f => f.Id == "gamebar.on");
        Assert.Equal(Severity.Info, bar.Severity);
        Assert.Contains("gamebar.disable", bar.ActionIds);
        Assert.DoesNotContain(SlowdownRules.Evaluate(Healthy() with { GameBarEnabled = false }), f => f.Id == "gamebar.on");
    }

    [Fact]
    public void Boot_time_is_read_from_wevtutil_xml_and_uptime_falls_back_to_last_boot()
    {
        const string xml = "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><EventID>100</EventID></System>"
                           + "<EventData><Data Name='BootTsVersion'>2</Data><Data Name='BootTime'>41235</Data><Data Name='MainPathBootTime'>22000</Data></EventData></Event>";
        Assert.True(PerformanceTextParsers.TryParseBootTimeFromEventXml(xml, out long ms));
        Assert.Equal(41235, ms);
        Assert.True(PerformanceTextParsers.TryParseBootTimeFromEventXml("<Data Name=\"BootTime\">18412</Data>", out ms));
        Assert.Equal(18412, ms);
        Assert.False(PerformanceTextParsers.TryParseBootTimeFromEventXml("<Data Name='BootTime'>0</Data>", out _));
        Assert.False(PerformanceTextParsers.TryParseBootTimeFromEventXml("No events were found", out _));
        Assert.False(PerformanceTextParsers.TryParseBootTimeFromEventXml(null, out _));

        var now = new DateTime(2025, 10, 20, 12, 0, 0);
        Assert.Equal(TimeSpan.FromHours(36), PerformanceTextParsers.UptimeFrom(36 * 60 * 60 * 1000L, null, now));
        Assert.Equal(TimeSpan.FromDays(2), PerformanceTextParsers.UptimeFrom(0, now.AddDays(-2), now));
        Assert.Null(PerformanceTextParsers.UptimeFrom(0, null, now));
        Assert.Null(PerformanceTextParsers.UptimeFrom(-1, now.AddDays(1), now));
    }

    [Fact]
    public void Start_mode_options_always_contain_the_current_mode()
    {
        Assert.Equal(ServiceClassifier.StartModes, ServiceClassifier.StartModeOptions("Manual"));
        Assert.Equal(ServiceClassifier.StartModes, ServiceClassifier.StartModeOptions(null));
        var boot = ServiceClassifier.StartModeOptions("Boot");
        Assert.Equal("Boot", boot[0]);
        Assert.Equal(ServiceClassifier.StartModes.Count + 1, boot.Count);
        Assert.True(ServiceClassifier.IsAutomaticScMode("delayed-auto"));
        Assert.False(ServiceClassifier.IsAutomaticScMode("demand"));
    }

    [Fact]
    public void Demo_report_carries_uptime_and_report_export_shows_it()
    {
        var report = new PerformanceReport(90, [], [], [], [], 18.4, 42, 32L * 1024 * 1024 * 1024, "Balanced", [], DateTimeOffset.Now, []) { Uptime = TimeSpan.FromDays(9.3) };
        Assert.Contains("| Uptime | 9 days 7 h |", PerformanceReportFormatter.BuildMarkdown(report));
        Assert.Contains("\"UptimeSeconds\"", PerformanceReportFormatter.BuildJson(report));
        Assert.Contains("| Uptime | unknown |", PerformanceReportFormatter.BuildMarkdown(report with { Uptime = null }));
    }
}
