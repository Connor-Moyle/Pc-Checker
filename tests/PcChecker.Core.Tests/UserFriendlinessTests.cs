using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using PcChecker.Core.Checkup;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Restore;
using PcChecker.Core.Sensors.PawnIo;
using PcChecker.Core.Settings;
using PcChecker.Core.Shell;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>Page help, keyboard shortcuts and Simple versus Advanced mode in the shell.</summary>
public class HelpAndShortcutTests
{
    [Fact]
    public void Every_navigation_page_has_help_text()
    {
        var pages = NavigationKeys();
        Assert.True(pages.Count >= 30, $"Parsed only {pages.Count} pages from NavigationCatalog.cs");
        var missing = pages.Where(p => HelpCatalog.Find(p) is null).ToList();
        Assert.True(missing.Count == 0,
            "These pages have no help yet; add an entry to src/PcChecker.Core/Shell/HelpCatalog.cs: " + string.Join(", ", missing));
    }

    [Fact]
    public void Help_entries_are_unique_and_belong_to_real_pages()
    {
        var pages = NavigationKeys();
        Assert.Equal(HelpCatalog.All.Count, HelpCatalog.All.Select(h => h.PageKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(HelpCatalog.All, h => Assert.Contains(h.PageKey, pages));
    }

    [Fact]
    public void Help_is_short_and_plain()
    {
        foreach (var help in HelpCatalog.All)
        {
            int sentences = Sentences(help.WhatItDoes) + Sentences(help.FirstStep);
            Assert.InRange(sentences, 2, 4);
            Assert.True(help.WhatItDoes.Length <= 200, $"{help.PageKey}: what it does is too long");
            Assert.True(help.FirstStep.Length <= 200, $"{help.PageKey}: first step is too long");
            Assert.DoesNotContain("{", help.WhatItDoes + help.FirstStep);
        }
    }

    [Fact]
    public void Every_docs_link_points_at_a_file_and_heading_that_exist()
    {
        var docs = Path.Combine(RepoRoot(), "docs");
        foreach (var help in HelpCatalog.All)
        {
            var file = Path.Combine(docs, help.DocsFile);
            Assert.True(File.Exists(file), $"{help.PageKey}: docs/{help.DocsFile} does not exist");
            if (help.DocsHeading is null)
            {
                continue;
            }

            var anchors = Regex.Matches(File.ReadAllText(file), @"^#{1,6}\s+(.+?)\s*$", RegexOptions.Multiline)
                .Select(m => HelpCatalog.Anchor(m.Groups[1].Value))
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(anchors.Contains(HelpCatalog.Anchor(help.DocsHeading)), $"{help.PageKey}: no heading \"{help.DocsHeading}\" in docs/{help.DocsFile}");
        }
    }

    [Theory]
    [InlineData("Keyboard & Mouse", "keyboard--mouse")]
    [InlineData("Check-up", "check-up")]
    [InlineData("Windows Update", "windows-update")]
    [InlineData("  Disk space ", "disk-space")]
    public void Anchors_match_github(string heading, string anchor) => Assert.Equal(anchor, HelpCatalog.Anchor(heading));

    [Fact]
    public void Docs_urls_point_at_the_repository()
    {
        Assert.Equal(HelpCatalog.RepositoryUrl + "/blob/main/docs/FEATURES.md#keyboard--mouse", HelpCatalog.Find("input")!.DocsUrl);
        Assert.Equal(HelpCatalog.RepositoryUrl + "/blob/main/docs/NETWORK.md", HelpCatalog.Find("network")!.DocsUrl);
        Assert.Null(HelpCatalog.Find("no-such-page"));
        Assert.Null(HelpCatalog.Find(null));
        Assert.NotNull(HelpCatalog.Find("DASHBOARD"));
    }

    [Fact]
    public void The_shortcuts_list_covers_the_global_keys_and_real_pages()
    {
        var keys = KeyboardShortcutCatalog.Global.Select(s => s.Keys).ToList();
        Assert.Contains("Ctrl+K", keys);
        Assert.Contains("F1", keys);
        Assert.Contains("F5", keys);
        Assert.Contains("?", keys);
        Assert.Contains("Ctrl+Tab", keys);
        Assert.Contains("F6", keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());

        var pages = NavigationKeys();
        Assert.All(KeyboardShortcutCatalog.PagesWithShortcuts, p => Assert.Contains(p, pages));
        Assert.Contains(KeyboardShortcutCatalog.ForPage("processes"), s => s.Keys == "Delete");
        Assert.Contains(KeyboardShortcutCatalog.ForPage("processes"), s => s.Keys == "Enter");
        Assert.Empty(KeyboardShortcutCatalog.ForPage("dashboard"));
        Assert.Empty(KeyboardShortcutCatalog.ForPage(null));
    }

    [Fact]
    public void Advanced_pages_are_real_pages_and_the_lookup_ignores_case()
    {
        var pages = NavigationKeys();
        Assert.All(SimpleModeRules.AdvancedPageKeys, p => Assert.Contains(p, pages));
        Assert.True(SimpleModeRules.IsAdvancedPage("Overclock"));
        Assert.False(SimpleModeRules.IsAdvancedPage("dashboard"));
        Assert.False(SimpleModeRules.IsAdvancedPage(null));

        // Everyday pages never get the chip.
        Assert.DoesNotContain("checkup", SimpleModeRules.AdvancedPageKeys);
        Assert.DoesNotContain("cleanup", SimpleModeRules.AdvancedPageKeys);
    }

    [Theory]
    // advanced, seen before, shown this session, closed this session, screenshots → shows
    [InlineData(false, false, false, false, false, true)]   // first launch in Simple mode
    [InlineData(false, true, true, false, false, true)]     // still up later in that same session
    [InlineData(false, true, false, false, false, false)]   // a later launch: shown once already
    [InlineData(false, true, true, true, false, false)]     // closed by the user
    [InlineData(true, false, false, false, false, false)]   // Advanced mode
    [InlineData(false, false, false, false, true, false)]   // screenshot run
    public void The_simple_mode_hint_shows_once_in_simple_mode_only(bool advanced, bool seen, bool shownNow, bool closed, bool screenshots, bool shows) =>
        Assert.Equal(shows, SimpleModeRules.ShowSimpleModeHint(advanced, seen, shownNow, closed, screenshots));

    [Fact]
    public async Task The_hint_flag_and_check_up_time_survive_a_save_but_stay_out_of_setup_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PcCheckerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new JsonSettingsService(dir);
            Assert.False(settings.Current.General.SimpleModeHintDismissed);
            settings.Current.General.SimpleModeHintDismissed = true;
            settings.Current.General.LastCheckupUtc = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
            await settings.SaveAsync();

            var reloaded = new JsonSettingsService(dir);
            await reloaded.LoadAsync();
            Assert.True(reloaded.Current.General.SimpleModeHintDismissed);
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), reloaded.Current.General.LastCheckupUtc);
            Assert.Contains("General.SimpleModeHintDismissed", PcChecker.Core.SetupBackup.SetupMachineFields.All);
            Assert.Contains("General.LastCheckupUtc", PcChecker.Core.SetupBackup.SetupMachineFields.All);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
            }
        }
    }

    private static int Sentences(string text) => Regex.Matches(text.Trim(), @"[.!?](\s|$)").Count;

    /// <summary>The page keys of src/PcChecker.App/Services/NavigationCatalog.cs, parsed from the source (the app needs Windows).</summary>
    internal static List<string> NavigationKeys()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PcChecker.App", "Services", "NavigationCatalog.cs"));
        return Regex.Matches(source, @"\bPage\(""[^""]+"",\s*""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcChecker.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

/// <summary>The Dashboard's "What needs attention" card.</summary>
public class AttentionRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Facts for a healthy PC that had a check-up yesterday with nothing left to do.</summary>
    private static AttentionFacts Healthy() => new()
    {
        Now = Now,
        LastCheckup = Now.AddDays(-1),
        Drives = [new AttentionDrive("C:", "Windows", 55, 400L << 30, true)],
        HeavyStartupApps = 0,
        Updates = new UpdateGlance(UpdateGlanceState.UpToDate, 0, Now, null),
        PawnIo = PawnIoState.Running,
        DeviceProblems = 0,
    };

    [Fact]
    public void Heavy_startup_apps_follow_the_live_list_with_the_check_ups_estimate()
    {
        var steam = new PcChecker.Core.Performance.StartupItem("Steam", "C:\\Steam\\steam.exe", "HKCU Run", true, null, "Registry");
        var helper = new PcChecker.Core.Performance.StartupItem("Tiny helper", "C:\\Tools\\tinyhelper.exe", "HKCU Run", true, null, "Registry");
        var fromCheckup = new List<CheckupStartupEntry>
        {
            new(steam, new PcChecker.Core.Startup.StartupInsight(null, true, null, PcChecker.Core.Startup.StartupImpactRules.High, "big")),
            new(helper, new PcChecker.Core.Startup.StartupInsight(null, true, null, PcChecker.Core.Startup.StartupImpactRules.High, "measured")),
        };

        // Both on: the check-up's estimate counts both as heavy.
        Assert.Equal(2, AttentionRules.CountHeavyStartupApps([steam, helper], fromCheckup));

        // The user turned Steam off on the Startup page after the check-up: the card drops it straight away.
        Assert.Equal(1, AttentionRules.CountHeavyStartupApps([steam with { Enabled = false }, helper], fromCheckup));

        // No live list (the read failed): the check-up's own count; nothing known: null.
        Assert.Equal(2, AttentionRules.CountHeavyStartupApps(null, fromCheckup));
        Assert.Null(AttentionRules.CountHeavyStartupApps(null, null));
        Assert.Equal(0, AttentionRules.CountHeavyStartupApps([], fromCheckup));
    }

    [Fact]
    public void A_healthy_pc_needs_nothing()
    {
        Assert.Empty(AttentionRules.Build(Healthy()));
    }

    [Fact]
    public void Unknown_facts_never_produce_items()
    {
        var items = AttentionRules.Build(new AttentionFacts { Now = Now, LastCheckup = Now });
        Assert.Empty(items);
    }

    [Fact]
    public void No_check_up_yet_suggests_running_one()
    {
        var item = Assert.Single(AttentionRules.Build(Healthy() with { LastCheckup = null }));
        Assert.Equal("checkup.never", item.Id);
        Assert.Equal(AttentionAction.RunCheckup, item.Action);
        Assert.Equal("checkup", item.PageKey);
    }

    [Fact]
    public void An_old_check_up_says_how_many_days_ago()
    {
        var item = Assert.Single(AttentionRules.Build(Healthy() with { LastCheckup = Now.AddDays(-14).AddHours(-3) }));
        Assert.Equal("checkup.stale", item.Id);
        Assert.Equal("Last check-up was 14 days ago", Format(item.Title, item.TitleArgs));
        Assert.Empty(AttentionRules.Build(Healthy() with { LastCheckup = Now.AddDays(-13) }));
    }

    [Fact]
    public void A_recent_check_up_with_things_to_do_points_to_its_list()
    {
        var item = Assert.Single(AttentionRules.Build(Healthy() with { CheckupItems = 3, CheckupCriticalItems = 1 }));
        Assert.Equal("checkup.todo", item.Id);
        Assert.Equal(AttentionLevel.Warning, item.Level);
        Assert.Equal("The last check-up found 3 things to do", Format(item.Title, item.TitleArgs));
        Assert.Equal(AttentionLevel.Suggestion, AttentionRules.Build(Healthy() with { CheckupItems = 1 })[0].Level);
        Assert.Equal("The last check-up found 1 thing to do", AttentionRules.Build(Healthy() with { CheckupItems = 1 })[0].Title);
    }

    [Fact]
    public void A_nearly_full_data_drive_is_a_warning_that_opens_disk_space()
    {
        var facts = Healthy() with { Drives = [new AttentionDrive("C:", null, 60, 200L << 30, true), new AttentionDrive("D:", "Games", 92.4, 70L << 30, false)] };
        var item = Assert.Single(AttentionRules.Build(facts));
        Assert.Equal(AttentionLevel.Warning, item.Level);
        Assert.Equal("disk-space", item.PageKey);
        Assert.Equal("Drive D: is 92 % full", Format(item.Title, item.TitleArgs));
    }

    [Fact]
    public void A_full_system_drive_is_critical_and_opens_cleanup()
    {
        var facts = Healthy() with { Drives = [new AttentionDrive("C:", null, 96, 9L << 30, true), new AttentionDrive("D:", null, 93, 60L << 30, false)] };
        var item = Assert.Single(AttentionRules.Build(facts));
        Assert.Equal(AttentionLevel.Critical, item.Level);
        Assert.Equal("cleanup", item.PageKey);
        Assert.Equal("Free up space", item.ActionText);
        Assert.Equal("1 other drive is nearly full too.", Format(item.Detail, item.DetailArgs));

        // Little free space on a big system drive is critical too, even below the percentage.
        var lowFree = Healthy() with { Drives = [new AttentionDrive("C:", null, 80, 3L << 30, true)] };
        Assert.Equal(AttentionLevel.Critical, Assert.Single(AttentionRules.Build(lowFree)).Level);
    }

    [Fact]
    public void Updates_waiting_and_a_turned_off_update_service()
    {
        var waiting = Assert.Single(AttentionRules.Build(Healthy() with { Updates = new UpdateGlance(UpdateGlanceState.UpdatesWaiting, 3, Now, null) }));
        Assert.Equal("Windows Update has 3 updates waiting", Format(waiting.Title, waiting.TitleArgs));
        Assert.Equal("windows-update", waiting.PageKey);

        var off = Assert.Single(AttentionRules.Build(Healthy() with { Updates = new UpdateGlance(UpdateGlanceState.ServiceOff, 0, null, null) }));
        Assert.Equal(AttentionLevel.Critical, off.Level);

        Assert.Empty(AttentionRules.Build(Healthy() with { Updates = new UpdateGlance(UpdateGlanceState.Paused, 0, Now, Now.AddDays(7)) }));
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(2, AttentionLevel.Suggestion)]
    [InlineData(4, AttentionLevel.Suggestion)]
    [InlineData(5, AttentionLevel.Warning)]
    public void Heavy_startup_apps_from_two(int count, AttentionLevel? level)
    {
        var items = AttentionRules.Build(Healthy() with { HeavyStartupApps = count });
        if (level is null)
        {
            Assert.Empty(items);
            return;
        }

        var item = Assert.Single(items);
        Assert.Equal(level, item.Level);
        Assert.Equal("startup", item.PageKey);
        Assert.Equal($"{count} apps start with Windows and slow it down", Format(item.Title, item.TitleArgs));
    }

    [Theory]
    [InlineData(PawnIoState.NotInstalled, "pawnio.missing")]
    [InlineData(PawnIoState.InstalledNotRunning, "pawnio.stopped")]
    [InlineData(PawnIoState.Running, null)]
    [InlineData(PawnIoState.NotSupported, null)]
    [InlineData(PawnIoState.Unknown, null)]
    public void PawnIo_is_offered_only_when_it_is_missing_or_stopped(PawnIoState state, string? id)
    {
        var items = AttentionRules.Build(Healthy() with { PawnIo = state });
        Assert.Equal(id, items.SingleOrDefault()?.Id);
        if (state == PawnIoState.NotInstalled)
        {
            Assert.Equal(AttentionAction.InstallPawnIo, items[0].Action);
            Assert.Equal("Install", items[0].ActionText);
        }
    }

    [Fact]
    public void Driver_problems_open_the_drivers_page()
    {
        var item = Assert.Single(AttentionRules.Build(Healthy() with { DeviceProblems = 2 }));
        Assert.Equal("drivers", item.PageKey);
        Assert.Equal("2 devices have a driver problem", Format(item.Title, item.TitleArgs));
    }

    [Fact]
    public void At_most_five_items_most_urgent_first()
    {
        var facts = new AttentionFacts
        {
            Now = Now,
            LastCheckup = null,
            Drives = [new AttentionDrive("C:", null, 97, 4L << 30, true)],
            HeavyStartupApps = 6,
            Updates = new UpdateGlance(UpdateGlanceState.ServiceOff, 0, null, null),
            PawnIo = PawnIoState.NotInstalled,
            DeviceProblems = 1,
        };
        var items = AttentionRules.Build(facts);
        Assert.Equal(AttentionRules.MaxItems, items.Count);
        Assert.Equal(["updates.off", "drive.full", "drivers.problems", "startup.heavy", "checkup.never"], items.Select(i => i.Id));
        Assert.True(items.Zip(items.Skip(1)).All(p => p.First.Level <= p.Second.Level));
        Assert.Equal(2, AttentionRules.Build(facts, max: 2).Count);
    }

    [Fact]
    public void Every_item_has_a_real_page_and_text_that_formats()
    {
        var pages = HelpAndShortcutTests.NavigationKeys();
        var all = new List<AttentionItem>();
        foreach (var facts in new[]
                 {
                     new AttentionFacts { Now = Now, LastCheckup = null, Drives = [new AttentionDrive("C:", null, 99, 1L << 30, true), new AttentionDrive("D:", null, 95, 1L << 30, false), new AttentionDrive("E:", null, 91, 1L << 30, false)], HeavyStartupApps = 3, Updates = new UpdateGlance(UpdateGlanceState.UpdatesWaiting, 1, Now, null), PawnIo = PawnIoState.InstalledNotRunning, DeviceProblems = 1 },
                     new AttentionFacts { Now = Now, LastCheckup = Now.AddDays(-30), Updates = new UpdateGlance(UpdateGlanceState.RestartPending, 0, Now, null), PawnIo = PawnIoState.NotInstalled },
                     new AttentionFacts { Now = Now, LastCheckup = Now, CheckupItems = 5 },
                 })
        {
            all.AddRange(AttentionRules.Build(facts, max: 20));
        }

        Assert.True(all.Count >= 10);
        foreach (var item in all)
        {
            Assert.Contains(item.PageKey, pages);
            Assert.False(string.IsNullOrWhiteSpace(Format(item.Title, item.TitleArgs)));
            Assert.False(string.IsNullOrWhiteSpace(Format(item.Detail, item.DetailArgs)));
            Assert.DoesNotContain("{", Format(item.Title, item.TitleArgs) + Format(item.Detail, item.DetailArgs));
            Assert.False(string.IsNullOrWhiteSpace(item.ActionText));
        }
    }

    [Fact]
    public void Check_up_staleness_and_days()
    {
        Assert.True(AttentionRules.IsCheckupStale(null, Now));
        Assert.True(AttentionRules.IsCheckupStale(Now.AddDays(-14), Now));
        Assert.False(AttentionRules.IsCheckupStale(Now.AddDays(-2), Now));
        Assert.Equal(0, AttentionRules.DaysAgo(Now.AddHours(1), Now));
        Assert.Equal(3, AttentionRules.DaysAgo(Now.AddDays(-3.5), Now));
    }

    private static string Format(string format, IReadOnlyList<object> args) =>
        string.Format(CultureInfo.InvariantCulture, format, args.ToArray());
}

/// <summary>The Undo button on success notifications.</summary>
public sealed class UndoTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
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

    private static readonly Guid Flow = Guid.NewGuid();

    [Fact]
    public void One_fresh_change_is_offered()
    {
        var entry = Entry(T0);
        Assert.Same(entry, UndoOfferRules.PickOffer([entry], T0.AddSeconds(1), Flow));
    }

    [Fact]
    public void Nothing_is_offered_for_old_offered_used_or_several_changes()
    {
        Assert.Null(UndoOfferRules.PickOffer([Entry(T0)], T0.AddSeconds(6), Flow));
        Assert.Null(UndoOfferRules.PickOffer([Entry(T0) with { OfferedAt = T0 }], T0.AddSeconds(1), Flow));
        Assert.Null(UndoOfferRules.PickOffer([Entry(T0) with { Used = true }], T0.AddSeconds(1), Flow));
        Assert.Null(UndoOfferRules.PickOffer([Entry(T0), Entry(T0.AddSeconds(1))], T0.AddSeconds(2), Flow));
        Assert.Null(UndoOfferRules.PickOffer([], T0, Flow));
    }

    [Fact]
    public void A_notification_from_somewhere_else_never_carries_the_undo()
    {
        var entry = Entry(T0);
        Assert.Null(UndoOfferRules.PickOffer([entry], T0.AddSeconds(1), null));
        Assert.Null(UndoOfferRules.PickOffer([entry], T0.AddSeconds(1), Guid.NewGuid()));

        // A change from another flow does not stop this flow's own change from being offered.
        var other = Entry(T0) with { Change = Entry(T0).Change with { FlowId = Guid.NewGuid() } };
        Assert.Same(entry, UndoOfferRules.PickOffer([entry, other], T0.AddSeconds(1), Flow));
    }

    [Fact]
    public void The_undo_button_works_for_ten_seconds_once()
    {
        var offered = Entry(T0) with { OfferedAt = T0 };
        Assert.True(UndoOfferRules.CanUndo(offered, T0.AddSeconds(10)));
        Assert.False(UndoOfferRules.CanUndo(offered, T0.AddSeconds(11)));
        Assert.False(UndoOfferRules.CanUndo(offered with { Used = true }, T0.AddSeconds(1)));
        Assert.False(UndoOfferRules.CanUndo(Entry(T0), T0.AddSeconds(1)));
    }

    [Fact]
    public void The_window_starts_when_the_notification_appears()
    {
        // Picked at T0, but queued behind another notification until T0+8 s.
        var shown = Entry(T0) with { OfferedAt = T0, ShownAt = T0.AddSeconds(8) };
        Assert.True(UndoOfferRules.CanUndo(shown, T0.AddSeconds(17)));
        Assert.False(UndoOfferRules.CanUndo(shown, T0.AddSeconds(19)));

        var picked = Entry(T0) with { OfferedAt = T0 };
        Assert.True(UndoOfferRules.CanShow(picked, T0.AddSeconds(20)));
        Assert.False(UndoOfferRules.CanShow(picked, T0.AddSeconds(31)));
        Assert.False(UndoOfferRules.CanShow(Entry(T0), T0));
    }

    [Fact]
    public void Old_entries_are_forgotten()
    {
        var kept = UndoOfferRules.Prune([Entry(T0), Entry(T0.AddMinutes(5))], T0.AddMinutes(5).AddSeconds(1)).ToList();
        Assert.Single(kept);
    }

    [Fact]
    public async Task The_tracker_offers_once_and_undoes_once()
    {
        var tracker = new ChangeUndoTracker();
        int undone = 0;
        var change = new UndoableChange(Guid.NewGuid(), Guid.NewGuid(), "Disable Steam", "Startup", "Steam", T0, _ =>
        {
            undone++;
            return Task.FromResult(OperationResult.Ok("Back on."));
        }, Flow);
        tracker.Register(change);

        Assert.Null(tracker.TakeOffer(T0.AddSeconds(1), Guid.NewGuid()));
        Assert.Same(change, tracker.TakeOffer(T0.AddSeconds(1), Flow));
        Assert.Null(tracker.TakeOffer(T0.AddSeconds(2), Flow));
        Assert.True(tracker.CanUndo(change.Id, T0.AddSeconds(5)));

        Assert.True((await tracker.UndoAsync(change.Id, T0.AddSeconds(5), CancellationToken.None)).Success);
        Assert.False((await tracker.UndoAsync(change.Id, T0.AddSeconds(6), CancellationToken.None)).Success);
        Assert.Equal(1, undone);
    }

    [Fact]
    public async Task The_tracker_starts_the_window_when_shown()
    {
        var tracker = new ChangeUndoTracker();
        var change = new UndoableChange(Guid.NewGuid(), Guid.NewGuid(), "x", "m", "t", T0, _ => Task.FromResult(OperationResult.Ok()), Flow);
        tracker.Register(change);
        tracker.TakeOffer(T0, Flow);

        Assert.True(tracker.MarkShown(change.Id, T0.AddSeconds(12)));
        Assert.True(tracker.CanUndo(change.Id, T0.AddSeconds(20)));
        Assert.True((await tracker.UndoAsync(change.Id, T0.AddSeconds(20), CancellationToken.None)).Success);

        var late = new UndoableChange(Guid.NewGuid(), Guid.NewGuid(), "y", "m", "t", T0, _ => Task.FromResult(OperationResult.Ok()), Flow);
        tracker.Register(late);
        tracker.TakeOffer(T0, Flow);
        Assert.False(tracker.MarkShown(late.Id, T0.AddSeconds(40)));
        Assert.False(tracker.MarkShown(Guid.NewGuid(), T0));
    }

    [Fact]
    public async Task A_late_click_does_not_undo()
    {
        var tracker = new ChangeUndoTracker();
        bool ran = false;
        var change = new UndoableChange(Guid.NewGuid(), Guid.NewGuid(), "x", "m", "t", T0, _ =>
        {
            ran = true;
            return Task.FromResult(OperationResult.Ok());
        }, Flow);
        tracker.Register(change);
        tracker.TakeOffer(T0, Flow);

        var result = await tracker.UndoAsync(change.Id, T0.AddSeconds(30), CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(ran);
        Assert.False((await tracker.UndoAsync(Guid.NewGuid(), T0, CancellationToken.None)).Success);
    }

    [Fact]
    public async Task A_failing_undo_is_reported_not_thrown()
    {
        var tracker = new ChangeUndoTracker();
        var change = new UndoableChange(Guid.NewGuid(), Guid.NewGuid(), "x", "m", "t", T0, _ => throw new InvalidOperationException("boom"), Flow);
        tracker.Register(change);
        tracker.TakeOffer(T0, Flow);
        var result = await tracker.UndoAsync(change.Id, T0.AddSeconds(1), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("boom", result.Message);
    }

    [Fact]
    public void Changes_from_one_offer_scope_count_as_one()
    {
        var tracker = new ChangeUndoTracker();
        var scope = Guid.NewGuid();
        tracker.Register(new UndoableChange(Guid.NewGuid(), scope, "a", "m", "t", T0, _ => Task.FromResult(OperationResult.Ok()), Flow));
        tracker.Register(new UndoableChange(Guid.NewGuid(), scope, "b", "m", "t", T0.AddSeconds(1), _ => Task.FromResult(OperationResult.Ok()), Flow));
        Assert.Equal("b", tracker.TakeOffer(T0.AddSeconds(2), Flow)?.Description);
    }

    [Fact]
    public async Task The_guard_registers_a_reversible_change_made_inside_an_offer()
    {
        var (guard, tracker) = Create();
        var flow = ChangeUndo.BeginFlow();
        using (ChangeUndo.Offer(_ => Task.FromResult(OperationResult.Ok())))
        {
            await guard.RunGuardedAsync("Disable Steam", ChangeOptions.For("Startup", "Steam", reversible: true), _ => Task.FromResult(OperationResult.Ok()), null, CancellationToken.None);
        }

        Assert.Equal(flow, ChangeUndo.CurrentFlow);
        Assert.Null(tracker.TakeOffer(DateTimeOffset.Now, null));
        Assert.Equal("Disable Steam", tracker.TakeOffer(DateTimeOffset.Now, flow)?.Description);
        Assert.Null(ChangeUndo.Current);
    }

    [Fact]
    public async Task A_flow_started_by_a_helper_lasts_for_the_calling_method_only()
    {
        var flow = await CallerAsync();

        // The method that made the change (and showed its notification) has ended: its flow is gone here.
        Assert.NotEqual(flow, ChangeUndo.CurrentFlow);

        static async Task<Guid> CallerAsync()
        {
            var started = StartFlow();                   // like UndoOffers.RunAsync: not async, so it sets the caller's flow
            await Task.Yield();
            Assert.Equal(started, ChangeUndo.CurrentFlow); // still there for the notification after the change
            await NestedAsync();                          // a nested method's own flow does not leak back
            Assert.Equal(started, ChangeUndo.CurrentFlow);
            return started;
        }

        static Guid StartFlow() => ChangeUndo.BeginFlow();

        static async Task NestedAsync()
        {
            ChangeUndo.BeginFlow();
            await Task.Yield();
        }
    }

    [Fact]
    public async Task The_guard_skips_failed_irreversible_and_unexplained_changes()
    {
        var (guard, tracker) = Create();
        var flow = ChangeUndo.BeginFlow();
        await guard.RunGuardedAsync("No undo given", ChangeOptions.For("Startup", "a", reversible: true), _ => Task.FromResult(OperationResult.Ok()), null, CancellationToken.None);
        using (ChangeUndo.Offer(_ => Task.FromResult(OperationResult.Ok())))
        {
            await guard.RunGuardedAsync("Not reversible", ChangeOptions.For("Cleanup", "b"), _ => Task.FromResult(OperationResult.Ok()), null, CancellationToken.None);
            await guard.RunGuardedAsync("Failed", ChangeOptions.For("Startup", "c", reversible: true), _ => Task.FromResult(OperationResult.Fail("no")), null, CancellationToken.None);
        }

        Assert.Null(tracker.TakeOffer(DateTimeOffset.Now, flow));
    }

    [Fact]
    public async Task The_guard_uses_the_undo_in_the_options_and_never_inside_a_batch()
    {
        var (guard, tracker) = Create();
        var flow = ChangeUndo.BeginFlow();
        var options = ChangeOptions.For("Power", "plan", reversible: true) with { Undo = _ => Task.FromResult(OperationResult.Ok()) };
        await guard.RunBatchAsync("Fixes", true, async ct =>
        {
            await guard.RunGuardedAsync("In a batch", options, _ => Task.FromResult(OperationResult.Ok()), null, ct);
        }, null, CancellationToken.None);
        Assert.Null(tracker.TakeOffer(DateTimeOffset.Now, flow));

        await guard.RunGuardedAsync("On its own", options, _ => Task.FromResult(OperationResult.Ok()), null, CancellationToken.None);
        Assert.Equal("On its own", tracker.TakeOffer(DateTimeOffset.Now, flow)?.Description);
    }

    [Fact]
    public async Task Changes_made_by_an_undo_are_not_offered_again()
    {
        var (guard, tracker) = Create();
        var flow = ChangeUndo.BeginFlow();
        var options = ChangeOptions.For("Startup", "Steam", reversible: true);
        Func<CancellationToken, Task<OperationResult>> undo = ct =>
            guard.RunGuardedAsync("Enable Steam", options with { Undo = _ => Task.FromResult(OperationResult.Ok()) }, _ => Task.FromResult(OperationResult.Ok()), null, ct);

        await guard.RunGuardedAsync("Disable Steam", options with { Undo = undo }, _ => Task.FromResult(OperationResult.Ok()), null, CancellationToken.None);
        var offer = tracker.TakeOffer(DateTimeOffset.Now, flow);
        Assert.NotNull(offer);
        Assert.True((await tracker.UndoAsync(offer!.Id, DateTimeOffset.Now, CancellationToken.None)).Success);
        Assert.Null(tracker.TakeOffer(DateTimeOffset.Now, flow));
    }

    private static UndoEntry Entry(DateTimeOffset completed) =>
        new(new UndoableChange(Guid.NewGuid(), Guid.NewGuid(), "change", "Tests", "t", completed, _ => Task.FromResult(OperationResult.Ok()), Flow));

    private (ChangeGuard Guard, ChangeUndoTracker Tracker) Create()
    {
        var settings = new JsonSettingsService(_dir);
        settings.Current.RestorePoints.CreateBeforeChanges = false;
        settings.Current.RestorePoints.CreateAfterChanges = false;
        var tracker = new ChangeUndoTracker();
        var journal = new JsonActionJournal(Path.Combine(_dir, "actions.jsonl"));
        return (new ChangeGuard(new ChangeBatchTests.CountingRestore(), journal, settings, NullLogger<ChangeGuard>.Instance, tracker), tracker);
    }
}
