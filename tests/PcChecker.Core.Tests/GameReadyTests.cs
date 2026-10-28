using Microsoft.Extensions.Logging.Abstractions;
using PcChecker.Core.Displays;
using PcChecker.Core.GameReady;
using PcChecker.Core.Games;
using PcChecker.Core.Hardware;
using PcChecker.Core.Latency;
using PcChecker.Core.Localization;
using PcChecker.Core.Memory;
using PcChecker.Core.Optimization;
using PcChecker.Core.Processes;
using PcChecker.Core.Security;
using PcChecker.Core.Storage;
using Xunit;

namespace PcChecker.Core.Tests;

public class GameReadyTests
{
    private static readonly Translator En = Translator.English;
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);

    // ----- graphics preference text ("GpuPreference=2;") -----------------------------------------------------------

    [Theory]
    [InlineData("GpuPreference=2;", 2)]
    [InlineData("GpuPreference=1;", 1)]
    [InlineData("AutoHDREnable=1;GpuPreference=0;", 0)]
    [InlineData(" gpupreference = 2 ; ", 2)]
    [InlineData("SwapEffectUpgradeEnable=1;", null)]
    [InlineData("GpuPreference=;", null)]
    [InlineData(null, null)]
    public void Reads_the_graphics_preference_of_a_program(string? text, int? expected) =>
        Assert.Equal(expected, DirectXSettingsText.GpuPreference(text));

    [Theory]
    [InlineData(null, "GpuPreference=2;")]
    [InlineData("", "GpuPreference=2;")]
    [InlineData("GpuPreference=1;", "GpuPreference=2;")]
    [InlineData("AutoHDREnable=1;GpuPreference=1;SwapEffectUpgradeEnable=0;", "AutoHDREnable=1;GpuPreference=2;SwapEffectUpgradeEnable=0;")]
    [InlineData("SwapEffectUpgradeEnable=1", "SwapEffectUpgradeEnable=1;GpuPreference=2;")]
    public void Builds_the_high_performance_preference_and_keeps_other_values(string? before, string expected) =>
        Assert.Equal(expected, DirectXSettingsText.WithGpuPreference(before, DirectXSettingsText.GpuHighPerformance));

    [Fact]
    public void Global_switches_are_set_removed_and_read_without_touching_the_rest()
    {
        const string before = "VRROptimizeEnable=0;SwapEffectUpgradeEnable=0;";
        string after = DirectXSettingsText.Set(before, DirectXSettingsText.WindowedOptimizations, "1");
        Assert.Equal("VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;", after);
        Assert.True(DirectXSettingsText.Switch(after, DirectXSettingsText.WindowedOptimizations));
        Assert.False(DirectXSettingsText.Switch(after, DirectXSettingsText.VariableRefreshRate));
        Assert.Null(DirectXSettingsText.Switch(after, "AutoHDREnable"));
        Assert.Null(DirectXSettingsText.Switch("AutoHDREnable=2097;", "AutoHDREnable"));
        Assert.Equal("SwapEffectUpgradeEnable=1;", DirectXSettingsText.Remove(after, DirectXSettingsText.VariableRefreshRate));
        Assert.Equal(string.Empty, DirectXSettingsText.Remove("GpuPreference=2;", DirectXSettingsText.GpuPreferenceName));
    }

    // ----- refresh rate selection -----------------------------------------------------------------------------------

    [Fact]
    public void Picks_the_highest_refresh_at_the_current_resolution_and_colour_depth()
    {
        var current = new DisplayMode(2560, 1440, 60, 32);
        DisplayMode[] modes =
        [
            new(3840, 2160, 60, 32), new(1920, 1080, 280, 32), // faster, but another resolution
            new(2560, 1440, 60, 32), new(2560, 1440, 144, 32), new(2560, 1440, 165, 32),
            new(2560, 1440, 240, 16), // fastest, but fewer colours
        ];

        var best = RefreshRateRules.BestAtCurrentResolution(current, modes);

        Assert.Equal(new DisplayMode(2560, 1440, 165, 32), best);
    }

    [Fact]
    public void A_59_Hz_mode_next_to_60_Hz_is_not_a_faster_refresh_rate()
    {
        var current = new DisplayMode(1920, 1080, 59, 32);
        Assert.Null(RefreshRateRules.BestAtCurrentResolution(current, [new(1920, 1080, 60, 32), new(1920, 1080, 59, 32)]));
        Assert.Null(RefreshRateRules.BestAtCurrentResolution(new DisplayMode(1920, 1080, 144, 32), [new(1920, 1080, 144, 32)]));
        Assert.Null(RefreshRateRules.BestAtCurrentResolution(current, []));
        Assert.Equal(GameReadyImpact.High, RefreshRateRules.Impact(60, 165));
        Assert.Equal(GameReadyImpact.Medium, RefreshRateRules.Impact(165, 240));
        Assert.Equal(GameReadyImpact.Low, RefreshRateRules.Impact(144, 165));
        Assert.Equal(GameReadyImpact.None, RefreshRateRules.Impact(143, 144));
    }

    [Fact]
    public void A_panel_left_at_60_Hz_is_a_big_problem_with_a_refresh_fix_that_remembers_the_old_mode()
    {
        var facts = Baseline() with
        {
            Displays = [new GameReadyDisplay(@"\\.\DISPLAY1", "ASUS VG27AQ", true, false, new DisplayMode(2560, 1440, 60, 32),
                [new(2560, 1440, 60, 32), new(2560, 1440, 120, 32), new(2560, 1440, 165, 32)], false, false)],
        };

        var report = GameReadyRules.Evaluate(facts, En);
        var item = report.Items[0];

        Assert.Equal("display-refresh-display1", item.Id);
        Assert.Equal(GameReadyStatus.Problem, item.Status);
        Assert.Equal(GameReadyImpact.High, item.Impact);
        Assert.Equal("ASUS VG27AQ runs at 60 Hz but can do 165 Hz", item.Title);
        Assert.Equal(GameReadyFixKind.RefreshRate, item.Fix!.Kind);
        Assert.Equal(165, item.Fix.Mode!.RefreshHz);
        Assert.Equal(60, item.Fix.PreviousMode!.RefreshHz);
        Assert.True(item.IsSafeFix);
    }

    // ----- PCI Express link and Resizable BAR ----------------------------------------------------------------------

    [Theory]
    [InlineData(16, 16, 4, 4, PcieLinkVerdict.Full)]
    [InlineData(16, 16, 1, 4, PcieLinkVerdict.SlowerSpeed)] // idle speed drop: never a problem
    [InlineData(4, 16, 4, 4, PcieLinkVerdict.ReducedWidth)]
    [InlineData(8, 16, 1, 4, PcieLinkVerdict.ReducedWidth)] // width wins even when idle
    [InlineData(8, 8, 4, 4, PcieLinkVerdict.Full)] // x8 cards (RTX 4060) are complete at x8
    [InlineData(null, 16, 4, 4, PcieLinkVerdict.Unknown)]
    [InlineData(0, 16, null, null, PcieLinkVerdict.Unknown)]
    public void Judges_the_link_by_lane_count_first(int? width, int? maxWidth, int? speed, int? maxSpeed, PcieLinkVerdict expected) =>
        Assert.Equal(expected, PcieLinkRules.Evaluate(new PcieLink(width, maxWidth, speed, maxSpeed)));

    [Fact]
    public void Reduced_lanes_are_a_problem_but_an_idle_speed_drop_only_asks_for_a_check_under_load()
    {
        var narrow = GameReadyRules.Evaluate(Baseline() with { Gpus = [Gpu(new PcieLink(4, 16, 4, 4))] }, En).Items.Single(i => i.Id == "gpu-link");
        var idle = GameReadyRules.Evaluate(Baseline() with { Gpus = [Gpu(new PcieLink(16, 16, 1, 4))] }, En).Items.Single(i => i.Id == "gpu-link");

        Assert.Equal(GameReadyStatus.Problem, narrow.Status);
        Assert.Contains("x4 of its x16", narrow.Title, StringComparison.Ordinal);
        Assert.Contains("PCIe 4.0 x4", narrow.Detail, StringComparison.Ordinal);
        Assert.Equal(GameReadyStatus.Info, idle.Status);
        Assert.Contains("under load", idle.Title, StringComparison.Ordinal);
        Assert.Equal("PCIe 4.0 x16", PcieLinkRules.Describe(4, 16));
    }

    [Fact]
    public void Resizable_bar_is_read_from_the_size_of_the_cards_memory_window()
    {
        const ulong mb = 1024 * 1024;
        Assert.Equal(ResizableBarState.On, ResizableBarRules.FromMemoryRanges([16 * mb, 16384 * mb, 32 * mb]));
        Assert.Equal(ResizableBarState.Off, ResizableBarRules.FromMemoryRanges([16 * mb, 256 * mb, 32 * mb]));
        Assert.Equal(ResizableBarState.Unknown, ResizableBarRules.FromMemoryRanges([16 * mb]));
        Assert.Equal(ResizableBarState.Unknown, ResizableBarRules.FromMemoryRanges([]));

        var unknown = GameReadyRules.Evaluate(Baseline() with { Gpus = [Gpu(null) with { ResizableBar = ResizableBarState.Unknown }] }, En)
            .Items.Single(i => i.Id == "gpu-rebar");
        Assert.Equal(GameReadyStatus.NotChecked, unknown.Status);
        Assert.Contains("Above 4G Decoding", unknown.Detail, StringComparison.Ordinal);
    }

    // ----- rules, scoring and order ---------------------------------------------------------------------------------

    [Fact]
    public void A_well_set_up_pc_scores_100_with_nothing_to_do()
    {
        var report = GameReadyRules.Evaluate(Baseline(), En);

        Assert.Equal(100, report.Score);
        Assert.DoesNotContain(report.Items, i => i.NeedsAttention);
        Assert.Contains(report.Items, i => i.Id == "memory-xmp" && i.Status == GameReadyStatus.Good);
        Assert.Equal("Excellent", GameReadyRules.Grade(report.Score, En));
    }

    [Fact]
    public void Biggest_wins_come_first_and_the_score_drops_by_their_weight()
    {
        var facts = Baseline() with
        {
            GameModeOn = false,
            GameDvrOn = true,
            Memory = new GameReadyMemory("Disabled", 4800, 6000, 2, true, false, "DDR5"),
            Displays = [new GameReadyDisplay("D1", "Main", true, false, new DisplayMode(1920, 1080, 60, 32), [new(1920, 1080, 240, 32)], false, false)],
        };

        var report = GameReadyRules.Evaluate(facts, En);
        var todo = report.Items.Where(i => i.NeedsAttention).Select(i => i.Id).ToList();

        // Two high-impact problems (refresh rate first by area), then the medium suggestion, then the small one.
        Assert.Equal(["display-refresh-d1", "memory-xmp", "game-dvr", "game-mode"], todo);
        Assert.Equal(100 - 15 - 15 - 5 - 2, report.Score);
        Assert.Equal(GameReadyStatus.Good, report.Items[^1].Status);
        Assert.Equal(GameReadyRules.RamTuningPage, report.Items.Single(i => i.Id == "memory-xmp").Fix!.Target);
    }

    [Fact]
    public void Only_reversible_low_risk_changes_are_offered_for_fix_all()
    {
        var facts = Baseline() with
        {
            HagsOn = false,
            GameModeOn = false,
            GameDvrOn = true,
            PointerPrecisionOn = true,
            HighPerformancePlan = false,
            DirectXGlobalSettings = "SwapEffectUpgradeEnable=0;",
            Memory = new GameReadyMemory("Disabled", 4800, 6000, 1, null, false, "DDR5"),
            Apps = new GameReadyApps(["Discord overlay", "Overwolf overlay"], ["Corsair iCUE"], [], []),
        };

        var report = GameReadyRules.Evaluate(facts, En);
        var safe = report.Items.Where(i => i.IsSafeFix).Select(i => i.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();

        Assert.Equal(["game-dvr", "game-mode", "mouse-acceleration", "power-plan", "windowed-optimizations"], safe);
        var hags = report.Items.Single(i => i.Id == "hags");
        Assert.True(hags.Fix!.RequiresRestart);
        Assert.False(hags.IsSafeFix);
        Assert.False(report.Items.Single(i => i.Id == "memory-channels").IsSafeFix); // a BIOS or hardware job: link only
        Assert.All(GameReadyRules.SafeOptimizationActions, id => Assert.True(GameReadyRules.IsSafeAction(id, OptimizationCatalog.All.Concat(LatencyActionCatalog.All))));
    }

    [Fact]
    public void Values_that_cannot_be_read_are_never_problems()
    {
        var facts = new GameReadyFacts { Now = Now, DirectXReadable = false };

        var report = GameReadyRules.Evaluate(facts, En);

        Assert.DoesNotContain(report.Items, i => i.Status == GameReadyStatus.Problem);
        Assert.Contains(report.Items, i => i.Id == "display-refresh" && i.Status == GameReadyStatus.NotChecked);
        Assert.Contains(report.Items, i => i.Id == "gpu" && i.Status == GameReadyStatus.NotChecked);
        Assert.Contains(report.Items, i => i.Id == "windowed-optimizations" && i.Status == GameReadyStatus.NotChecked);
        Assert.Equal(100, report.Score);
    }

    [Fact]
    public void Laptops_on_battery_and_hybrid_graphics_preferences_are_flagged()
    {
        var facts = Baseline() with
        {
            HasBattery = true,
            OnBattery = true,
            Gpus = [Gpu(new PcieLink(8, 8, 4, 4)), new GameReadyGpu("Intel(R) UHD Graphics", true, "Intel", Now.DateTime.AddDays(-20), null, ResizableBarState.Unknown)],
            Games =
            [
                new GameReadyGame("Cyberpunk 2077", @"C:\Games\Cyberpunk2077.exe", "C:", "GpuPreference=1;"),
                new GameReadyGame("Hades II", @"C:\Games\Hades2.exe", "C:", null),
            ],
        };

        var report = GameReadyRules.Evaluate(facts, En);
        var preference = report.Items.Single(i => i.Id == "gpu-preference");

        Assert.Equal(GameReadyStatus.Problem, report.Items.Single(i => i.Id == "power-battery").Status);
        Assert.Equal(GameReadyStatus.Problem, preference.Status);
        Assert.Equal([@"C:\Games\Cyberpunk2077.exe"], preference.Fix!.Paths);
        Assert.DoesNotContain(report.Items, i => i.Id == "power-plan"); // never push High performance on a laptop

        var desktop = GameReadyRules.Evaluate(facts with { HasBattery = false, OnBattery = false, Games = [facts.Games[1]] }, En);
        Assert.Equal(GameReadyStatus.Good, desktop.Items.Single(i => i.Id == "gpu-preference").Status);
    }

    [Fact]
    public void Memory_integrity_is_only_ever_explained_never_turned_off()
    {
        var item = GameReadyRules.Evaluate(Baseline() with { MemoryIntegrityOn = true }, En).Items.Single(i => i.Id == "memory-integrity");

        Assert.Equal(GameReadyStatus.Info, item.Status);
        Assert.Equal(0, GameReadyRules.Penalty(item));
        Assert.Equal(GameReadyFixKind.OpenPage, item.Fix!.Kind);
        Assert.False(item.Fix.ChangesSystem);
    }

    // ----- background apps, memory kits, drives ---------------------------------------------------------------------

    [Fact]
    public void Sorts_running_programs_into_overlays_rgb_suites_browsers_and_busy_updaters()
    {
        const long gb = 1024L * 1024 * 1024;
        var processes = new List<RunningProcess>
        {
            new("Discord.exe", 400_000_000, 0), new("Discord.exe", 100_000_000, 0),
            new("iCUE.exe", 200_000_000, 0), new("HWiNFO64.exe", 50_000_000, 0), new("steam.exe", 300_000_000, 0),
            new("TiWorker.exe", 80_000_000, 6 * 1024 * 1024), new("OneDrive.exe", 90_000_000, 1000),
        };
        processes.AddRange(Enumerable.Range(0, 20).Select(_ => new RunningProcess("chrome.exe", gb / 8, 0)));

        var apps = BackgroundAppCatalog.Classify(processes);

        Assert.Equal(["Discord overlay"], apps.Overlays);
        Assert.Equal(["Corsair iCUE"], apps.RgbSuites); // HWiNFO is a sensor tool, not an RGB suite
        Assert.Equal(("Google Chrome", 20, 20 * (gb / 8)), apps.Browsers.Single());
        Assert.Equal(["Windows Update"], apps.BusyUpdaters); // OneDrive is idle
        Assert.True(BackgroundAppCatalog.IsHeavyBrowser(20, gb));
        Assert.False(BackgroundAppCatalog.IsHeavyBrowser(7, gb));
    }

    [Fact]
    public void Mixed_memory_kits_are_told_apart_from_a_matched_pair()
    {
        MemoryModuleInfo Module(string part, long gb, int speed) =>
            new("DIMM", "G.Skill", null, "OK", null, null, gb * 1024 * 1024 * 1024, speed, speed, "DDR5", "DIMM", part, null, null, 64, 64, new Dictionary<string, string>());

        Assert.False(MemoryKitRules.IsMixed([Module("F5-6000J3636F16G", 16, 6000), Module("F5-6000J3636F16G ", 16, 6000)]));
        Assert.True(MemoryKitRules.IsMixed([Module("F5-6000J3636F16G", 16, 6000), Module("CMK32GX5M2B5600C36", 16, 5600)]));
        Assert.True(MemoryKitRules.IsMixed([Module("", 16, 6000), Module("", 8, 6000)]));
        Assert.False(MemoryKitRules.IsMixed([Module("F5-6000J3636F16G", 16, 6000)]));
    }

    [Fact]
    public void Game_drives_get_their_kind_from_the_storage_list_and_free_space_from_the_volume()
    {
        InstalledGame Game(string path) => new("id" + path, GameLauncher.Steam, "1", "Game", path, null, null, null, null, null, null, null, null);
        var games = new[] { Game(@"C:\Games\A"), Game(@"D:\SteamLibrary\B"), Game(@"D:\SteamLibrary\C") };
        var drives = new[]
        {
            new PhysicalDrive { Number = 0, Media = DriveMedia.Ssd, Volumes = ["C:"] },
            new PhysicalDrive { Number = 1, Media = DriveMedia.Hdd, Volumes = ["D:"] },
        };
        var volumes = new[]
        {
            new LogicalDriveInfo("C:", "C:", null, "NTFS", null, 1_000_000_000_000, 15_000_000_000, null, false, new Dictionary<string, string>()),
            new LogicalDriveInfo("D:", "D:", null, "NTFS", null, 4_000_000_000_000, 1_200_000_000_000, null, false, new Dictionary<string, string>()),
        };

        var result = GameReadyFactsBuilder.Drives(games, drives, volumes);

        Assert.Equal([("C:", DriveMedia.Ssd, 1), ("D:", DriveMedia.Hdd, 2)], result.Select(d => (d.Drive, d.Media, d.GameCount)).ToList());
        var report = GameReadyRules.Evaluate(Baseline() with { GameDrives = result }, En);
        Assert.Equal("2 games are on a hard disk", report.Items.Single(i => i.Id == "storage-hdd").Title);
        Assert.Contains(report.Items, i => i.Id == "storage-space-c" && i.Status == GameReadyStatus.Suggestion); // 15 GB free
        Assert.True(GameReadyRules.IsLowOnSpace(50_000_000_000, 1_000_000_000_000)); // 5 %
        Assert.False(GameReadyRules.IsLowOnSpace(null, 1_000_000_000_000));
        Assert.True(GameReadyFactsBuilder.SameLetter(@"d:\", "D:"));
        Assert.False(GameReadyFactsBuilder.SameLetter("DATA", "D:"));
    }

    [Fact]
    public void Power_mode_overlay_guids_map_to_the_windows_names()
    {
        Assert.Equal(PowerModeOverlay.BestPowerEfficiency, PowerModeRules.FromOverlay(PowerModeRules.BestPowerEfficiency));
        Assert.Equal(PowerModeOverlay.BestPerformance, PowerModeRules.FromOverlay(PowerModeRules.BestPerformance));
        Assert.Equal(PowerModeOverlay.Balanced, PowerModeRules.FromOverlay(Guid.Empty));
        Assert.Equal(PowerModeOverlay.Unknown, PowerModeRules.FromOverlay(null));
    }

    // ----- the whole check in demo mode -----------------------------------------------------------------------------

    [Fact]
    public async Task The_demo_pc_shows_the_classic_mistakes_and_a_directx_fix_can_be_undone()
    {
        var system = new DemoGameReadySystem();
        var service = new GameReadyService(new DemoDisplayService(), new DemoOptimizationService([new DemoLatencyActionsProvider()]), new DemoMemoryService(),
            new DemoHardwareInventoryService(), new DemoGameLibraryService(), new DemoDriveInventoryService(), new DemoProcessService(),
            new DemoSecurityStatusService(), system, NullLogger<GameReadyService>.Instance)
        { ProcessSampleGap = TimeSpan.Zero };

        var facts = await service.CheckAsync(null, CancellationToken.None);
        var report = GameReadyRules.Evaluate(facts, En);
        var problems = report.Items.Where(i => i.NeedsAttention).Select(i => i.Id).ToList();

        Assert.StartsWith("display-refresh-", report.Items[0].Id); // the 240 Hz monitor at 60 Hz is the biggest win
        Assert.Contains("memory-xmp", problems);
        Assert.Contains("game-dvr", problems);
        Assert.Contains("hags", problems);
        Assert.Contains("storage-hdd", problems);
        Assert.Contains("apps-rgb", problems);
        Assert.Contains(report.Items, i => i.Id == "apps-overlays");
        Assert.Equal(GameReadyStatus.Info, report.Items.Single(i => i.Id == "gpu-link").Status);

        var fix = report.Items.Single(i => i.Id == "windowed-optimizations").Fix!;
        var change = await service.ApplyAsync(fix, CancellationToken.None);
        Assert.True(change.Result.Success);
        Assert.Equal("SwapEffectUpgradeEnable=1;", await system.ReadGlobalSettingsAsync(CancellationToken.None));
        Assert.True((await change.Undo!(CancellationToken.None)).Success);
        Assert.Null(await system.ReadGlobalSettingsAsync(CancellationToken.None));

        var refused = await service.ApplyAsync(new GameReadyFix(GameReadyFixKind.DirectXSetting, "x", "SomethingElse"), CancellationToken.None);
        Assert.False(refused.Result.Success);
    }

    // ----- helpers --------------------------------------------------------------------------------------------------

    private static GameReadyGpu Gpu(PcieLink? link) =>
        new("NVIDIA GeForce RTX 4070", false, "NVIDIA", Now.DateTime.AddDays(-30), link, ResizableBarState.On);

    /// <summary>A gaming desktop with everything set up well.</summary>
    private static GameReadyFacts Baseline() => new()
    {
        Displays = [new GameReadyDisplay("D1", "Main", true, false, new DisplayMode(2560, 1440, 165, 32), [new(2560, 1440, 165, 32), new(2560, 1440, 60, 32)], false, false)],
        DirectXGlobalSettings = "SwapEffectUpgradeEnable=1;",
        HagsOn = true,
        GameModeOn = true,
        GameDvrOn = false,
        PointerPrecisionOn = false,
        HighPerformancePlan = true,
        HasBattery = false,
        OnBattery = false,
        BatterySaverOn = false,
        PowerMode = PowerModeOverlay.Balanced,
        Gpus = [Gpu(new PcieLink(16, 16, 4, 4))],
        Memory = new GameReadyMemory("Enabled", 6000, 6000, 2, true, false, "DDR5"),
        MousePollingHz = 1000,
        Now = Now,
    };
}
