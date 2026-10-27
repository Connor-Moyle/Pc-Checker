using System.Text.Json;
using System.Text.Json.Serialization;
using PcChecker.Core.Accessibility;
using PcChecker.Core.Common;
using PcChecker.Core.Processes;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>
/// The Task Manager grade Processes page: grouping into Apps, Background and Windows processes, power usage, heat map,
/// affinity masks, column settings, hung and suspended decisions, safety rules and command line helpers.
/// </summary>
public class TaskManagerProcessesTests
{
    private const string Windows = @"C:\Windows";
    private const int OwnPid = 9120;
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly SignatureInfo Microsoft = new(SignatureState.Signed, "Microsoft Windows");
    private static readonly SignatureInfo Vendor = new(SignatureState.Signed, "Contoso");

    private static ProcessInfo P(int pid, string name, string? path, int parent = 1, double cpu = 0, long memoryMb = 10, bool window = false,
        DateTimeOffset? start = null, SignatureInfo? signature = null, double? gpu = null, double read = 0, double write = 0,
        ProcessRunState state = ProcessRunState.Running, string user = "Alex", int? session = 1, int? connections = null,
        bool efficiency = false, string? commandLine = null) =>
        new(pid, parent, name, path, user, start ?? T0.AddMinutes(pid / 100.0), cpu, memoryMb * ByteSize.Megabyte, read, write, gpu,
            ProcessPriority.Normal, signature ?? Vendor)
        {
            WindowHandle = window ? 0x1000 + pid : 0,
            WindowTitle = window ? name + " window" : null,
            State = state,
            SessionId = session,
            ConnectionCount = connections,
            EfficiencyMode = efficiency,
            CommandLine = commandLine,
        };

    // ------------------------------------------------------------------ categories and grouping

    [Fact]
    public void Processes_are_sorted_into_apps_background_and_windows_like_task_manager()
    {
        Assert.Equal(ProcessCategory.App, ProcessGrouping.Classify(P(10, "notepad.exe", @"C:\Windows\System32\notepad.exe", window: true, signature: Microsoft), Windows));
        Assert.Equal(ProcessCategory.Windows, ProcessGrouping.Classify(P(11, "svchost.exe", @"C:\Windows\System32\svchost.exe", signature: Microsoft), Windows));
        Assert.Equal(ProcessCategory.Windows, ProcessGrouping.Classify(P(12, "Registry", null, signature: SignatureInfo.Unknown), Windows));
        Assert.Equal(ProcessCategory.Windows, ProcessGrouping.Classify(P(4, "System", null, signature: SignatureInfo.Unknown), Windows));
        Assert.Equal(ProcessCategory.Windows, ProcessGrouping.Classify(P(13, "unknownhost.exe", @"C:\Windows\System32\unknownhost.exe", signature: Microsoft), Windows));

        // Drivers' helpers in DriverStore and third-party programs run in the background, even from the Windows folder.
        Assert.Equal(ProcessCategory.Background, ProcessGrouping.Classify(
            P(14, "NVDisplay.Container.exe", @"C:\Windows\System32\DriverStore\FileRepository\nv.inf\NVDisplay.Container.exe", signature: new SignatureInfo(SignatureState.Signed, "NVIDIA Corporation")), Windows));
        Assert.Equal(ProcessCategory.Background, ProcessGrouping.Classify(P(15, "RtkAudio.exe", @"C:\Windows\System32\RtkAudio.exe"), Windows));
        Assert.Equal(ProcessCategory.Background, ProcessGrouping.Classify(P(16, "steam.exe", @"C:\Program Files (x86)\Steam\steam.exe"), Windows));
    }

    [Fact]
    public void An_app_collects_its_helpers_but_a_game_it_started_stays_its_own_app()
    {
        const string steamDir = @"C:\Program Files (x86)\Steam\";
        var steam = P(100, "steam.exe", steamDir + "steam.exe", parent: 50, window: true, start: T0);
        var helper = P(110, "steamwebhelper.exe", steamDir + "steamwebhelper.exe", parent: 100, start: T0.AddSeconds(5));
        var helperChild = P(120, "steamwebhelper.exe", steamDir + "steamwebhelper.exe", parent: 110, start: T0.AddSeconds(6));
        var game = P(130, "game.exe", @"D:\Games\game.exe", parent: 100, window: true, start: T0.AddMinutes(3));
        var crashReporter = P(140, "crash.exe", @"D:\Games\crash.exe", parent: 130, start: T0.AddMinutes(3.1));
        var explorer = P(50, "explorer.exe", @"C:\Windows\explorer.exe", parent: 40, window: true, start: T0.AddMinutes(-1), signature: Microsoft);
        var oneDrive = P(150, "OneDrive.exe", @"C:\Users\Alex\AppData\Local\Microsoft\OneDrive\OneDrive.exe", parent: 50, start: T0);
        var conhost = P(160, "conhost.exe", @"C:\Windows\System32\conhost.exe", parent: 130, start: T0.AddMinutes(4), signature: Microsoft);

        // A "child" that started before its parent belongs to a recycled process id, not to Steam.
        var orphan = P(170, "updater.exe", @"C:\Tools\updater.exe", parent: 100, start: T0.AddMinutes(-30));

        var groups = ProcessGrouping.Build([steam, helper, helperChild, game, crashReporter, explorer, oneDrive, conhost, orphan], Windows);
        var steamGroup = groups.Single(g => g.Members.Contains(steam));
        Assert.Equal(ProcessCategory.App, steamGroup.Category);
        Assert.Equal([100, 110, 120], steamGroup.Members.Select(m => m.Pid).Order());
        Assert.Same(steam, steamGroup.Lead);

        var gameGroup = groups.Single(g => g.Members.Contains(game));
        Assert.Equal([130, 140], gameGroup.Members.Select(m => m.Pid).Order());

        // File Explorer does not adopt everything started from the desktop; Windows processes are never adopted.
        Assert.Single(groups.Single(g => g.Members.Contains(explorer)).Members);
        Assert.Equal(ProcessCategory.Background, groups.Single(g => g.Members.Contains(oneDrive)).Category);
        Assert.Equal(ProcessCategory.Windows, groups.Single(g => g.Members.Contains(conhost)).Category);
        Assert.Equal(ProcessCategory.Background, groups.Single(g => g.Members.Contains(orphan)).Category);
    }

    [Fact]
    public void Processes_view_lists_sections_in_task_manager_order_with_counts()
    {
        var list = new[]
        {
            P(4, "System", null, signature: SignatureInfo.Unknown, cpu: 0.25),
            P(11, "svchost.exe", @"C:\Windows\System32\svchost.exe", signature: Microsoft, cpu: 0.1),
            P(12, "svchost.exe", @"C:\Windows\System32\svchost.exe", signature: Microsoft, cpu: 0.2),
            P(20, "chrome.exe", @"C:\Chrome\chrome.exe", window: true, cpu: 3, start: T0),
            P(21, "chrome.exe", @"C:\Chrome\chrome.exe", parent: 20, cpu: 2, start: T0.AddSeconds(1)),
            P(30, "helper.exe", @"C:\Tools\helper.exe", cpu: 1),
        };

        var collapsed = ProcessGrouping.Arrange(list, Windows, ProcessSortColumn.Cpu, descending: true);
        Assert.Equal(["section:App", "group:" + @"C:\Chrome\chrome.exe", "section:Background", list[5].Key, "section:Windows", "group:svchost-or-path", list[0].Key],
            collapsed.Select(l => l.IsGroup && l.Group!.Title == "Service Host" ? "group:svchost-or-path" : l.Key));
        Assert.Equal(1, collapsed[0].SectionCount);
        Assert.Equal(2, collapsed[4].SectionCount);
        Assert.Equal(5, collapsed[1].Group!.CpuPercent, 3);

        var expanded = ProcessGrouping.Arrange(list, Windows, ProcessSortColumn.Cpu, true, new HashSet<string> { @"C:\Chrome\chrome.exe" });
        Assert.Equal([20, 21], expanded.Where(l => l.IsChild).Select(l => l.Process!.Pid));

        // Empty sections are left out.
        Assert.DoesNotContain(ProcessGrouping.Arrange([list[5]], Windows, ProcessSortColumn.Name, false), l => l.Section == ProcessCategory.App);
    }

    // ------------------------------------------------------------------ power usage and heat map

    [Fact]
    public void Power_usage_weighs_processor_graphics_and_disk_into_five_levels()
    {
        Assert.Equal(0, PowerUsageRules.Score(0, null, 0));
        Assert.Equal(10 + 6 + 3, PowerUsageRules.Score(10, 10, 10 * 1024 * 1024), 3);
        Assert.Equal(15, PowerUsageRules.Score(0, null, 500.0 * 1024 * 1024), 3); // disk capped at 50 MB/s
        Assert.Equal(PowerUsage.VeryLow, PowerUsageRules.Level(0.2));
        Assert.Equal(PowerUsage.Low, PowerUsageRules.Level(1));
        Assert.Equal(PowerUsage.Moderate, PowerUsageRules.Level(5));
        Assert.Equal(PowerUsage.High, PowerUsageRules.Level(12));
        Assert.Equal(PowerUsage.VeryHigh, PowerUsageRules.Level(70));
        Assert.Equal(PowerUsage.VeryLow, PowerUsageRules.Level(double.NaN));
        Assert.Equal("Very high", PowerUsageRules.Label(PowerUsage.VeryHigh));
    }

    [Fact]
    public void Power_trend_averages_over_two_minutes_and_forgets_ended_programs()
    {
        var tracker = new PowerTrendTracker();
        tracker.Update(new Dictionary<string, double> { ["a"] = 40 }, TimeSpan.Zero);
        Assert.Equal(40, tracker.Get("a"));

        // A burst of 0 for one second barely moves a two-minute average.
        tracker.Update(new Dictionary<string, double> { ["a"] = 0, ["b"] = 5 }, TimeSpan.FromSeconds(1));
        Assert.InRange(tracker.Get("a")!.Value, 39, 40);
        Assert.Equal(5, tracker.Get("b"));

        tracker.Update(new Dictionary<string, double> { ["b"] = 5 }, TimeSpan.FromSeconds(1));
        Assert.Null(tracker.Get("a"));
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void Heat_map_levels_grow_with_use_and_the_tint_keeps_text_readable_in_both_themes()
    {
        Assert.Equal(0, ProcessHeatMap.Level(HeatMetric.Cpu, 0.4));
        Assert.Equal(1, ProcessHeatMap.Level(HeatMetric.Cpu, 2));
        Assert.Equal(4, ProcessHeatMap.Level(HeatMetric.Cpu, 90));
        Assert.Equal(3, ProcessHeatMap.Level(HeatMetric.Memory, 2L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024)); // 12.5 %
        Assert.Equal(0, ProcessHeatMap.Level(HeatMetric.Memory, 1000, 0));
        Assert.Equal(2, ProcessHeatMap.Level(HeatMetric.Disk, 2 * 1024 * 1024));
        Assert.Equal(4, ProcessHeatMap.Level(HeatMetric.Power, (int)PowerUsage.VeryHigh));
        Assert.Equal(0, ProcessHeatMap.Opacity(0));
        Assert.True(ProcessHeatMap.Opacity(4) > ProcessHeatMap.Opacity(1));

        // Strongest tint over a card, with the theme's normal text colour on top.
        var cases = new[]
        {
            (Variant: PaletteVariant.Dark, Text: ThemeColor.Parse("#FFFFFF")),
            (Variant: PaletteVariant.Light, Text: ThemeColor.Parse("#E4000000")),
        };
        foreach (var (variant, text) in cases)
        {
            var orange = AccentPalette.Accent(AccentRole.Orange, variant);
            foreach (var surface in AccentPalette.Surfaces(variant))
            {
                var tint = new ThemeColor((byte)Math.Round(ProcessHeatMap.Opacity(ProcessHeatMap.MaxLevel) * 255), orange.R, orange.G, orange.B).Over(surface);
                Assert.True(ContrastMath.MeetsText(text.Over(tint), tint), $"{variant} on {surface}");
            }
        }
    }

    // ------------------------------------------------------------------ affinity

    [Fact]
    public void Affinity_masks_round_trip_and_describe_ranges()
    {
        Assert.Equal(0xFFUL, AffinityMath.FullMask(8));
        Assert.Equal(ulong.MaxValue, AffinityMath.FullMask(64));
        Assert.Equal(0UL, AffinityMath.FullMask(0));
        ulong mask = AffinityMath.ToMask([0, 1, 2, 3, 8, 70, -1]);
        Assert.Equal(0x10FUL, mask);
        Assert.Equal([0, 1, 2, 3, 8], AffinityMath.Processors(mask, 16));
        Assert.Equal("CPU 0\u20133, 8", AffinityMath.Describe(mask, 16));
        Assert.Equal("All 16 processors", AffinityMath.Describe(AffinityMath.FullMask(16), 16));
        Assert.Equal("CPU 5", AffinityMath.Describe(1UL << 5, 16));

        Assert.Null(AffinityMath.Validate(mask, 16));
        Assert.Contains("at least one", AffinityMath.Validate(0, 16));
        Assert.Contains("does not have", AffinityMath.Validate(1UL << 20, 16));
        Assert.Contains("64", AffinityMath.Validate(1, 128));
    }

    // ------------------------------------------------------------------ columns and settings

    [Fact]
    public void Column_settings_fall_back_to_defaults_and_always_keep_the_name()
    {
        Assert.Equal(["Name", "Status", "Cpu", "Memory", "Disk", "Network", "Gpu", "Power"], ProcessColumns.Resolve(null, detailsView: false));
        Assert.Contains("Pid", ProcessColumns.Resolve(null, detailsView: true));
        Assert.Contains("Architecture", ProcessColumns.Resolve(null, detailsView: true));

        // Unknown ids (from another version) and duplicates are dropped; order follows the catalogue; Name is forced.
        Assert.Equal(["Name", "Pid", "Cpu"], ProcessColumns.Resolve(["cpu", "Bogus", "PID", "Cpu"], detailsView: false));
        Assert.Equal(["Name"], ProcessColumns.Resolve([], detailsView: false));

        var shown = ProcessColumns.Toggle(["Name", "Cpu"], "Threads", show: true);
        Assert.Equal(["Name", "Cpu", "Threads"], shown);
        Assert.Equal(["Name", "Threads"], ProcessColumns.Toggle(shown, "Cpu", show: false));
        Assert.Equal(["Name", "Cpu"], ProcessColumns.Toggle(["Name", "Cpu"], "Name", show: false));
        Assert.Equal(["Name", "Cpu"], ProcessColumns.Toggle(["Name", "Cpu"], "Nope", show: true));

        // Every column id is a sort key, so clicking any column title sorts.
        Assert.All(ProcessColumns.All, c => Assert.True(ProcessSorting.TryParseColumn(c.Id, out _), c.Id));
    }

    [Fact]
    public void Processes_settings_survive_a_save_and_a_broken_file()
    {
        var settings = AppSettings.CreateDefault();
        settings.Processes.DetailsView = true;
        settings.Processes.UpdateSpeed = ProcessUpdateSpeed.Low;
        settings.Processes.DetailsColumns = ["Name", "Pid", "Threads"];
        settings.Processes.RecentRunCommands = CommandLineText.AddRecent(["notepad"], "cmd");

        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, options), options)!;
        Assert.True(loaded.Processes.DetailsView);
        Assert.Equal(ProcessUpdateSpeed.Low, loaded.Processes.UpdateSpeed);
        Assert.Equal(["Name", "Pid", "Threads"], loaded.Processes.DetailsColumns);
        Assert.Null(loaded.Processes.ProcessesColumns);
        Assert.Equal(["cmd", "notepad"], loaded.Processes.RecentRunCommands);

        var broken = AppSettings.CreateDefault();
        broken.Processes = null!;
        var repaired = JsonSettingsService.Repair(broken);
        Assert.NotNull(repaired.Processes);
        Assert.Equal(ProcessUpdateSpeed.Normal, repaired.Processes.UpdateSpeed);
        Assert.NotNull(repaired.Processes.RecentRunCommands);

        Assert.Equal(TimeSpan.FromMilliseconds(500), UpdateSpeedRules.Interval(ProcessUpdateSpeed.High));
        Assert.Equal(TimeSpan.FromSeconds(1), UpdateSpeedRules.Interval(ProcessUpdateSpeed.Normal));
        Assert.Equal(TimeSpan.FromSeconds(4), UpdateSpeedRules.Interval(ProcessUpdateSpeed.Low));
        Assert.Null(UpdateSpeedRules.Interval(ProcessUpdateSpeed.Paused));
    }

    // ------------------------------------------------------------------ status: suspended and not responding

    [Fact]
    public void Suspended_means_every_thread_waits_suspended()
    {
        const int Waiting = ProcessStatusRules.ThreadStateWaiting;
        const int Suspended = ProcessStatusRules.WaitReasonSuspended;
        Assert.True(ProcessStatusRules.IsSuspended([(Waiting, Suspended), (Waiting, Suspended)]));
        Assert.False(ProcessStatusRules.IsSuspended([(Waiting, Suspended), (Waiting, 6)]));   // one thread waits for input
        Assert.False(ProcessStatusRules.IsSuspended([(Waiting, Suspended), (2, 0)]));         // one thread is running
        Assert.False(ProcessStatusRules.IsSuspended([]));                                     // exiting, not suspended
    }

    [Fact]
    public void Not_responding_needs_a_hung_window_a_failed_ping_and_two_readings_in_a_row()
    {
        Assert.Equal(ProcessRunState.NotResponding, ProcessStatusRules.Decide(false, true, true, true));
        Assert.Equal(ProcessRunState.Running, ProcessStatusRules.Decide(false, true, false, true));
        Assert.Equal(ProcessRunState.Running, ProcessStatusRules.Decide(false, true, true, false));
        Assert.Equal(ProcessRunState.Suspended, ProcessStatusRules.Decide(true, true, true, true)); // Resume is the fix, not End task

        var tracker = new HungWindowTracker();
        Assert.Empty(tracker.Update(new Dictionary<string, bool> { ["app"] = true }));
        Assert.Equal(["app"], tracker.Update(new Dictionary<string, bool> { ["app"] = true }));
        Assert.Empty(tracker.Update(new Dictionary<string, bool> { ["app"] = false }));       // one good reading clears it
        Assert.Empty(tracker.Update(new Dictionary<string, bool> { ["app"] = true }));
        Assert.Empty(tracker.Update(new Dictionary<string, bool>()));                         // window gone: forgotten
        Assert.Empty(tracker.Update(new Dictionary<string, bool> { ["app"] = true }));
    }

    [Fact]
    public void End_task_closes_windows_first_unless_the_program_cannot_react()
    {
        Assert.Equal(EndTaskPlan.CloseWindowsFirst, ProcessStatusRules.PlanEndTask(hasWindow: true, ProcessRunState.Running));
        Assert.Equal(EndTaskPlan.EndNow, ProcessStatusRules.PlanEndTask(hasWindow: true, ProcessRunState.NotResponding));
        Assert.Equal(EndTaskPlan.EndNow, ProcessStatusRules.PlanEndTask(hasWindow: true, ProcessRunState.Suspended));
        Assert.Equal(EndTaskPlan.EndNow, ProcessStatusRules.PlanEndTask(hasWindow: false, ProcessRunState.Running));

        var running = P(1, "a.exe", null);
        var hung = P(2, "a.exe", null, state: ProcessRunState.NotResponding);
        var paused = P(3, "a.exe", null, state: ProcessRunState.Suspended);
        Assert.Equal(ProcessRunState.NotResponding, ProcessStatusRules.GroupState([running, hung, paused]));
        Assert.Equal(ProcessRunState.Running, ProcessStatusRules.GroupState([running, paused]));
        Assert.Equal(ProcessRunState.Suspended, ProcessStatusRules.GroupState([paused]));
        Assert.Equal("Efficiency mode", ProcessStatusRules.Label(ProcessRunState.Running, efficiencyMode: true));
        Assert.Equal("Suspended", ProcessStatusRules.Label(ProcessRunState.Suspended, efficiencyMode: true));
        Assert.Equal(string.Empty, ProcessStatusRules.Label(ProcessRunState.Running, efficiencyMode: false));
    }

    [Fact]
    public void Efficiency_mode_uses_ecoqos_and_low_priority_like_task_manager()
    {
        Assert.True(EfficiencyModeRules.IsOn(1, 1));
        Assert.False(EfficiencyModeRules.IsOn(1, 0));
        Assert.False(EfficiencyModeRules.IsOn(0, 1));
        Assert.Equal((1u, 1u, ProcessPriority.Idle), EfficiencyModeRules.Request(on: true));
        Assert.Equal((0u, 0u, ProcessPriority.Normal), EfficiencyModeRules.Request(on: false));
    }

    // ------------------------------------------------------------------ safety rules

    [Fact]
    public void Windows_and_critical_processes_are_never_suspended_or_throttled()
    {
        var lsass = P(700, "lsass.exe", @"C:\Windows\System32\lsass.exe", signature: Microsoft, session: 0, user: "SYSTEM");
        var dwm = P(710, "dwm.exe", @"C:\Windows\System32\dwm.exe", signature: Microsoft);
        var game = P(720, "game.exe", @"D:\Games\game.exe", window: true);
        var pausedGame = game with { State = ProcessRunState.Suspended };

        Assert.NotNull(ProcessSafetyRules.SuspendRefusal(lsass, Windows, OwnPid));
        Assert.Contains("part of Windows", ProcessSafetyRules.SuspendRefusal(dwm, Windows, OwnPid));
        Assert.Contains("PC Checker", ProcessSafetyRules.SuspendRefusal(P(OwnPid, "PcChecker.exe", @"C:\Tools\PcChecker.exe"), Windows, OwnPid));
        Assert.Null(ProcessSafetyRules.SuspendRefusal(game, Windows, OwnPid));
        Assert.Contains("already", ProcessSafetyRules.SuspendRefusal(pausedGame, Windows, OwnPid));

        Assert.Null(ProcessSafetyRules.ResumeRefusal(pausedGame, Windows, OwnPid, suspendedByPcChecker: false));
        Assert.NotNull(ProcessSafetyRules.ResumeRefusal(game, Windows, OwnPid, suspendedByPcChecker: false));

        Assert.NotNull(ProcessSafetyRules.EfficiencyRefusal(dwm, Windows, OwnPid));
        Assert.Null(ProcessSafetyRules.EfficiencyRefusal(game, Windows, OwnPid));
    }

    [Fact]
    public void Restart_and_dumps_are_offered_only_where_they_are_safe()
    {
        var explorer = P(50, "explorer.exe", @"C:\Windows\explorer.exe", window: true, signature: Microsoft);
        var app = P(60, "app.exe", @"C:\Apps\app.exe", window: true);
        var service = P(70, "agent.exe", @"C:\Apps\agent.exe", session: 0, user: "SYSTEM");
        var otherUser = P(80, "app.exe", @"C:\Apps\app.exe", user: "Sam");
        var kernelName = P(90, "Registry", "Registry", signature: SignatureInfo.Unknown);
        var svchost = P(95, "svchost.exe", @"C:\Windows\System32\svchost.exe", signature: Microsoft);

        Assert.Null(ProcessSafetyRules.RestartRefusal(explorer, Windows, OwnPid, "Alex"));
        Assert.Null(ProcessSafetyRules.RestartRefusal(app, Windows, OwnPid, "alex"));
        Assert.Contains("service", ProcessSafetyRules.RestartRefusal(service, Windows, OwnPid, "Alex"));
        Assert.Contains("Sam", ProcessSafetyRules.RestartRefusal(otherUser, Windows, OwnPid, "Alex"));
        Assert.NotNull(ProcessSafetyRules.RestartRefusal(kernelName, Windows, OwnPid, "Alex"));
        Assert.Contains("part of Windows", ProcessSafetyRules.RestartRefusal(svchost, Windows, OwnPid, "Alex"));

        Assert.Contains("sign-in secrets", ProcessSafetyRules.DumpRefusal(P(700, "lsass.exe", null), OwnPid));
        Assert.NotNull(ProcessSafetyRules.DumpRefusal(P(600, "csrss.exe", null), OwnPid));
        Assert.Null(ProcessSafetyRules.DumpRefusal(app, OwnPid));
        Assert.Null(ProcessSafetyRules.DumpRefusal(P(OwnPid, "PcChecker.exe", @"C:\Tools\PcChecker.exe"), OwnPid));
    }

    [Fact]
    public void Windowed_windows_programs_are_refused_by_what_they_are_not_by_their_section()
    {
        // File Explorer always owns the desktop window, so it is shown under Apps; it must still never be paused.
        var explorer = P(50, "explorer.exe", @"C:\Windows\explorer.exe", window: true, signature: Microsoft);
        var frameHost = P(51, "ApplicationFrameHost.exe", @"C:\Windows\System32\ApplicationFrameHost.exe", window: true, signature: Microsoft);
        var settings = P(52, "SystemSettings.exe", @"C:\Windows\ImmersiveControlPanel\SystemSettings.exe", window: true, signature: Microsoft);
        Assert.Equal(ProcessCategory.App, ProcessGrouping.Classify(explorer, Windows));

        foreach (var process in new[] { explorer, frameHost, settings })
        {
            Assert.Contains("part of Windows", ProcessSafetyRules.SuspendRefusal(process, Windows, OwnPid));
            Assert.NotNull(ProcessSafetyRules.EfficiencyRefusal(process, Windows, OwnPid));
        }

        // Explorer under any signature state, even before its signature was checked.
        Assert.NotNull(ProcessSafetyRules.SuspendRefusal(explorer with { Signature = SignatureInfo.Pending }, Windows, OwnPid));
        Assert.Null(ProcessSafetyRules.RestartRefusal(explorer, Windows, OwnPid, "Alex"));
        Assert.Contains("Start menu", ProcessSafetyRules.RestartRefusal(settings, Windows, OwnPid, "Alex"));
    }

    [Fact]
    public void A_windows_folder_program_counts_as_windows_while_its_signature_is_checked()
    {
        var pending = P(30, "unknownhost.exe", @"C:\Windows\System32\unknownhost.exe", signature: SignatureInfo.Pending);
        Assert.Equal(ProcessCategory.Windows, ProcessGrouping.Classify(pending, Windows));
        Assert.NotNull(ProcessSafetyRules.SuspendRefusal(pending, Windows, OwnPid));
        Assert.NotNull(ProcessSafetyRules.EfficiencyRefusal(pending, Windows, OwnPid));

        // Once checked and not Microsoft's, it is an ordinary background process.
        var vendor = pending with { Signature = Vendor };
        Assert.Equal(ProcessCategory.Background, ProcessGrouping.Classify(vendor, Windows));
        Assert.Null(ProcessSafetyRules.SuspendRefusal(vendor, Windows, OwnPid));

        // Outside the Windows folder a pending signature changes nothing.
        Assert.Equal(ProcessCategory.Background, ProcessGrouping.Classify(P(31, "tool.exe", @"C:\Tools\tool.exe", signature: SignatureInfo.Pending), Windows));
    }

    [Fact]
    public void Store_apps_and_browser_helpers_are_not_restarted()
    {
        var calculator = P(40, "CalculatorApp.exe", @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2_x64__8wekyb3d8bbwe\CalculatorApp.exe", window: true);
        var renderer = P(41, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            commandLine: "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --type=renderer --renderer-client-id=7");
        var browser = renderer with { Pid = 42, CommandLine = "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"", WindowHandle = 0x2000 };

        Assert.Contains("Store app", ProcessSafetyRules.RestartRefusal(calculator, Windows, OwnPid, "Alex"));
        Assert.Contains("helper process", ProcessSafetyRules.RestartRefusal(renderer, Windows, OwnPid, "Alex"));
        Assert.Null(ProcessSafetyRules.RestartRefusal(browser, Windows, OwnPid, "Alex"));
        Assert.True(ProcessSafetyRules.IsPackagedApp(@"C:\Windows\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe"));
        Assert.False(ProcessSafetyRules.IsPackagedApp(@"C:\Program Files\Apps\app.exe"));
    }

    [Fact]
    public void Only_program_files_and_windows_programs_may_run_as_administrator()
    {
        string[] programFiles = [@"C:\Program Files", @"C:\Program Files (x86)"];
        Assert.True(ProcessSafetyRules.MayRunElevated(@"C:\Program Files\Vendor\tool.exe", Windows, programFiles));
        Assert.True(ProcessSafetyRules.MayRunElevated(@"C:\Program Files (x86)\Steam\steam.exe", Windows, programFiles));
        Assert.True(ProcessSafetyRules.MayRunElevated(@"C:\Windows\System32\cmd.exe", Windows, programFiles));

        Assert.False(ProcessSafetyRules.MayRunElevated(@"C:\Users\Alex\Downloads\setup.exe", Windows, programFiles));
        Assert.False(ProcessSafetyRules.MayRunElevated(@"C:\Users\Alex\Desktop\tool.exe", Windows, programFiles));
        Assert.False(ProcessSafetyRules.MayRunElevated(@"D:\Games\game.exe", Windows, programFiles));
        Assert.False(ProcessSafetyRules.MayRunElevated(@"C:\Windows\Temp\x.exe", Windows, programFiles));
        Assert.False(ProcessSafetyRules.MayRunElevated(@"C:\Windows\Tasks\x.exe", Windows, programFiles));
        Assert.False(ProcessSafetyRules.MayRunElevated(@"C:\Program Files\..\Users\Alex\x.exe", Windows, programFiles));
        Assert.False(ProcessSafetyRules.MayRunElevated(@"C:\Program FilesEvil\x.exe", Windows, programFiles));
        Assert.False(ProcessSafetyRules.MayRunElevated("tool.exe", Windows, programFiles));

        Assert.True(ProcessSafetyRules.IsRunnableFile(@"C:\x\setup.EXE"));
        Assert.True(ProcessSafetyRules.IsRunnableFile(@"C:\x\script.ps1"));
        Assert.False(ProcessSafetyRules.IsRunnableFile(@"C:\x\notes.txt"));
    }

    [Fact]
    public void Bare_names_are_looked_up_on_the_path_like_windows_does()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Windows\System32\notepad.exe",
            @"C:\Users\Alex\AppData\Local\Microsoft\WindowsApps\wt.exe",
            @"C:\Windows\System32\run.cmd",
        };
        const string path = @"C:\Windows\System32;""C:\Users\Alex\AppData\Local\Microsoft\WindowsApps"";relative";

        Assert.Equal(@"C:\Windows\System32\notepad.exe", ProcessSafetyRules.ResolveOnPath("notepad", path, ".COM;.EXE;.CMD", files.Contains));
        Assert.Equal(@"C:\Windows\System32\notepad.exe", ProcessSafetyRules.ResolveOnPath("notepad.exe", path, ".COM;.EXE", files.Contains));
        Assert.Equal(@"C:\Users\Alex\AppData\Local\Microsoft\WindowsApps\wt.exe", ProcessSafetyRules.ResolveOnPath("wt", path, null, files.Contains));
        Assert.Equal(@"C:\Windows\System32\run.cmd", ProcessSafetyRules.ResolveOnPath("run", path, ".EXE;.CMD", files.Contains));
        Assert.Null(ProcessSafetyRules.ResolveOnPath("missing", path, ".EXE", files.Contains));
        Assert.Null(ProcessSafetyRules.ResolveOnPath(@"sub\notepad", path, ".EXE", files.Contains));
        Assert.Null(ProcessSafetyRules.ResolveOnPath("ms-settings:", path, ".EXE", files.Contains));

        // The alias resolves into the user's own folder, so it is not started as administrator.
        Assert.False(ProcessSafetyRules.MayRunElevated(ProcessSafetyRules.ResolveOnPath("wt", path, null, files.Contains), Windows, [@"C:\Program Files"]));
    }

    // ------------------------------------------------------------------ text helpers

    [Fact]
    public void Arguments_after_the_program_file_are_kept_exactly()
    {
        Assert.Equal("--x \"y z\"", CommandLineText.ArgumentsAfterExecutable("\"C:\\Program Files\\App\\app.exe\" --x \"y z\""));
        Assert.Equal("-silent", CommandLineText.ArgumentsAfterExecutable(@"C:\Steam\steam.exe -silent"));
        Assert.Equal(string.Empty, CommandLineText.ArgumentsAfterExecutable("\"C:\\App\\app.exe\""));
        Assert.Equal(string.Empty, CommandLineText.ArgumentsAfterExecutable(null));
        Assert.Equal("/k echo hi", CommandLineText.ArgumentsAfterExecutable("  cmd.exe\t/k echo hi "));

        // An unquoted path with spaces: the known program path is taken as a whole.
        Assert.Equal("--x", CommandLineText.ArgumentsAfterExecutable(@"C:\Program Files\App\app.exe --x", @"C:\Program Files\App\app.exe"));
        Assert.Equal("--x", CommandLineText.ArgumentsAfterExecutable(@"c:\program files\app\APP.EXE --x", @"C:\Program Files\App\app.exe"));
        Assert.Equal(string.Empty, CommandLineText.ArgumentsAfterExecutable(@"C:\Program Files\App\app.exe", @"C:\Program Files\App\app.exe"));
        Assert.Equal("--x \"y z\"", CommandLineText.ArgumentsAfterExecutable("\"C:\\Program Files\\App\\app.exe\" --x \"y z\"", @"C:\Program Files\App\app.exe"));
        Assert.Equal("Files\\Other\\b.exe", CommandLineText.ArgumentsAfterExecutable(@"C:\Program Files\Other\b.exe", @"C:\Program Files\App\app.exe"));
    }

    [Fact]
    public void Browser_helpers_get_a_plain_role()
    {
        Assert.Equal("Tab or extension", CommandLineText.ChildRole("\"chrome.exe\" --type=renderer --renderer-client-id=7"));
        Assert.Equal("Extension", CommandLineText.ChildRole("chrome.exe --type=renderer --extension-process"));
        Assert.Equal("Graphics helper", CommandLineText.ChildRole("chrome.exe --type=gpu-process --x"));
        Assert.Equal("Network helper", CommandLineText.ChildRole("chrome.exe --type=utility --utility-sub-type=network.mojom.NetworkService"));
        Assert.Equal("Audio helper", CommandLineText.ChildRole("Discord.exe --type=utility --utility-sub-type=audio.mojom.AudioService"));
        Assert.Equal("Crash reporter", CommandLineText.ChildRole("chrome.exe --type=crashpad-handler"));
        Assert.Null(CommandLineText.ChildRole("\"C:\\Program Files\\Google\\Chrome\\chrome.exe\""));
        Assert.Null(CommandLineText.ChildRole(null));
    }

    [Fact]
    public void Run_new_task_splits_the_program_from_its_options()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Program Files\My App\app.exe", @"C:\My Folder" };
        bool Exists(string path) => files.Contains(path);

        Assert.Equal(new RunCommand(@"C:\Program Files\My App\app.exe", "--safe"), CommandLineText.ParseRunCommand(@"C:\Program Files\My App\app.exe --safe", Exists));
        Assert.Equal(new RunCommand(@"C:\My Folder", string.Empty), CommandLineText.ParseRunCommand(@" C:\My Folder ", Exists));
        Assert.Equal(new RunCommand(@"C:\x y\z.exe", "-a"), CommandLineText.ParseRunCommand("\"C:\\x y\\z.exe\" -a", Exists));
        Assert.Equal(new RunCommand("notepad", @"C:\notes.txt"), CommandLineText.ParseRunCommand(@"notepad C:\notes.txt", Exists));
        Assert.Equal(new RunCommand("https://example.com/a b", string.Empty), CommandLineText.ParseRunCommand("https://example.com/a b", Exists));
        Assert.Equal(new RunCommand("ms-settings:windowsupdate", string.Empty), CommandLineText.ParseRunCommand("ms-settings:windowsupdate", Exists));
        Assert.Null(CommandLineText.ParseRunCommand("   ", Exists));
        Assert.Equal(new RunCommand("cmd", string.Empty), CommandLineText.ParseRunCommand("cmd", _ => throw new IOException()));
    }

    [Fact]
    public void Recent_commands_and_dump_names_follow_task_manager()
    {
        var recent = CommandLineText.AddRecent(["notepad", "cmd", "Notepad"], "CMD");
        Assert.Equal(["CMD", "notepad"], recent);
        var many = Enumerable.Range(0, 20).Select(i => "app" + i);
        Assert.Equal(CommandLineText.MaxRecentCommands, CommandLineText.AddRecent(many, "new").Count);

        var taken = new HashSet<string> { @"C:\Temp\chrome.DMP", @"C:\Temp\chrome (2).DMP" };
        Assert.Equal(@"C:\Temp\chrome (3).DMP", CommandLineText.DumpFilePath(@"C:\Temp\", "chrome.exe", taken.Contains));
        Assert.Equal(@"C:\Temp\Discord.DMP", CommandLineText.DumpFilePath(@"C:\Temp", "Discord.exe", taken.Contains));
        Assert.Equal(@"C:\Temp\a_b.DMP", CommandLineText.DumpFilePath(@"C:\Temp", "a:b", _ => false));
        Assert.Equal(@"C:\Temp\process.DMP", CommandLineText.DumpFilePath(@"C:\Temp", "  ", _ => false));
    }

    [Fact]
    public void Gpu_column_shows_the_busiest_engine_like_task_manager()
    {
        var values = new Dictionary<string, double>
        {
            ["pid_100_luid_0x00000000_0x0000D1A5_phys_0_eng_0_engtype_3D"] = 40,
            ["pid_100_luid_0x00000000_0x0000D1A5_phys_0_eng_3_engtype_VideoDecode"] = 30,
            ["pid_200_luid_0x00000000_0x0000E000_phys_0_eng_0_engtype_3D"] = 12,
            ["pid_200_luid_0x00000000_0x0000D1A5_phys_0_eng_5_engtype_Copy"] = 2,
            ["pid_300_luid_0x00000000_0x0000D1A5_phys_0_eng_0_engtype_3D"] = 0,
            ["luid_0x0_phys_0_eng_0_engtype_3D"] = 50,
        };

        var busiest = ProcessMath.BusiestGpuEngineByPid(values);
        Assert.Equal((40.0, "GPU 0 - 3D"), busiest[100]);       // the busiest engine, not 70 % summed
        Assert.Equal((12.0, "GPU 1 - 3D"), busiest[200]);       // second adapter by LUID
        Assert.False(busiest.ContainsKey(300));
        Assert.Equal(2, busiest.Count);

        // Adapter numbers come from every adapter, idle or not: the second adapter stays "GPU 1" while the first rests.
        var secondOnly = new Dictionary<string, double>
        {
            ["pid_100_luid_0x00000000_0x0000D1A5_phys_0_eng_0_engtype_3D"] = 0,
            ["pid_200_luid_0x00000000_0x0000E000_phys_0_eng_0_engtype_3D"] = 25,
        };
        Assert.Equal((25.0, "GPU 1 - 3D"), ProcessMath.BusiestGpuEngineByPid(secondOnly)[200]);
        Assert.Equal("0 MB/s", ProcessMath.FormatMegabytesPerSecond(1000));
        Assert.Equal("0.1 MB/s", ProcessMath.FormatMegabytesPerSecond(0.1 * 1024 * 1024));
        Assert.Equal("12.4 MB/s", ProcessMath.FormatMegabytesPerSecond(12.4 * 1024 * 1024));
    }

    [Fact]
    public void Architectures_map_from_windows_machine_types()
    {
        Assert.Equal(ProcessArchitecture.X86, ProcessArchitectureRules.FromWow64(ProcessArchitectureRules.MachineI386, ProcessArchitectureRules.MachineAmd64));
        Assert.Equal(ProcessArchitecture.X64, ProcessArchitectureRules.FromWow64(ProcessArchitectureRules.MachineUnknown, ProcessArchitectureRules.MachineAmd64));
        Assert.Equal(ProcessArchitecture.Arm64, ProcessArchitectureRules.FromWow64(0, ProcessArchitectureRules.MachineArm64));
        Assert.Equal(ProcessArchitecture.Unknown, ProcessArchitectureRules.FromMachine(0x1234));
        Assert.Equal("x86", ProcessArchitectureRules.Label(ProcessArchitecture.X86));
        Assert.Equal(string.Empty, ProcessArchitectureRules.Label(ProcessArchitecture.Unknown));
    }

    [Fact]
    public void New_columns_sort_the_way_people_expect()
    {
        var list = new[]
        {
            P(1, "a.exe", null, connections: 4),
            P(2, "b.exe", null, state: ProcessRunState.NotResponding),
            P(3, "c.exe", null, state: ProcessRunState.Suspended, connections: 30),
            P(4, "d.exe", null, efficiency: true),
        };

        Assert.Equal([2, 3, 4, 1], ProcessSorting.Sort(list, ProcessSortColumn.Status, descending: true).Select(p => p.Pid));
        Assert.Equal([3, 1, 2, 4], ProcessSorting.Sort(list, ProcessSortColumn.Network, descending: true).Select(p => p.Pid));
        Assert.True(ProcessSorting.DefaultDescending(ProcessSortColumn.Power));
        Assert.False(ProcessSorting.DefaultDescending(ProcessSortColumn.CommandLine));
    }

    [Fact]
    public void Store_and_windows_apps_paused_by_windows_are_not_resumed()
    {
        var searchHost = P(4664, "SearchHost.exe", @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe",
            signature: Microsoft, state: ProcessRunState.Suspended);
        var storeApp = P(4700, "Calculator.exe", @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_8wekyb3d8bbwe\Calculator.exe",
            window: true, state: ProcessRunState.Suspended);
        var spotify = P(8410, "Spotify.exe", @"C:\Users\Alex\AppData\Roaming\Spotify\Spotify.exe", window: true, state: ProcessRunState.Suspended);

        Assert.Contains("Windows paused", ProcessSafetyRules.ResumeRefusal(searchHost, Windows, OwnPid, suspendedByPcChecker: false));
        Assert.NotNull(ProcessSafetyRules.ResumeRefusal(storeApp, Windows, OwnPid, suspendedByPcChecker: false));
        Assert.Null(ProcessSafetyRules.ResumeRefusal(storeApp, Windows, OwnPid, suspendedByPcChecker: true));
        Assert.Null(ProcessSafetyRules.ResumeRefusal(spotify, Windows, OwnPid, suspendedByPcChecker: false));
    }

    [Fact]
    public async Task Demo_refuses_to_resume_search_that_windows_paused_but_resumes_what_it_paused_itself()
    {
        var demo = new DemoProcessService();
        var snapshot = await demo.SampleAsync(CancellationToken.None);
        var searchHost = snapshot.Processes.First(p => p.Name == "SearchHost.exe");
        Assert.Equal(ProcessRunState.Suspended, searchHost.State);
        Assert.False((await demo.ResumeAsync(searchHost, CancellationToken.None)).Success);

        var game = snapshot.Processes.First(p => p.HasWindow && p.State == ProcessRunState.Running
                                                 && ProcessSafetyRules.SuspendRefusal(p, Windows, demo.OwnProcessId) is null);
        Assert.True((await demo.SuspendAsync(game, CancellationToken.None)).Success);
        Assert.True(demo.WasSuspendedByPcChecker(game));
        var paused = (await demo.SampleAsync(CancellationToken.None)).Processes.First(p => p.Pid == game.Pid);
        Assert.True((await demo.ResumeAsync(paused, CancellationToken.None)).Success);
        Assert.False(demo.WasSuspendedByPcChecker(game));
    }

    [Fact]
    public void File_explorer_as_the_desktop_is_ended_directly_never_asked_to_close()
    {
        var shell = P(50, "explorer.exe", @"C:\Windows\explorer.exe", window: true, signature: Microsoft);
        var copy = P(51, "explorer.exe", @"D:\Portable\explorer.exe", window: true);

        Assert.True(ProcessSafetyRules.IsShellExplorer(shell, Windows));
        Assert.True(ProcessSafetyRules.IsShellExplorer(P(52, "explorer.exe", null, window: true), Windows));
        Assert.False(ProcessSafetyRules.IsShellExplorer(copy, Windows));
        Assert.False(ProcessSafetyRules.IsShellExplorer(P(53, "notepad.exe", @"C:\Windows\notepad.exe", window: true), Windows));

        Assert.Equal(EndTaskPlan.EndNow, ProcessStatusRules.PlanEndTask(hasWindow: true, ProcessRunState.Running, isShell: true));
        Assert.Equal(EndTaskPlan.CloseWindowsFirst, ProcessStatusRules.PlanEndTask(hasWindow: true, ProcessRunState.Running, isShell: false));
    }

    [Fact]
    public void Run_as_administrator_starts_the_full_path_it_checked_never_a_bare_name()
    {
        string[] programFiles = [@"C:\Program Files", @"C:\Program Files (x86)"];
        static string? NotFound(string _) => null;
        static bool NoFolder(string _) => false;
        Func<string, string?> onPath = name => name.Equals("notepad", StringComparison.OrdinalIgnoreCase) ? @"C:\Windows\System32\notepad.exe"
            : name.Equals("evil", StringComparison.OrdinalIgnoreCase) ? @"C:\Users\Alex\AppData\Local\Microsoft\WindowsApps\evil.exe" : null;
        Func<string, string?> appPaths = name => name.Equals("chrome", StringComparison.OrdinalIgnoreCase)
            ? @"C:\Program Files\Google\Chrome\Application\chrome.exe" : null;

        var notepad = ProcessSafetyRules.DecideElevatedLaunch("notepad", Windows, programFiles, NoFolder, onPath, appPaths);
        Assert.Null(notepad.Refusal);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", notepad.Target);

        Assert.Equal(@"C:\Program Files\Google\Chrome\Application\chrome.exe",
            ProcessSafetyRules.DecideElevatedLaunch("chrome", Windows, programFiles, NoFolder, onPath, appPaths).Target);

        // Found only in a user folder, not found at all (Windows would look in the current folder and the user's own
        // App Paths list), or a relative path: refused.
        Assert.NotNull(ProcessSafetyRules.DecideElevatedLaunch("evil", Windows, programFiles, NoFolder, onPath, appPaths).Refusal);
        Assert.NotNull(ProcessSafetyRules.DecideElevatedLaunch("planted", Windows, programFiles, NoFolder, NotFound, NotFound).Refusal);
        Assert.NotNull(ProcessSafetyRules.DecideElevatedLaunch("notes.txt", Windows, programFiles, NoFolder, NotFound, NotFound).Refusal);
        Assert.NotNull(ProcessSafetyRules.DecideElevatedLaunch(@"tools\x.exe", Windows, programFiles, NoFolder, NotFound, NotFound).Refusal);
        Assert.NotNull(ProcessSafetyRules.DecideElevatedLaunch(@"C:\Users\Alex\Downloads\setup.exe", Windows, programFiles, NoFolder, NotFound, NotFound).Refusal);
        Assert.NotNull(ProcessSafetyRules.DecideElevatedLaunch(@"C:\Users\Alex\Downloads\run.py", Windows, programFiles, NoFolder, NotFound, NotFound).Refusal);

        // Trusted programs, folders, documents, web addresses and Windows links open as before.
        Assert.Null(ProcessSafetyRules.DecideElevatedLaunch(@"C:\Windows\System32\cmd.exe", Windows, programFiles, NoFolder, NotFound, NotFound).Refusal);
        Assert.Null(ProcessSafetyRules.DecideElevatedLaunch(@"C:\Users\Alex\Documents", Windows, programFiles, _ => true, NotFound, NotFound).Refusal);
        Assert.Null(ProcessSafetyRules.DecideElevatedLaunch(@"C:\Users\Alex\notes.txt", Windows, programFiles, NoFolder, NotFound, NotFound).Refusal);
        Assert.Equal("ms-settings:", ProcessSafetyRules.DecideElevatedLaunch("ms-settings:", Windows, programFiles, NoFolder, NotFound, NotFound).Target);
        Assert.Null(ProcessSafetyRules.DecideElevatedLaunch("https://example.com", Windows, programFiles, NoFolder, NotFound, NotFound).Refusal);
    }

    [Theory]
    [InlineData("script.py", true)]
    [InlineData("tool.pyw", true)]
    [InlineData("app.jar", true)]
    [InlineData("module.psm1", true)]
    [InlineData("package.msix", true)]
    [InlineData("package.appx", true)]
    [InlineData("setup.appinstaller", true)]
    [InlineData("deploy.application", true)]
    [InlineData("notes.txt", false)]
    [InlineData("photo.jpg", false)]
    public void Scripts_and_installers_count_as_programs_not_documents(string file, bool runnable)
    {
        Assert.Equal(runnable, ProcessSafetyRules.IsRunnableFile(@"C:\Users\Alex\Downloads\" + file));
    }

    [Theory]
    [InlineData("ms-settings:", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData(@"C:\Windows", false)]
    [InlineData("C:", false)]
    [InlineData("notepad", false)]
    public void Windows_links_are_told_apart_from_drive_letters(string text, bool link)
    {
        Assert.Equal(link, ProcessSafetyRules.IsShellLink(text));
    }

    [Fact]
    public void Run_new_task_history_is_never_written_to_a_setup_file()
    {
        var settings = AppSettings.CreateDefault();
        settings.Processes.RecentRunCommands = ["tool.exe --token=SECRET-TOKEN"];
        settings.Processes.DetailsView = true;

        string text = PcChecker.Core.SetupBackup.SetupExporter.Export(settings, null, PcChecker.Core.SetupBackup.SetupExportOptions.Everything, "0.5.0", T0, null);

        Assert.DoesNotContain("SECRET-TOKEN", text);
        Assert.DoesNotContain("RecentRunCommands", text);
        Assert.Contains("DetailsView", text);
    }

    // ------------------------------------------------------------------ demo data

    [Fact]
    public async Task Demo_list_shows_apps_with_helpers_a_frozen_app_and_a_suspended_app()
    {
        var demo = new DemoProcessService();
        var snapshot = await demo.SampleAsync(CancellationToken.None);
        Assert.True(snapshot.ConnectionsAvailable);

        var groups = ProcessGrouping.Build(snapshot.Processes, Windows);
        var apps = groups.Where(g => g.Category == ProcessCategory.App).ToList();
        Assert.Contains(apps, g => g.Title == "Steam" && g.Members.Count == 4);
        Assert.Contains(apps, g => g.Members.Any(m => m.Name == "chrome.exe") && g.Members.Count >= 5);
        Assert.Contains(apps, g => g.State == ProcessRunState.NotResponding);
        Assert.Contains(apps, g => g.State == ProcessRunState.Suspended);
        Assert.Contains(groups, g => g.Category == ProcessCategory.Windows && g.Members.Any(m => m.Name == "svchost.exe"));
        Assert.Contains(groups, g => g.Category == ProcessCategory.Background && g.Members.Any(m => m.EfficiencyMode));

        // Demo actions follow the same safety rules as the real ones.
        var svchost = snapshot.Processes.First(p => p.Name == "svchost.exe");
        Assert.False((await demo.SuspendAsync(svchost, CancellationToken.None)).Success);
        var spotify = snapshot.Processes.First(p => p.Name == "Spotify.exe" && p.HasWindow);
        Assert.True((await demo.ResumeAsync(spotify, CancellationToken.None)).Success);
        var after = await demo.SampleAsync(CancellationToken.None);
        Assert.Equal(ProcessRunState.Running, after.Processes.First(p => p.Pid == spotify.Pid).State);
    }
}
