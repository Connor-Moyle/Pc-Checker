using PcChecker.Core.Common;
using PcChecker.Core.Gaming;
using PcChecker.Core.Power;
using PcChecker.Core.Settings;
using Xunit;

namespace PcChecker.Core.Tests;

public class FrameStatsTests
{
    private static FrameStats Steady(double fps, double seconds, double start = 0)
    {
        var stats = new FrameStats();
        double interval = 1000.0 / fps;
        int frames = (int)(seconds * fps);
        for (int i = 0; i <= frames; i++)
        {
            stats.Add(start + i * interval);
        }

        return stats;
    }

    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void Steady_presents_give_that_fps_and_frame_time(double fps)
    {
        var stats = Steady(fps, 5);
        var snapshot = stats.Snapshot(stats.LastTimestampMs!.Value);

        Assert.NotNull(snapshot);
        Assert.Equal(fps, snapshot!.Value.Fps, 1);
        Assert.Equal(1000.0 / fps, snapshot.Value.FrameTimeMs, 2);
        Assert.Equal(fps, snapshot.Value.OnePercentLowFps!.Value, 1);
    }

    [Fact]
    public void One_percent_low_reflects_the_slowest_frames()
    {
        // 990 frames at 10 ms (100 FPS) with 10 stutters of 50 ms (20 FPS) spread through them.
        var stats = new FrameStats();
        double t = 0;
        stats.Add(t);
        for (int i = 1; i <= 1000; i++)
        {
            t += i % 100 == 0 ? 50 : 10;
            stats.Add(t);
        }

        var snapshot = stats.Snapshot(t)!.Value;
        Assert.Equal(20, snapshot.OnePercentLowFps!.Value, 1);
        Assert.True(snapshot.Fps > 80, $"Average FPS should stay high, was {snapshot.Fps}");
    }

    [Fact]
    public void Too_few_frames_give_no_one_percent_low()
    {
        var stats = Steady(60, 0.25);
        var snapshot = stats.Snapshot(stats.LastTimestampMs!.Value);
        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Value.OnePercentLowFps);
    }

    [Fact]
    public void No_presents_for_two_seconds_means_no_data()
    {
        var stats = Steady(144, 3);
        double last = stats.LastTimestampMs!.Value;

        Assert.NotNull(stats.Snapshot(last + 1999));
        Assert.Null(stats.Snapshot(last + 2001));
        Assert.Null(new FrameStats().Snapshot(0));
    }

    [Fact]
    public void A_long_gap_resets_the_history()
    {
        var stats = Steady(30, 10);
        int before = stats.Count;
        double resume = stats.LastTimestampMs!.Value + 5000;
        stats.Add(resume);
        stats.Add(resume + 10);

        Assert.True(before > 200);
        Assert.Equal(2, stats.Count);
        Assert.Equal(100, stats.Snapshot(resume + 10)!.Value.Fps, 1);
    }

    [Fact]
    public void Out_of_order_and_invalid_timestamps_are_ignored()
    {
        var stats = new FrameStats();
        stats.Add(100);
        stats.Add(50);
        stats.Add(double.NaN);
        stats.Add(110);
        Assert.Equal(2, stats.Count);
    }

    [Fact]
    public void Only_the_last_30_seconds_are_kept()
    {
        var stats = Steady(100, 45);
        Assert.InRange(stats.Count, 2990, 3010);
    }

    [Fact]
    public void Present_counter_prefers_runtime_events_and_falls_back_to_kernel()
    {
        var counter = new PresentCounter();
        for (int i = 0; i <= 100; i++)
        {
            counter.Add(PresentSource.Kernel, i * 20.0); // 50 FPS from the kernel only (e.g. a Vulkan game)
        }

        Assert.Equal(PresentSource.Kernel, counter.ActiveSource(2000));
        Assert.Equal(50, counter.Snapshot(2000)!.Value.Fps, 1);

        for (int i = 0; i <= 100; i++)
        {
            counter.Add(PresentSource.Runtime, i * 10.0); // 100 FPS reported by DXGI for the same window
        }

        Assert.Equal(PresentSource.Runtime, counter.ActiveSource(1000));
        Assert.Equal(100, counter.Snapshot(1000)!.Value.Fps, 1);
    }

    [Fact]
    public void Present_events_are_classified_by_provider_and_id()
    {
        Assert.Equal(PresentSource.Runtime, PresentEvents.Classify(PresentEvents.DxgiProvider, 42));
        Assert.Equal(PresentSource.Runtime, PresentEvents.Classify(PresentEvents.DxgiProvider, 55));
        Assert.Equal(PresentSource.Runtime, PresentEvents.Classify(PresentEvents.D3D9Provider, 1));
        Assert.Equal(PresentSource.Kernel, PresentEvents.Classify(PresentEvents.DxgKrnlProvider, 184));
        Assert.Null(PresentEvents.Classify(PresentEvents.DxgiProvider, 43));
        Assert.Null(PresentEvents.Classify(Guid.NewGuid(), 42));
    }
}

public class GameProfileMatcherTests
{
    private static GameProfile Profile(string name, string exe, bool enabled = true) => new() { Name = name, ExeName = exe, Enabled = enabled };

    [Theory]
    [InlineData(@"C:\Games\ELDEN RING\Game\eldenring.exe")]
    [InlineData("ELDENRING.EXE")]
    [InlineData("eldenring")]
    [InlineData("/mnt/games/eldenring.exe")]
    [InlineData("\"D:\\Steam\\eldenring.exe\"")]
    public void Matches_by_exe_name_ignoring_case_and_folders(string seen)
    {
        var profiles = new[] { Profile("Elden Ring", "eldenring.exe") };
        Assert.Same(profiles[0], GameProfileMatcher.Match(seen, profiles));
    }

    [Fact]
    public void Different_exe_or_empty_input_does_not_match()
    {
        var profiles = new[] { Profile("Elden Ring", "eldenring.exe") };
        Assert.Null(GameProfileMatcher.Match("eldenring_launcher.exe", profiles));
        Assert.Null(GameProfileMatcher.Match("", profiles));
        Assert.Null(GameProfileMatcher.Match((string?)null, profiles));
        Assert.Null(GameProfileMatcher.Match("eldenring.exe", null));
    }

    [Fact]
    public void First_enabled_profile_wins()
    {
        var disabled = Profile("Old", "game.exe", enabled: false);
        var first = Profile("Competitive", "game.exe");
        var second = Profile("Casual", "Game.EXE");
        Assert.Same(first, GameProfileMatcher.Match("game.exe", new[] { disabled, first, second }));
        Assert.Null(GameProfileMatcher.Match("game.exe", new[] { disabled }));
    }

    [Fact]
    public void Foreground_app_matches_by_path_then_process_name()
    {
        var profiles = new[] { Profile("Cyberpunk", "Cyberpunk2077.exe") };
        var withPath = new ForegroundApp(1, "whatever", @"C:\x\Cyberpunk2077.exe", "Cyberpunk 2077", true);
        var withoutPath = new ForegroundApp(1, "Cyberpunk2077", null, "Cyberpunk 2077", true);
        Assert.NotNull(GameProfileMatcher.Match(withPath, profiles));
        Assert.NotNull(GameProfileMatcher.Match(withoutPath, profiles));
    }

    [Theory]
    [InlineData("explorer", true)]
    [InlineData("PcChecker", true)]
    [InlineData("StartMenuExperienceHost.exe", true)]
    [InlineData("eldenring", false)]
    public void Shell_and_own_process_are_ignored(string name, bool ignored)
    {
        Assert.Equal(ignored, GameProfileMatcher.IsIgnoredProcess(name));
        Assert.True(GameProfileMatcher.IsIgnoredProcess("eldenring", 1234, 1234));
        Assert.True(GameProfileMatcher.IsIgnoredProcess("eldenring", 0));
    }

    [Fact]
    public void Suggested_name_prefers_a_short_window_title()
    {
        Assert.Equal("ELDEN RING", GameProfileMatcher.SuggestName(@"C:\g\eldenring.exe", "ELDEN RING"));
        Assert.Equal("eldenring", GameProfileMatcher.SuggestName(@"C:\g\eldenring.exe", ""));
        Assert.Equal("eldenring", GameProfileMatcher.SuggestName(@"C:\g\eldenring.exe", new string('x', 80)));
    }
}

public class GameProfileRulesTests
{
    [Fact]
    public void Validate_explains_what_is_missing_or_wrong()
    {
        var existing = new List<GameProfile> { new() { Name = "Elden Ring", ExeName = "eldenring.exe" } };
        Assert.NotNull(GameProfileRules.Validate(new GameProfile { Name = "", ExeName = "a.exe" }, existing));
        Assert.NotNull(GameProfileRules.Validate(new GameProfile { Name = "A", ExeName = "" }, existing));
        Assert.NotNull(GameProfileRules.Validate(new GameProfile { Name = "A", ExeName = "explorer.exe" }, existing));
        Assert.NotNull(GameProfileRules.Validate(new GameProfile { Name = "elden ring", ExeName = "x.exe" }, existing));
        Assert.Null(GameProfileRules.Validate(new GameProfile { Name = "Elden Ring", ExeName = "eldenring.exe" }, existing, originalName: "Elden Ring"));
        Assert.Null(GameProfileRules.Validate(new GameProfile { Name = "Cyberpunk", ExeName = "Cyberpunk2077" }, existing));
    }

    [Fact]
    public void Upsert_adds_or_replaces_in_place_with_a_canonical_exe_name()
    {
        var settings = new GamingSettings();
        GameProfileRules.Upsert(settings, new GameProfile { Name = "A", ExeName = "a" });
        GameProfileRules.Upsert(settings, new GameProfile { Name = " B ", ExeName = @"C:\games\b.EXE", FanProfileName = " ", PowerPlanId = Guid.Empty });
        GameProfileRules.Upsert(settings, new GameProfile { Name = "A2", ExeName = "a.exe" }, originalName: "a");

        Assert.Equal(new[] { "A2", "B" }, settings.Profiles.Select(p => p.Name));
        Assert.Equal("a.exe", settings.Profiles[0].ExeName);
        Assert.Equal("b.exe", settings.Profiles[1].ExeName);
        Assert.Null(settings.Profiles[1].FanProfileName);
        Assert.Null(settings.Profiles[1].PowerPlanId);
        Assert.True(GameProfileRules.Remove(settings, "b"));
        Assert.False(GameProfileRules.Remove(settings, "b"));
    }

    [Fact]
    public void Describe_lists_what_changes()
    {
        var profile = new GameProfile { Name = "X", ExeName = "x.exe", OverlayPresetName = "Full", FanProfileName = "Performance", PowerPlanId = PowerPlanParser.HighPerformance };
        Assert.Equal("Overlay: Full · Fans: Performance · Power: High performance", GameProfileRules.Describe(profile, PowerPlanParser.DescribeKnown));
        Assert.Equal("Overlay hidden · kept after the game closes", GameProfileRules.Describe(new GameProfile { ShowOverlay = false, RevertOnExit = false }));
    }
}

public class ProfileSwitchPlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 21, 4, 0, TimeSpan.Zero);

    private static GamingSettings Settings(params GameProfile[] profiles) => new() { Profiles = profiles.ToList() };

    private static GameProfile Game(string name, string exe, bool revert = true) => new() { Name = name, ExeName = exe, RevertOnExit = revert };

    private static ForegroundApp App(int pid, string exe, bool fullscreen = true) => new(pid, exe.Replace(".exe", string.Empty), null, exe, fullscreen);

    [Fact]
    public void Enters_only_after_the_game_stays_in_front_for_the_debounce()
    {
        var settings = Settings(Game("Elden Ring", "eldenring.exe"));
        var planner = new ProfileSwitchPlanner();

        Assert.True(planner.OnForeground(App(10, "eldenring.exe"), settings, T0).IsNone);
        Assert.True(planner.OnTick(T0.AddSeconds(1.5)).IsNone);
        var decision = planner.OnTick(T0.AddSeconds(2));

        Assert.Equal("Elden Ring", decision.Enter?.Name);
        Assert.Equal(10, decision.EnterProcessId);
        Assert.True(decision.CaptureBaseline);
        Assert.Null(decision.Exit);
        Assert.Equal(T0.AddSeconds(2), planner.Active!.Since);
    }

    [Fact]
    public void Alt_tabbing_past_a_game_does_not_switch()
    {
        var settings = Settings(Game("Elden Ring", "eldenring.exe"));
        var planner = new ProfileSwitchPlanner();
        planner.OnForeground(App(10, "eldenring.exe"), settings, T0);
        planner.OnForeground(App(20, "chrome.exe"), settings, T0.AddSeconds(1));

        Assert.True(planner.OnTick(T0.AddSeconds(5)).IsNone);
        Assert.Null(planner.Active);
    }

    [Fact]
    public void Losing_focus_keeps_the_profile_and_process_exit_reverts_it()
    {
        var settings = Settings(Game("Elden Ring", "eldenring.exe"));
        var planner = new ProfileSwitchPlanner();
        planner.OnForeground(App(10, "eldenring.exe"), settings, T0);
        planner.OnTick(T0.AddSeconds(2));

        Assert.True(planner.OnForeground(App(20, "chrome.exe"), settings, T0.AddSeconds(10)).IsNone);
        Assert.True(planner.OnForeground(App(10, "eldenring.exe"), settings, T0.AddSeconds(20)).IsNone);
        Assert.True(planner.OnProcessExited(99, T0.AddSeconds(30)).IsNone);

        var exit = planner.OnProcessExited(10, T0.AddSeconds(40));
        Assert.Equal("Elden Ring", exit.Exit?.Name);
        Assert.True(exit.RevertExit);
        Assert.Null(exit.Enter);
        Assert.Null(planner.Active);
    }

    [Fact]
    public void Relaunching_the_game_applies_the_profile_again()
    {
        var settings = Settings(Game("Elden Ring", "eldenring.exe"));
        var planner = new ProfileSwitchPlanner();
        planner.OnForeground(App(10, "eldenring.exe"), settings, T0);
        planner.OnTick(T0.AddSeconds(2));
        planner.OnProcessExited(10, T0.AddSeconds(60));

        planner.OnForeground(App(11, "eldenring.exe"), settings, T0.AddSeconds(70));
        var again = planner.OnTick(T0.AddSeconds(72));
        Assert.Equal(11, again.EnterProcessId);
        Assert.True(again.CaptureBaseline);
    }

    [Fact]
    public void A_second_game_takes_over_and_the_first_is_undone()
    {
        var settings = Settings(Game("Elden Ring", "eldenring.exe"), Game("Cyberpunk", "Cyberpunk2077.exe"));
        var planner = new ProfileSwitchPlanner();
        planner.OnForeground(App(10, "eldenring.exe"), settings, T0);
        planner.OnTick(T0.AddSeconds(2));

        planner.OnForeground(App(30, "Cyberpunk2077.exe"), settings, T0.AddSeconds(10));
        var handover = planner.OnTick(T0.AddSeconds(12));

        Assert.Equal("Elden Ring", handover.Exit?.Name);
        Assert.True(handover.RevertExit);
        Assert.Equal("Cyberpunk", handover.Enter?.Name);
        Assert.False(handover.CaptureBaseline);
        Assert.Equal(30, planner.Active!.ProcessId);

        // The first game closing in the background changes nothing.
        Assert.True(planner.OnProcessExited(10, T0.AddSeconds(20)).IsNone);
    }

    [Fact]
    public void Handover_from_a_profile_that_keeps_its_changes_recaptures_the_baseline()
    {
        var settings = Settings(Game("Keep", "keep.exe", revert: false), Game("Other", "other.exe"));
        var planner = new ProfileSwitchPlanner();
        planner.OnForeground(App(1, "keep.exe"), settings, T0);
        planner.OnTick(T0.AddSeconds(2));
        planner.OnForeground(App(2, "other.exe"), settings, T0.AddSeconds(3));
        var decision = planner.OnTick(T0.AddSeconds(5));

        Assert.False(decision.RevertExit);
        Assert.True(decision.CaptureBaseline);
    }

    [Fact]
    public void Only_when_fullscreen_ignores_windowed_games()
    {
        var settings = Settings(Game("Elden Ring", "eldenring.exe"));
        settings.OnlyWhenFullscreen = true;
        var planner = new ProfileSwitchPlanner();

        planner.OnForeground(App(10, "eldenring.exe", fullscreen: false), settings, T0);
        Assert.True(planner.OnTick(T0.AddSeconds(5)).IsNone);

        planner.OnForeground(App(10, "eldenring.exe", fullscreen: true), settings, T0.AddSeconds(6));
        Assert.NotNull(planner.OnTick(T0.AddSeconds(8)).Enter);
    }

    [Fact]
    public void Turning_auto_switch_off_or_removing_the_profile_undoes_it()
    {
        var settings = Settings(Game("Elden Ring", "eldenring.exe"));
        var planner = new ProfileSwitchPlanner();
        planner.OnForeground(App(10, "eldenring.exe"), settings, T0);
        planner.OnTick(T0.AddSeconds(2));

        settings.AutoSwitch = false;
        var off = planner.OnSettingsChanged(settings, T0.AddSeconds(5));
        Assert.Equal("Elden Ring", off.Exit?.Name);
        Assert.True(off.RevertExit);
        Assert.True(planner.OnForeground(App(10, "eldenring.exe"), settings, T0.AddSeconds(6)).IsNone);

        settings.AutoSwitch = true;
        planner.OnForeground(App(10, "eldenring.exe"), settings, T0.AddSeconds(7));
        planner.OnTick(T0.AddSeconds(9));
        settings.Profiles.Clear();
        Assert.NotNull(planner.OnSettingsChanged(settings, T0.AddSeconds(10)).Exit);
        Assert.Null(planner.Active);
    }

    [Fact]
    public void Editing_the_active_profile_reapplies_it()
    {
        var settings = Settings(Game("Elden Ring", "eldenring.exe"));
        var planner = new ProfileSwitchPlanner();
        planner.OnForeground(App(10, "eldenring.exe"), settings, T0);
        planner.OnTick(T0.AddSeconds(2));
        Assert.True(planner.OnSettingsChanged(settings, T0.AddSeconds(3)).IsNone);

        settings.Profiles[0] = new GameProfile { Name = "Elden Ring", ExeName = "eldenring.exe", FanProfileName = "Silent" };
        var decision = planner.OnSettingsChanged(settings, T0.AddSeconds(4));
        Assert.Equal("Silent", decision.Enter?.FanProfileName);
        Assert.True(decision.RevertExit);
        Assert.Equal(10, decision.EnterProcessId);
    }

    [Fact]
    public void Deactivate_reverts_per_profile_unless_forced()
    {
        var settings = Settings(Game("Keep", "keep.exe", revert: false));
        var planner = new ProfileSwitchPlanner();
        planner.OnForeground(App(1, "keep.exe"), settings, T0);
        planner.OnTick(T0.AddSeconds(2));

        Assert.False(planner.Deactivate().RevertExit);
        Assert.True(planner.Deactivate().IsNone);
    }
}

public class GamingSupportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PcCheckerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Gaming_settings_round_trip()
    {
        var svc = new JsonSettingsService(_dir);
        svc.Current.Gaming.AutoSwitch = false;
        svc.Current.Gaming.OnlyWhenFullscreen = true;
        svc.Current.Gaming.Profiles.Add(new GameProfile
        {
            Name = "Elden Ring", ExeName = "eldenring.exe", ExePath = @"C:\Games\eldenring.exe", Enabled = false,
            OverlayPresetName = "Minimal", ShowOverlay = false, FanProfileName = "Silent", PowerPlanId = PowerPlanParser.HighPerformance, RevertOnExit = false,
        });
        await svc.SaveAsync();

        var reloaded = new JsonSettingsService(_dir);
        await reloaded.LoadAsync();
        var gaming = reloaded.Current.Gaming;
        var p = Assert.Single(gaming.Profiles);
        Assert.False(gaming.AutoSwitch);
        Assert.True(gaming.OnlyWhenFullscreen);
        Assert.Equal(("Elden Ring", "eldenring.exe", @"C:\Games\eldenring.exe"), (p.Name, p.ExeName, p.ExePath));
        Assert.False(p.Enabled);
        Assert.Equal("Minimal", p.OverlayPresetName);
        Assert.False(p.ShowOverlay);
        Assert.Equal("Silent", p.FanProfileName);
        Assert.Equal(PowerPlanParser.HighPerformance, p.PowerPlanId);
        Assert.False(p.RevertOnExit);
    }

    [Fact]
    public void Gaming_defaults_switch_automatically_with_no_profiles()
    {
        var gaming = AppSettings.CreateDefault().Gaming;
        Assert.True(gaming.AutoSwitch);
        Assert.False(gaming.OnlyWhenFullscreen);
        Assert.Empty(gaming.Profiles);
        Assert.True(new GameProfile().ShowOverlay && new GameProfile().RevertOnExit && new GameProfile().Enabled);
    }

    [Theory]
    [InlineData(OverlayMetricIds.Fps, 143.6, "144")]
    [InlineData(OverlayMetricIds.FpsLow1, 98.2, "98")]
    [InlineData(OverlayMetricIds.FrameTime, 6.944, "6.9 ms")]
    [InlineData(OverlayMetricIds.FrameTime, 16.67, "16.7 ms")]
    public void Frame_metrics_format_for_the_overlay(string id, double value, string expected)
    {
        Assert.Equal(expected, OverlayMetricFormatter.Format(id, value));
        Assert.Equal(OverlayMetricFormatter.Placeholder, OverlayMetricFormatter.Format(id, null));
        Assert.True(OverlayMetricFormatter.IsFrameMetric(id));
    }

    [Fact]
    public void Frame_metrics_are_known_and_off_by_default()
    {
        var defaults = OverlayMetricIds.CreateDefaultMetrics();
        foreach (var id in new[] { OverlayMetricIds.Fps, OverlayMetricIds.FrameTime, OverlayMetricIds.FpsLow1 })
        {
            Assert.Contains(OverlayMetricIds.All, m => m.Id == id);
            Assert.False(defaults.Single(m => m.Id == id).Enabled);
        }

        // Settings saved before the FPS counter existed gain the new metrics, switched off.
        var old = new List<OverlayMetric> { new() { Id = OverlayMetricIds.CpuTemp, Enabled = true, Order = 0 } };
        var normalized = OverlayPresetCatalog.Normalize(old);
        Assert.Equal(OverlayMetricIds.CpuTemp, normalized[0].Id);
        Assert.False(normalized.Single(m => m.Id == OverlayMetricIds.Fps).Enabled);

        var full = OverlayPresetCatalog.BuiltIn().Single(p => p.Name == OverlayPresetCatalog.Full);
        Assert.Equal(OverlayMetricIds.Fps, OverlayPresetCatalog.EnabledInOrder(full.Metrics)[0].Id);
    }

    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000D1C5_phys_0_eng_0_engtype_3D", true, 1234)]
    [InlineData("pid_88_luid_0x0_0x1_phys_0_eng_3_engtype_VideoDecode", true, 88)]
    [InlineData("luid_0x0_phys_0", false, 0)]
    [InlineData("pid_abc_luid", false, 0)]
    public void Gpu_engine_instance_names_give_the_process_id(string instance, bool ok, int pid)
    {
        Assert.Equal(ok, ProcessMetrics.TryParseGpuEnginePid(instance, out int parsed));
        Assert.Equal(pid, parsed);
    }

    [Fact]
    public void Running_apps_rank_by_gpu_then_cpu_and_fullscreen_is_detected()
    {
        var apps = new[]
        {
            new RunningApp(1, "chrome", null, "YouTube", 12, 3, 100),
            new RunningApp(2, "eldenring", null, "ELDEN RING", 20, 95, 100),
            new RunningApp(3, "notepad", null, "x", 1, null, 10),
        };
        Assert.Equal(new[] { 2, 1, 3 }, ProcessMetrics.Rank(apps).Select(a => a.Pid));
        Assert.Equal("eldenring.exe", apps[1].ExeName);
        Assert.Equal(25, ProcessMetrics.CpuPercent(TimeSpan.Zero, TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(500), 8), 3);

        Assert.True(ProcessMetrics.CoversMonitor(0, 0, 2560, 1440, 0, 0, 2560, 1440));
        Assert.True(ProcessMetrics.CoversMonitor(2559, -1, 5121, 1441, 2560, 0, 5120, 1440));
        Assert.False(ProcessMetrics.CoversMonitor(0, 0, 1920, 1040, 0, 0, 1920, 1080));
    }

    [Fact]
    public async Task Demo_gaming_services_provide_sample_data()
    {
        var tracker = new DemoForegroundAppTracker();
        Assert.Equal(DemoForegroundAppTracker.GamePid, tracker.Current!.Pid);
        Assert.True(tracker.Current.IsFullscreen);
        var apps = await tracker.GetRunningAppsAsync(CancellationToken.None);
        Assert.Equal("Cyberpunk2077.exe", apps[0].ExeName);

        using var frames = new DemoFrameRateMonitor();
        Assert.True(frames.IsAvailable);
        Assert.InRange(frames.Latest!.Fps, 130, 160);
        Assert.Equal(DemoForegroundAppTracker.GameExe, frames.Latest.ProcessName);

        using var power = new DemoPowerService();
        var profiles = new DemoGameProfileService(power);
        Assert.Equal(2, profiles.SampleProfiles.Count);
        Assert.All(profiles.SampleProfiles, p => Assert.Null(GameProfileRules.Validate(p, [])));
        Assert.NotNull(GameProfileMatcher.Match(tracker.Current, profiles.SampleProfiles));
    }
}
