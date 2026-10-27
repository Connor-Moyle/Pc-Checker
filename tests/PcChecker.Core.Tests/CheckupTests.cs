using Microsoft.Extensions.Logging.Abstractions;
using PcChecker.Core.Checkup;
using PcChecker.Core.Cleanup;
using PcChecker.Core.Common;
using PcChecker.Core.Debloat;
using PcChecker.Core.DiskSpace;
using PcChecker.Core.Drivers;
using PcChecker.Core.Health;
using PcChecker.Core.History;
using PcChecker.Core.Logging;
using PcChecker.Core.Optimization;
using PcChecker.Core.Performance;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;
using PcChecker.Core.Startup;
using PcChecker.Core.WindowsUpdate;
using Xunit;

namespace PcChecker.Core.Tests;

public class CheckupRankingTests
{
    internal static CheckupItem Item(string id, Severity severity, RiskLevel risk = RiskLevel.Safe, CheckupFixKind fix = CheckupFixKind.None,
        CheckupSource source = CheckupSource.Health, string? topic = null, double magnitude = 0, bool suggest = true) =>
        new(id, source, severity, "Title " + id, "Why", "Benefit", risk, fix, fix == CheckupFixKind.None ? [] : ["target-" + id])
        {
            Topic = topic,
            Magnitude = magnitude,
            SuggestByDefault = suggest,
        };

    [Fact]
    public void Severity_always_outranks_fixes_risk_and_size()
    {
        var bestLow = Item("low", Severity.Low, RiskLevel.Safe, CheckupFixKind.CleanSafeJunk, magnitude: 1000);
        var worstMedium = Item("medium", Severity.Medium, RiskLevel.Advanced);
        var worstCritical = Item("critical", Severity.Critical, RiskLevel.Advanced);
        var bestHigh = Item("high", Severity.High, RiskLevel.Safe, CheckupFixKind.InstallWindowsUpdates, magnitude: 50);

        var ranked = CheckupPrioritizer.Rank([bestLow, worstMedium, bestHigh, worstCritical]);

        Assert.Equal(["critical", "high", "medium", "low"], ranked.Select(i => i.Id));
        Assert.True(CheckupPrioritizer.Score(worstMedium) > CheckupPrioritizer.Score(bestLow));
    }

    [Fact]
    public void Within_a_severity_fixable_and_safer_and_bigger_come_first()
    {
        var openOnly = Item("open", Severity.Medium);
        var moderateFix = Item("moderate", Severity.Medium, RiskLevel.Moderate, CheckupFixKind.InstallDriverUpdates);
        var safeFix = Item("safe", Severity.Medium, RiskLevel.Safe, CheckupFixKind.ApplyOptimization);
        var safeBigFix = Item("big", Severity.Medium, RiskLevel.Safe, CheckupFixKind.CleanSafeJunk, magnitude: 4);

        var ranked = CheckupPrioritizer.Rank([openOnly, moderateFix, safeFix, safeBigFix]);

        Assert.Equal(["big", "safe", "moderate", "open"], ranked.Select(i => i.Id));
    }

    [Fact]
    public void Rank_keeps_one_item_per_id_and_per_topic()
    {
        var healthRestart = Item("health.restart", Severity.Medium, topic: CheckupItemMapper.RestartTopic);
        var updateRestart = Item("wu.restart", Severity.Medium, source: CheckupSource.WindowsUpdate, topic: CheckupItemMapper.RestartTopic);
        var pendingOpen = Item("health.pending", Severity.Low, topic: CheckupItemMapper.PendingUpdatesTopic);
        var pendingFix = Item("wu.pending", Severity.Low, fix: CheckupFixKind.InstallWindowsUpdates, source: CheckupSource.WindowsUpdate,
            topic: CheckupItemMapper.PendingUpdatesTopic);
        var duplicate = Item("wu.pending", Severity.Critical);

        var ranked = CheckupPrioritizer.Rank([healthRestart, updateRestart, pendingOpen, pendingFix, duplicate]);

        Assert.Single(ranked, i => i.Topic == CheckupItemMapper.RestartTopic);
        Assert.Equal("health.restart", ranked.Single(i => i.Topic == CheckupItemMapper.RestartTopic).Id); // same score: page order decides
        var pending = Assert.Single(ranked, i => i.Id == "wu.pending");
        Assert.True(pending.HasFix); // the first occurrence of an id wins, and the fixable one wins the topic
        Assert.DoesNotContain(ranked, i => i.Id == "health.pending");
    }
}

public class CheckupMappingTests
{
    private static CleanupCategory Category(string id, RiskLevel risk) => new(id, id + " name", "", risk, [], [], true, true);

    private static CategoryScanResult Found(CleanupCategory category, long bytes) => new(category, 10, bytes, [], []);

    [Fact]
    public void Cleanup_counts_only_scanned_safe_categories_and_scales_severity()
    {
        var temp = Category("user.temp", RiskLevel.Safe);
        var prefetch = Category("prefetch", RiskLevel.Moderate);
        var old = Category(CleanupRules.WindowsOldId, RiskLevel.Safe);
        var shader = Category("shader.cache", RiskLevel.Safe);
        var scan = new CleanupScanResult([Found(temp, 3 * ByteSize.Gigabyte), Found(prefetch, 5 * ByteSize.Gigabyte), Found(old, 20 * ByteSize.Gigabyte),
            Found(shader, 0)], 0, DateTimeOffset.Now);

        var item = Assert.Single(CheckupItemMapper.FromCleanup(scan, ["user.temp", "prefetch", CleanupRules.WindowsOldId, "shader.cache"]));

        Assert.Equal(CheckupFixKind.CleanSafeJunk, item.Fix);
        Assert.Equal(["user.temp"], item.Targets);
        Assert.Equal(Severity.Medium, item.Severity);
        Assert.Equal(RiskLevel.Safe, item.Risk);
        Assert.Contains("3", item.Title);

        var tiny = new CleanupScanResult([Found(temp, 10 * ByteSize.Megabyte)], 0, DateTimeOffset.Now);
        Assert.Empty(CheckupItemMapper.FromCleanup(tiny, ["user.temp"]));
        var notScanned = CheckupItemMapper.FromCleanup(scan, ["prefetch"]);
        Assert.Empty(notScanned);
    }

    [Fact]
    public void Windows_update_installs_like_install_all_and_flags_restart_and_settings_only()
    {
        var security = new PendingUpdate("a", "2026-10 Cumulative Update", "KB1", 100, UpdateClassification.Cumulative, UpdateSeverity.Critical,
            UpdateRestartNeed.Yes, false, null);
        var optional = new PendingUpdate("b", "Feature update", null, 100, UpdateClassification.Feature, UpdateSeverity.Unspecified,
            UpdateRestartNeed.Yes, false, null) { IsOptional = true };
        var interactive = new PendingUpdate("c", "Needs questions", null, 100, UpdateClassification.Other, UpdateSeverity.Unspecified,
            UpdateRestartNeed.No, false, null) { NeedsUserInput = true };
        var status = new WindowsUpdateStatus(null, null, RestartPending: true, UpdatePauseState.NotPaused, null, UpdatePolicyStatus.None, 35, false, [],
            DateTimeOffset.Now);

        var items = CheckupItemMapper.FromWindowsUpdate(status, new PendingUpdateScan([security, optional, interactive], [], DateTimeOffset.Now));

        var install = Assert.Single(items, i => i.Fix == CheckupFixKind.InstallWindowsUpdates);
        Assert.Equal(["a"], install.Targets);
        Assert.Equal(Severity.High, install.Severity);
        Assert.Equal(RiskLevel.Safe, install.Risk);
        Assert.True(install.MayNeedRestart);
        Assert.Contains(items, i => i.Topic == CheckupItemMapper.RestartTopic && !i.HasFix);
        Assert.Contains(items, i => i.Id == "windowsupdate.settings" && !i.HasFix);

        var definitions = new PendingUpdate("d", "Defender definitions", null, 1, UpdateClassification.Definition, UpdateSeverity.Unspecified,
            UpdateRestartNeed.No, true, null);
        var quiet = CheckupItemMapper.FromWindowsUpdate(null, new PendingUpdateScan([definitions], [], DateTimeOffset.Now));
        Assert.Equal(Severity.Low, Assert.Single(quiet).Severity);
    }

    [Fact]
    public void Driver_updates_are_a_moderate_fix_and_device_problems_open_the_page()
    {
        var update = new DriverUpdateInfo("u1", "NVIDIA - Display", null, "1.0", null, 10, DriverUpdateSource.WindowsUpdate, null, null, null, false, false, true);
        var installed = update with { Id = "u2", IsInstalled = true };
        var item = Assert.Single(CheckupItemMapper.FromDriverUpdates(new DriverUpdateCheckResult([update, installed], [], [], DateTimeOffset.Now)));
        Assert.Equal(RiskLevel.Moderate, item.Risk);
        Assert.Equal(CheckupFixKind.InstallDriverUpdates, item.Fix);
        Assert.Equal(["u1"], item.Targets);
        Assert.True(item.MayNeedRestart);

        var broken = new DriverInfo("USB Controller", "USB\\1", "USB", "Vendor", "1.0", DateTime.Today, "oem3.inf", true, null, false, [], "Error", 28, null);
        var unplugged = broken with { DeviceName = "Old phone", IsPresent = false };
        var problems = Assert.Single(CheckupItemMapper.FromDriverScan(new DriverScanResult([broken, unplugged], [], DateTimeOffset.Now)));
        Assert.False(problems.HasFix);
        Assert.Equal(CheckupItemMapper.DevicesTopic, problems.Topic);
        Assert.Contains("USB Controller", problems.Title);
    }

    [Fact]
    public void Startup_suggests_only_enabled_high_impact_apps_and_never_preselects_them()
    {
        static CheckupStartupEntry Entry(string name, bool enabled, string kind, string impact) =>
            new(new StartupItem(name, name + ".exe", @"HKCU\Run", enabled, null, kind), new StartupInsight(null, null, null, impact, "Heavy launcher."));

        var entries = new List<CheckupStartupEntry>
        {
            Entry("Steam", true, StartupKinds.Registry, StartupImpactRules.High),
            Entry("Disabled", false, StartupKinds.Registry, StartupImpactRules.High),
            Entry("Small", true, StartupKinds.Folder, StartupImpactRules.Low),
            Entry("Task", true, StartupKinds.Task, StartupImpactRules.High),
        };
        entries.AddRange(Enumerable.Range(1, 8).Select(i => Entry($"App{i}", true, StartupKinds.Folder, StartupImpactRules.High)));

        var items = CheckupItemMapper.FromStartup(entries);

        Assert.Equal(CheckupItemMapper.MaxStartupSuggestions, items.Count);
        Assert.All(items, i => Assert.Equal(CheckupFixKind.DisableStartupItem, i.Fix));
        Assert.All(items, i => Assert.False(CheckupSelection.IsSelectedByDefault(i)));
        Assert.DoesNotContain(items, i => i.Title.Contains("Disabled") || i.Title.Contains("Task") || i.Title.Contains("Small"));
        Assert.Equal(CheckupItemMapper.StartupKey(entries[4].Item), items[0].Targets.Single()); // App1: alphabetical, so the list is stable
    }

    [Fact]
    public void Performance_findings_use_reversible_actions_and_skip_hints_and_startup_counts()
    {
        var actions = new List<OptimizationAction>
        {
            new("tempfiles.clean", "Clean temporary files", "", "Disk", RiskLevel.Safe, true, false, false),
            new("storagesense.enable", "Turn on Storage Sense", "", "Disk", RiskLevel.Safe, true, false, false),
            new("sfc.scannow", "Check system files", "", "Repair", RiskLevel.Safe, false, false, false),
            new("power.ultimate", "Ultimate plan", "", "Power", RiskLevel.Moderate, true, false, true),
        };
        static SlowdownFinding Finding(string id, Severity severity, params string[] actionIds) =>
            new(id, FindingCategory.Configuration, severity, id, "detail", "Slower PC", actionIds, false);
        var report = PerformanceReport.Empty() with
        {
            Findings =
            [
                Finding("disk.low.C:", Severity.High, "tempfiles.clean", "storagesense.enable"),
                Finding("repair", Severity.Medium, "sfc.scannow"),
                Finding("power", Severity.Medium, "power.ultimate"),
                Finding("gamebar.on", Severity.Info, "storagesense.enable"),
                Finding("startup.many", Severity.High, "startup.review"),
            ],
        };

        var items = CheckupItemMapper.FromPerformance(report, actions, skipStartupCount: true);

        Assert.Equal(3, items.Count);
        var disk = items.Single(i => i.Id == "performance.disk.low.C:");
        Assert.Equal(CheckupFixKind.ApplyOptimization, disk.Fix);
        Assert.Equal(["storagesense.enable"], disk.Targets);

        // The fix is one setting, not the cure for a full drive: it is named, and never ticked for the user.
        Assert.Equal("Turn on Storage Sense", disk.FixAction);
        Assert.False(CheckupSelection.IsSelectedByDefault(disk));
        Assert.Null(items.Single(i => i.Id == "performance.repair").FixAction);
        Assert.Equal(CheckupFixKind.None, items.Single(i => i.Id == "performance.repair").Fix);
        Assert.Equal(RiskLevel.Advanced, items.Single(i => i.Id == "performance.power").Risk);
        Assert.Contains(CheckupItemMapper.FromPerformance(report, actions, skipStartupCount: false), i => i.Id == "performance.startup.many");
    }

    [Fact]
    public void Recommended_settings_and_old_drivers_list_only_what_is_safe_and_still_to_do()
    {
        var copilot = DebloatCatalog.Find("ai.copilot")!;
        var telemetry = DebloatCatalog.Find("privacy.telemetry")!;
        var tips = DebloatCatalog.Find("ads.tips")!;
        var advanced = copilot with { Id = "advanced.one", AdvancedOnly = true };
        var states = new Dictionary<string, TweakState>
        {
            [copilot.Id] = new(copilot.Id, TweakStatus.NotApplied, null),
            [telemetry.Id] = new(telemetry.Id, TweakStatus.Applied, null),
            [tips.Id] = new(tips.Id, TweakStatus.Partial, null),
            [advanced.Id] = new(advanced.Id, TweakStatus.NotApplied, null),
        };

        var debloat = Assert.Single(CheckupItemMapper.FromDebloat([copilot, telemetry, tips, advanced], states));
        Assert.Equal([copilot.Id, tips.Id], debloat.Targets);
        Assert.False(CheckupSelection.IsSelectedByDefault(debloat));

        var safe = new DriverCleanupItem(DriverCleanupKind.Package, "oem12.inf", "Old GPU driver", DriverCleanupStatus.SafeToRemove, "Superseded") { SizeBytes = 300 * ByteSize.Megabyte };
        var probably = safe with { Id = "oem13.inf", Status = DriverCleanupStatus.ProbablySafe };
        var inbox = safe with { Id = "usbport.inf", IsInbox = true };
        var scan = new DriverCleanupScanResult([safe, probably, inbox], DriverCleanupSummary.Empty, [], DateTimeOffset.Now);

        var drivers = Assert.Single(CheckupItemMapper.FromOldDrivers(scan));
        Assert.Equal([CheckupItemMapper.OldDriverKey(safe)], drivers.Targets);
        Assert.Contains("300", drivers.Benefit);
        Assert.True(CheckupSelection.IsSelectedByDefault(drivers));
    }

    [Fact]
    public void Health_items_become_open_only_entries_with_topics()
    {
        var items = new List<HealthItem>
        {
            new("Security", HealthStatus.Critical, "Antivirus is off", "No protection.", "Turn it on.", HealthItem.NoMetrics),
            new("Windows Update", HealthStatus.Warning, "A restart is needed to finish updates", "Pending.", null, HealthItem.NoMetrics),
            new("Battery", HealthStatus.Good, "Battery is fine", "", null, HealthItem.NoMetrics),
        };
        var report = HealthReport.Empty() with { Items = items };

        var mapped = CheckupItemMapper.FromHealth(report);

        Assert.Equal(2, mapped.Count);
        Assert.All(mapped, i => Assert.False(i.HasFix));
        Assert.Equal(Severity.Critical, mapped[0].Severity);
        Assert.EndsWith("Turn it on.", mapped[0].Why);
        Assert.Equal(CheckupItemMapper.RestartTopic, mapped[1].Topic);
    }
}

public class CheckupSelectionAndPlanTests
{
    private static CheckupItem Item(string id, CheckupFixKind fix, RiskLevel risk = RiskLevel.Safe, bool suggest = true) =>
        CheckupRankingTests.Item(id, Severity.Medium, risk, fix, suggest: suggest);

    [Fact]
    public void Only_safe_suggested_fixes_are_ticked_and_advanced_needs_advanced_mode()
    {
        var safe = Item("safe", CheckupFixKind.CleanSafeJunk);
        var moderate = Item("moderate", CheckupFixKind.InstallDriverUpdates, RiskLevel.Moderate);
        var advanced = Item("advanced", CheckupFixKind.ApplyOptimization, RiskLevel.Advanced);
        var personal = Item("personal", CheckupFixKind.ApplyRecommendedSettings, suggest: false);
        var openOnly = Item("open", CheckupFixKind.None);

        Assert.Equal(["safe"], CheckupSelection.DefaultSelection([safe, moderate, advanced, personal, openOnly], advancedMode: true));
        Assert.True(CheckupSelection.CanSelect(moderate, advancedMode: false));
        Assert.False(CheckupSelection.CanSelect(advanced, advancedMode: false));
        Assert.True(CheckupSelection.CanSelect(advanced, advancedMode: true));
        Assert.False(CheckupSelection.CanSelect(openOnly, advancedMode: true));
        Assert.NotNull(CheckupSelection.WhyNotSelectable(advanced, advancedMode: false));
        Assert.Null(CheckupSelection.WhyNotSelectable(personal, advancedMode: false));
    }

    [Fact]
    public void Plan_orders_steps_groups_by_service_and_skips_what_cannot_be_fixed()
    {
        var updates = Item("updates", CheckupFixKind.InstallWindowsUpdates);
        var junk = Item("junk", CheckupFixKind.CleanSafeJunk);
        var opt1 = Item("opt1", CheckupFixKind.ApplyOptimization);
        var opt2 = Item("opt2", CheckupFixKind.ApplyOptimization);
        var drivers = Item("drivers", CheckupFixKind.InstallDriverUpdates, RiskLevel.Moderate);
        var advanced = Item("advanced", CheckupFixKind.ApplyOptimization, RiskLevel.Advanced);
        var openOnly = Item("open", CheckupFixKind.None);

        var plan = CheckupBatchPlanner.Plan([updates, junk, opt1, drivers, opt2, advanced, openOnly, junk], advancedMode: false, restorePointsOn: true);

        Assert.Equal([CheckupFixKind.CleanSafeJunk, CheckupFixKind.ApplyOptimization, CheckupFixKind.InstallDriverUpdates, CheckupFixKind.InstallWindowsUpdates],
            plan.Steps.Select(s => s.Kind));
        Assert.Equal(["opt1", "opt2"], plan.Steps[1].Items.Select(i => i.Id));
        Assert.Equal(5, plan.ItemCount);
        Assert.Equal(["advanced", "open"], plan.Skipped.Select(s => s.Item.Id));
        Assert.False(plan.Steps.Single(s => s.Kind == CheckupFixKind.InstallDriverUpdates).CanStopPartWay);
        Assert.True(plan.Steps.Single(s => s.Kind == CheckupFixKind.InstallWindowsUpdates).CanStopPartWay);
    }

    [Fact]
    public void Plan_asks_for_one_shared_restore_point_only_when_a_step_would_make_one()
    {
        var junk = Item("junk", CheckupFixKind.CleanSafeJunk);
        var startup = Item("startup", CheckupFixKind.DisableStartupItem);
        var opt1 = Item("opt1", CheckupFixKind.ApplyOptimization);
        var opt2 = Item("opt2", CheckupFixKind.ApplyOptimization);
        var settings = Item("settings", CheckupFixKind.ApplyRecommendedSettings);
        var updates = Item("updates", CheckupFixKind.InstallWindowsUpdates);

        Assert.False(CheckupBatchPlanner.Plan([junk, startup], false, restorePointsOn: true).SharedRestorePoint);

        var policyOn = CheckupBatchPlanner.Plan([junk, opt1, opt2, updates], false, restorePointsOn: true);
        Assert.True(policyOn.SharedRestorePoint);
        Assert.Equal(3, policyOn.SeparateRestorePoints);

        Assert.False(CheckupBatchPlanner.Plan([opt1, updates], false, restorePointsOn: false).SharedRestorePoint);

        var debloat = CheckupBatchPlanner.Plan([settings, updates], false, restorePointsOn: false);
        Assert.True(debloat.SharedRestorePoint);
        Assert.Equal(1, debloat.SeparateRestorePoints);
    }

    [Fact]
    public void Plan_tells_light_changes_apart_from_ones_that_run_without_a_restore_point()
    {
        var junk = CheckupRankingTests.Item("junk", Severity.Medium, fix: CheckupFixKind.CleanSafeJunk);
        var startup = CheckupRankingTests.Item("startup", Severity.Low, fix: CheckupFixKind.DisableStartupItem);
        var optimization = CheckupRankingTests.Item("opt", Severity.Medium, fix: CheckupFixKind.ApplyOptimization);
        var updates = CheckupRankingTests.Item("wu", Severity.High, fix: CheckupFixKind.InstallWindowsUpdates);

        var light = CheckupBatchPlanner.Plan([junk, startup], advancedMode: false, restorePointsOn: false);
        Assert.False(light.SharedRestorePoint);
        Assert.True(light.OnlyLightChanges);

        var settingOff = CheckupBatchPlanner.Plan([junk, optimization, updates], advancedMode: false, restorePointsOn: false);
        Assert.False(settingOff.SharedRestorePoint);
        Assert.False(settingOff.OnlyLightChanges);

        Assert.True(CheckupBatchPlanner.Plan([junk, optimization, updates], advancedMode: false, restorePointsOn: true).SharedRestorePoint);
    }

    [Fact]
    public void After_fixes_the_report_drops_fixed_items_and_installed_updates()
    {
        static PendingUpdate Update(string id) =>
            new(id, "Update " + id, null, 1, UpdateClassification.Security, UpdateSeverity.Important, UpdateRestartNeed.Yes, false, null);
        var installs = CheckupRankingTests.Item("wu", Severity.High, fix: CheckupFixKind.InstallWindowsUpdates) with { Targets = ["a"] };
        var junk = CheckupRankingTests.Item("junk", Severity.Medium, fix: CheckupFixKind.CleanSafeJunk);
        var health = CheckupRankingTests.Item("health", Severity.Critical);
        var results = CheckupScanResults.Empty(DateTimeOffset.Now) with
        {
            PendingUpdates = new PendingUpdateScan([Update("a"), Update("b")], [], DateTimeOffset.Now),
        };
        var report = new CheckupReport(results, [health, installs, junk]);

        var fixes = new CheckupFixReport([new(installs, true, "Installed"), new(junk, false, "Failed")], ChangeBatchOutcome.None, false);
        var after = CheckupAfterFix.Apply(report, fixes);

        Assert.Equal(["health", "junk"], after.Items.Select(i => i.Id));
        Assert.Equal(["b"], after.Results.PendingUpdates!.Updates.Select(u => u.Id));
        Assert.Equal(report.Captured, after.Captured);

        var nothing = new CheckupFixReport([new(junk, false, "Failed")], ChangeBatchOutcome.None, false);
        Assert.Same(report, CheckupAfterFix.Apply(report, nothing));
    }

    [Fact]
    public void Progress_combines_parts_into_one_steady_percentage()
    {
        Assert.Equal(0, CheckupProgress.Overall(0, 8, null));
        Assert.Equal(50, CheckupProgress.Overall(4, 8, 0));
        Assert.Equal(56.3, CheckupProgress.Overall(4, 8, 50));
        Assert.Equal(100, CheckupProgress.Overall(7, 8, 100));
        Assert.Equal(100, CheckupProgress.Overall(12, 8, 250));
        Assert.Equal(0, CheckupProgress.Overall(0, 0, 50));

        var update = CheckupProgress.Combine("Check-up", 2, 8, "Drivers", new ProgressUpdate("Searching", 40, "Windows Update"));
        Assert.Equal("Check-up 3 of 8: Drivers", update.Stage);
        Assert.Equal("Searching — Windows Update", update.Detail);
        Assert.Equal(30, update.Percent);
    }
}

public class DashboardGlanceTests
{
    private static WindowsUpdateStatus Status(bool restart = false, bool disabled = false, bool paused = false, DateTimeOffset? lastCheck = null) =>
        new(lastCheck, null, restart, paused ? new UpdatePauseState(true, DateTimeOffset.Now, DateTimeOffset.Now.AddDays(7)) : UpdatePauseState.NotPaused,
            null, UpdatePolicyStatus.None, 35, disabled, [], DateTimeOffset.Now);

    [Fact]
    public void Update_tile_puts_service_off_then_restart_then_waiting_updates_first()
    {
        var pending = new PendingUpdateScan([new PendingUpdate("a", "Update", null, 1, UpdateClassification.Security, UpdateSeverity.Important,
            UpdateRestartNeed.No, false, null)], [], DateTimeOffset.Now);
        var empty = new PendingUpdateScan([], [], DateTimeOffset.Now);

        Assert.Equal(UpdateGlanceState.ServiceOff, DashboardGlance.Updates(Status(restart: true, disabled: true), pending).State);
        Assert.Equal(UpdateGlanceState.RestartPending, DashboardGlance.Updates(Status(restart: true), pending).State);
        var waiting = DashboardGlance.Updates(Status(paused: true), pending);
        Assert.Equal(UpdateGlanceState.UpdatesWaiting, waiting.State);
        Assert.Equal(1, waiting.Waiting);
        Assert.Equal(UpdateGlanceState.Paused, DashboardGlance.Updates(Status(paused: true), empty).State);
        Assert.Equal(UpdateGlanceState.UpToDate, DashboardGlance.Updates(Status(), empty).State);
        Assert.Equal(UpdateGlanceState.CheckedByWindows, DashboardGlance.Updates(Status(lastCheck: DateTimeOffset.Now), null).State);
        Assert.Equal(UpdateGlanceState.Unknown, DashboardGlance.Updates(null, null).State);
    }

    [Fact]
    public void Fullest_drive_and_temperature_trend()
    {
        var c = new DriveSpaceInfo(@"C:\", "System", "NTFS", 1000, 300, true);
        var d = new DriveSpaceInfo(@"D:\", "Games", "NTFS", 2000, 100, false);
        var e = new DriveSpaceInfo(@"E:\", "Empty", "NTFS", 0, 0, false);
        Assert.Equal(@"D:\", DashboardGlance.FullestDrive([c, d, e])!.RootPath);
        Assert.Equal(95, DashboardGlance.UsedPercent(d));
        Assert.Null(DashboardGlance.FullestDrive([e]));

        var start = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 24 * 60).Select(m => new HistorySample(start.AddMinutes(m), 60,
            new Dictionary<string, double> { [HistoryMetrics.CpuTemp] = 40 + (m % 60 == 30 ? 40 : 0) })).ToList();
        var trend = DashboardGlance.Trend(new HistoryData(start, start.AddHours(24), samples), HistoryMetrics.CpuTemp);

        Assert.True(trend.HasData);
        Assert.InRange(trend.Points.Count, 40, DashboardGlance.TrendPoints + 1);
        Assert.Equal(80, trend.Peak);
        Assert.Empty(DashboardGlance.Trend(new HistoryData(start, start.AddHours(24), samples), HistoryMetrics.GpuTemp).Points);
    }
}

public sealed class ChangeBatchTests : IDisposable
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

    internal (ChangeGuard Guard, CountingRestore Restore, JsonActionJournal Journal, JsonSettingsService Settings) Create(bool before = true)
    {
        var settings = new JsonSettingsService(_dir);
        settings.Current.RestorePoints.CreateBeforeChanges = before;
        settings.Current.RestorePoints.CreateAfterChanges = false;
        var restore = new CountingRestore();
        var journal = new JsonActionJournal(Path.Combine(_dir, "actions.jsonl"));
        return (new ChangeGuard(restore, journal, settings, NullLogger<ChangeGuard>.Instance), restore, journal, settings);
    }

    [Fact]
    public void Resolve_drops_restore_points_inside_a_batch_and_shares_its_sequence()
    {
        var options = new ChangeOptions(true, true, "Tests", "t", true, null);
        Assert.Equal((true, true, (long?)null), ChangeBatch.Resolve(options, false, false, null));
        Assert.Equal((false, false, (long?)7), ChangeBatch.Resolve(options, true, true, new ChangeBatchState("batch", 7)));
        Assert.Equal((true, false, (long?)null), ChangeBatch.Resolve(ChangeOptions.For("Tests", "t"), true, false, null));
    }

    [Fact]
    public async Task A_batch_makes_one_restore_point_for_all_its_changes()
    {
        var (guard, restore, journal, _) = Create();
        var mustHave = ChangeOptions.For("Drivers", "x") with { OnRestorePointFailed = RestorePointFailedBehavior.Abort };
        var outcome = await guard.RunBatchAsync("Check-up fixes", true, async ct =>
        {
            var a = await guard.RunGuardedAsync("First", ChangeOptions.For("Optimize", "a"), _ => Task.FromResult(OperationResult.Ok()), null, ct);
            var b = await guard.RunGuardedAsync("Second", new ChangeOptions(true, null, "Debloat", "b", true, null), _ => Task.FromResult(OperationResult.Ok()), null, ct);
            var c = await guard.RunGuardedAsync("Third", mustHave, _ => Task.FromResult(OperationResult.Ok()), null, ct);
            Assert.True(a.Success && b.Success && c.Success);
        }, null, CancellationToken.None);

        Assert.Equal(1, outcome.RestorePointSequence);
        Assert.Single(restore.Created);
        var records = await journal.ReadAsync();
        Assert.Equal(4, records.Count);
        Assert.All(records, r => Assert.Equal(1, r.RestorePointSequence));
        Assert.Null(ChangeBatch.Current);

        // After the batch, changes make their own restore points again.
        await guard.RunGuardedAsync("Later", ChangeOptions.For("Optimize", "c"), _ => Task.FromResult(OperationResult.Ok()), null, CancellationToken.None);
        Assert.Equal(2, restore.Created.Count);
    }

    [Fact]
    public async Task Without_the_shared_point_each_change_follows_its_own_rules()
    {
        var (guard, restore, _, _) = Create();
        restore.Fail = true;
        var mustHave = ChangeOptions.For("Drivers", "x") with { OnRestorePointFailed = RestorePointFailedBehavior.Abort };
        OperationResult? guarded = null;
        var outcome = await guard.RunBatchAsync("Check-up fixes", true, async ct =>
            guarded = await guard.RunGuardedAsync("Install", mustHave, _ => Task.FromResult(OperationResult.Ok()), null, ct), null, CancellationToken.None);

        Assert.Null(outcome.RestorePointSequence);
        Assert.NotNull(outcome.RestorePointWarning);
        Assert.False(guarded!.Success); // the driver install still refuses to run without a restore point
        Assert.Equal(2, restore.Attempts);
    }

    internal sealed class CountingRestore : IRestorePointService
    {
        public List<string> Created { get; } = [];
        public bool Fail { get; set; }
        public int Attempts { get; private set; }

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<OperationResult> EnableAsync(string drive, CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok());
        public Task<IReadOnlyList<RestorePoint>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RestorePoint>>([]);

        public Task<OperationResult<RestorePoint>> CreateAsync(string description, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Fail)
            {
                return Task.FromResult(OperationResult<RestorePoint>.Fail("System Restore is off"));
            }

            Created.Add(description);
            return Task.FromResult(OperationResult<RestorePoint>.Ok(new RestorePoint(Created.Count, description, DateTimeOffset.Now, "MODIFY_SETTINGS")));
        }
    }
}

public sealed class CheckupServiceTests : IDisposable
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
    public async Task Demo_check_up_builds_a_ranked_list_and_fixes_the_default_selection_behind_one_restore_point()
    {
        var settings = new JsonSettingsService(_dir);
        settings.Current.RestorePoints.CreateBeforeChanges = true;
        settings.Current.RestorePoints.CreateAfterChanges = false;
        var restore = new ChangeBatchTests.CountingRestore();
        var guard = new ChangeGuard(restore, new JsonActionJournal(Path.Combine(_dir, "actions.jsonl")), settings, NullLogger<ChangeGuard>.Instance);
        var startup = new DemoStartupService();
        var optimization = new DemoOptimizationService([], startup);
        var service = new CheckupService(new DemoHealthService(), new DemoTempFileScanner(), new DemoTempFileCleaner(), new DemoDriverService(),
            new DemoWindowsUpdateService(), startup, new DemoPerformanceAnalyzer(startup), optimization, new DemoDebloatService(optimization),
            new DemoDriverCleanupService(), guard, settings, NullLogger<CheckupService>.Instance)
        {
            StartupSizeProbe = _ => null,
        };
        var updates = new List<ProgressUpdate>();
        var progress = new SyncProgress(updates.Add);

        var report = await service.RunAsync(progress, CancellationToken.None);

        Assert.Same(report, service.Last);
        Assert.NotEmpty(report.Items);
        Assert.Empty(report.Results.Gaps);
        Assert.Equal(report.Items, CheckupPrioritizer.Rank(report.Items));
        Assert.Contains(report.Items, i => i.Source == CheckupSource.Cleanup && i.HasFix);
        Assert.All(updates.Where(u => u.Percent is not null), u => Assert.InRange(u.Percent!.Value, 0, 100));

        var ticked = report.Items.Where(i => CheckupSelection.DefaultSelection(report.Items, false).Contains(i.Id)).ToList();
        Assert.All(ticked, i => Assert.Equal(RiskLevel.Safe, i.Risk));
        var plan = CheckupBatchPlanner.Plan(ticked, advancedMode: false, restorePointsOn: true);
        var fixes = await service.FixAsync(report, plan, null, CancellationToken.None);

        Assert.Equal(plan.ItemCount, fixes.Results.Count);
        Assert.False(fixes.Cancelled);
        Assert.True(restore.Created.Count <= 1);
        var fixedIds = fixes.Results.Where(r => r.Success).Select(r => r.Item.Id).ToList();
        Assert.NotEmpty(fixedIds);
        Assert.DoesNotContain(service.Last!.Items, i => fixedIds.Contains(i.Id));
    }

    [Fact]
    public async Task A_service_time_out_fails_its_item_without_counting_as_a_cancel()
    {
        var settings = new JsonSettingsService(_dir);
        settings.Current.RestorePoints.CreateBeforeChanges = false;
        var guard = new ChangeGuard(new ChangeBatchTests.CountingRestore(), new JsonActionJournal(Path.Combine(_dir, "actions.jsonl")), settings,
            NullLogger<ChangeGuard>.Instance);
        var startup = new DemoStartupService();
        var demoOptimization = new DemoOptimizationService([], startup);
        var timingOut = new TimingOutOptimization(new OptimizationAction("slow", "Slow setting", "", "Test", RiskLevel.Safe, true, false, false));
        var service = new CheckupService(new DemoHealthService(), new DemoTempFileScanner(), new DemoTempFileCleaner(), new DemoDriverService(),
            new DemoWindowsUpdateService(), startup, new DemoPerformanceAnalyzer(startup), timingOut, new DemoDebloatService(demoOptimization),
            new DemoDriverCleanupService(), guard, settings, NullLogger<CheckupService>.Instance);
        var item = new CheckupItem("performance.slow", CheckupSource.Performance, Severity.Medium, "Slow", "Why", "Benefit", RiskLevel.Safe,
            CheckupFixKind.ApplyOptimization, ["slow"]);
        var report = new CheckupReport(CheckupScanResults.Empty(DateTimeOffset.Now), [item]);

        var fixes = await service.FixAsync(report, CheckupBatchPlanner.Plan([item], false, false), null, CancellationToken.None);

        Assert.False(fixes.Cancelled);
        var result = Assert.Single(fixes.Results);
        Assert.False(result.Success);
        Assert.DoesNotContain("cancelled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TimingOutOptimization(OptimizationAction action) : IOptimizationService
    {
        public IReadOnlyList<OptimizationAction> Actions { get; } = [action];

        public Task<ActionState> GetStateAsync(string actionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult> ApplyAsync(string actionId, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("The request timed out.");

        public Task<OperationResult> RevertAsync(string actionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OperationResult> SetStartupItemEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult> SetServiceStartModeAsync(string serviceName, string startMode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SyncProgress(Action<ProgressUpdate> report) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value)
        {
            lock (this)
            {
                report(value);
            }
        }
    }
}
