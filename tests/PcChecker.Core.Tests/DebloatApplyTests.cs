using PcChecker.Core.Common;
using PcChecker.Core.Debloat;
using PcChecker.Core.Optimization;
using Xunit;

namespace PcChecker.Core.Tests;

/// <summary>
/// The causes behind "debloat tweaks don't seem to apply": values of the wrong type read as applied, results that were
/// not checked against the PC afterwards, changes that only show after an Explorer restart or sign-out, per-user values
/// written to the wrong account, and batches stopped by a missing restore point.
/// </summary>
public class DebloatApplyTests
{
    private const string UserSid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string AdminSid = "S-1-5-21-1004336348-1177238915-682003330-500";

    private static DebloatTweak Tweak(string id) => DebloatCatalog.Find(id)!;

    private static (DebloatEngine Engine, InMemoryDebloatSystem System) Create()
    {
        var system = new InMemoryDebloatSystem(DebloatCatalog.Windows11_24H2);
        return (new DebloatEngine(system, new DebloatCaptureBook()), system);
    }

    // ---- state comparison ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_number_stored_as_text_is_not_applied_and_applying_fixes_the_type()
    {
        var (engine, system) = Create();
        var tweak = Tweak("explorer.extensions");
        system.Memory.Write(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "HideFileExt", new RegistryData(DebloatValueKind.String, "0"));

        var before = (await engine.GetStatesAsync([tweak], default))[tweak.Id];
        Assert.Equal(TweakStatus.NotApplied, before.Status);
        Assert.Equal("Stored as REG_SZ; Windows needs REG_DWORD", before.Detail);

        Assert.Equal(TweakOutcome.Applied, (await engine.ApplyAsync(tweak, default)).Outcome);
        Assert.Equal(new RegistryData(DebloatValueKind.DWord, "0"), system.Registry.Read(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "HideFileExt"));
        Assert.Equal(TweakStatus.Applied, (await engine.GetStatesAsync([tweak], default))[tweak.Id].Status);

        // Revert puts back exactly what was there, type included.
        await engine.RevertAsync(tweak, default);
        Assert.Equal(new RegistryData(DebloatValueKind.String, "0"), system.Registry.Read(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "HideFileExt"));
    }

    [Fact]
    public void Type_rules_follow_how_windows_reads_values()
    {
        Assert.True(RegistryValueCodec.KindAccepted(DebloatValueKind.ExpandString, DebloatValueKind.String));
        Assert.False(RegistryValueCodec.KindAccepted(DebloatValueKind.QWord, DebloatValueKind.DWord));
        Assert.False(RegistryValueCodec.Matches(new RegistryData(DebloatValueKind.QWord, "0"), DebloatValueKind.DWord, "0"));
        Assert.Null(RegistryValueCodec.WrongTypeDetail(new RegistryData(DebloatValueKind.DWord, "1"), DebloatValueKind.DWord));
        Assert.Null(RegistryValueCodec.WrongTypeDetail(null, DebloatValueKind.DWord));
        Assert.Equal("Stored as REG_BINARY; Windows needs REG_SZ", RegistryValueCodec.WrongTypeDetail(new RegistryData(DebloatValueKind.Binary, "00"), DebloatValueKind.String));
    }

    [Fact]
    public async Task A_tweak_that_is_already_in_place_is_reported_as_already_applied_without_writing()
    {
        var (engine, system) = Create();
        var tweak = Tweak("explorer.extensions");
        system.Memory.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "HideFileExt", 0);
        system.Memory.BlockedKeys.Add(@"HKCU\" + DebloatCatalog.ExplorerAdvanced); // any write would fail

        var result = await engine.ApplyAsync(tweak, default);
        Assert.Equal(TweakOutcome.AlreadyApplied, result.Outcome);
        Assert.Contains("Already applied", result.Message);
    }

    [Fact]
    public async Task Linked_optimize_action_already_in_effect_is_not_run_again()
    {
        var svc = new Probe();
        var result = await svc.ApplyAsync(["gaming.gamemode"], null, default); // Game Mode is on in the demo PC
        Assert.Equal(TweakOutcome.AlreadyApplied, Assert.Single(result.Results).Outcome);
        Assert.Equal(0, svc.DelegatedCalls);
        Assert.Equal(1, result.AlreadyApplied);
        Assert.Contains("1 already applied", result.Summary);
    }

    // ---- result classification ---------------------------------------------------------------------------------

    [Fact]
    public void An_applied_change_that_does_not_read_back_is_reported_as_failed()
    {
        var tweak = Tweak("privacy.advertisingid");
        var result = new TweakRunResult(tweak.Id, tweak.Title, TweakOutcome.Applied, "Applied.");

        var notKept = DebloatResultClassifier.Classify(result, tweak, new TweakState(tweak.Id, TweakStatus.NotApplied, "Windows default"), revert: false);
        Assert.Equal(DebloatResultKind.Failed, notKept.Kind);
        Assert.Equal(DebloatResultClassifier.NotKeptMessage, notKept.Message);

        var partial = DebloatResultClassifier.Classify(result, tweak, new TweakState(tweak.Id, TweakStatus.Partial, "1 of 2 changes in place"), revert: false);
        Assert.Equal(DebloatResultKind.Failed, partial.Kind);
        Assert.Contains("1 of 2 changes in place", partial.Message);

        var unknown = DebloatResultClassifier.Classify(result, tweak, TweakState.Unknown(tweak.Id), revert: false);
        Assert.Equal(DebloatResultKind.Applied, unknown.Kind);

        // A revert that leaves the setting applied restored the saved value, which can be the applied one.
        var revert = new TweakRunResult(tweak.Id, tweak.Title, TweakOutcome.Reverted, "Restored the values saved before the change.");
        Assert.Equal(DebloatResultKind.Reverted, DebloatResultClassifier.Classify(revert, tweak, new TweakState(tweak.Id, TweakStatus.Applied, null), revert: true).Kind);
    }

    [Fact]
    public void Applied_results_say_what_is_needed_before_they_show()
    {
        var tweak = Tweak("taskbar.alignleft");
        var view = DebloatResultClassifier.Classify(new TweakRunResult(tweak.Id, tweak.Title, TweakOutcome.Applied, "Applied."), tweak,
            new TweakState(tweak.Id, TweakStatus.Applied, "Applied"), revert: false);
        Assert.Equal(DebloatResultKind.Applied, view.Kind);
        Assert.Equal(DebloatVisibility.RestartExplorer, view.Visibility);
        Assert.Equal("Applied – restart Explorer to see it", view.Label);
        Assert.Equal("Restart File Explorer to see it.", view.NextStep);

        var already = DebloatResultClassifier.Classify(new TweakRunResult(tweak.Id, tweak.Title, TweakOutcome.AlreadyApplied, "Already applied: nothing needed to change."),
            tweak, null, revert: false);
        Assert.Equal("Already applied", already.Label);
        Assert.Null(already.NextStep);

        var failed = DebloatResultClassifier.Classify(new TweakRunResult(tweak.Id, tweak.Title, TweakOutcome.Failed, "Windows blocked the change to HKCU\\x (access denied)."),
            tweak, null, revert: false);
        Assert.Contains("organisation manages it", failed.Message);
        Assert.Null(failed.NextStep);
    }

    [Fact]
    public void Summary_counts_every_kind_and_lists_failures_first()
    {
        var results = new List<TweakRunResult>
        {
            new("taskbar.alignleft", "Align left", TweakOutcome.Applied, "Applied."),
            new("ai.recall", "Recall", TweakOutcome.Applied, "Applied."),
            new("explorer.extensions", "Extensions", TweakOutcome.AlreadyApplied, "Already applied: nothing needed to change."),
            new("svc.fax", "Fax", TweakOutcome.Skipped, "1 printer installed."),
            new("privacy.location", "Location", TweakOutcome.Failed, "Windows blocked the change (access denied)."),
        };
        var views = DebloatResultClassifier.ClassifyAll(results, new Dictionary<string, TweakState>(), revert: false);
        Assert.Equal(DebloatResultKind.Failed, views[0].Kind);
        Assert.Equal(DebloatResultKind.Skipped, views[1].Kind);

        var summary = DebloatResultClassifier.Summarize(views, revert: false, "Note: no restore point was created.");
        Assert.Equal("Debloat finished with problems", summary.Title);
        Assert.Equal(3, summary.Done);
        Assert.Equal(1, summary.Failed);
        Assert.True(summary.NeedsExplorerRestart);
        Assert.True(summary.NeedsSignOut);
        Assert.False(summary.NeedsWindowsRestart);
        Assert.StartsWith("2 applied (1 shows after File Explorer restarts, 1 after you sign out), 1 already applied, 1 skipped, 1 failed.", summary.Message);
        Assert.EndsWith("Note: no restore point was created.", summary.Message);

        Assert.Equal("Debloat failed", DebloatResultClassifier.Summarize([views[0]], revert: false).Title);
        Assert.Equal("Revert finished", DebloatResultClassifier.Summarize([], revert: true).Title);
    }

    // ---- restart rules -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("taskbar.alignleft", DebloatVisibility.RestartExplorer)]
    [InlineData("explorer.extensions", DebloatVisibility.RestartExplorer)] // Explorer reads it when it starts
    [InlineData("explorer.classicmenu", DebloatVisibility.RestartExplorer)]
    [InlineData("ai.copilot", DebloatVisibility.RestartExplorer)] // the catalog says an Explorer restart is enough
    [InlineData("ai.recall", DebloatVisibility.SignOut)]
    [InlineData("ai.clicktodo", DebloatVisibility.SignOut)]
    [InlineData("ai.recallfeature", DebloatVisibility.RestartWindows)]
    [InlineData("gaming.xboxservices", DebloatVisibility.RestartWindows)] // disabled services keep running until then
    [InlineData("explorer.longpaths", DebloatVisibility.RestartWindows)]
    [InlineData("privacy.wer", DebloatVisibility.Now)] // a service set to Manual needs nothing
    [InlineData("privacy.advertisingid", DebloatVisibility.Now)]
    [InlineData("ai.paint", DebloatVisibility.Now)]
    [InlineData("start.settingshome", DebloatVisibility.Now)]
    [InlineData("ads.start", DebloatVisibility.Now)]
    public void Restart_rules_match_how_windows_picks_up_each_change(string id, DebloatVisibility expected) =>
        Assert.Equal(expected, DebloatRestartRules.For(Tweak(id)));

    [Fact]
    public void Every_per_user_taskbar_and_explorer_tweak_says_it_needs_at_least_an_explorer_restart()
    {
        var shell = DebloatCatalog.All.Where(t => t.Category is DebloatCatalog.TaskbarCategory or DebloatCatalog.ExplorerCategory
                                                  && t.Operations.OfType<RegistryValueOp>().Any(o => o.Hive == DebloatHive.CurrentUser));
        Assert.All(shell, t => Assert.NotEqual(DebloatVisibility.Now, DebloatRestartRules.For(t)));
    }

    // ---- signed-in user's hive -------------------------------------------------------------------------------------

    [Fact]
    public void Session_owner_is_the_account_most_explorer_processes_belong_to()
    {
        Assert.Equal(UserSid, UserHiveResolver.PickSessionOwner([null, "S-1-5-18", UserSid, " " + UserSid + " ", AdminSid]));
        Assert.Null(UserHiveResolver.PickSessionOwner([null, "S-1-5-18", "S-1-5-19", ""]));
        Assert.Null(UserHiveResolver.PickSessionOwner([]));
    }

    [Theory]
    [InlineData(UserSid, true)]
    [InlineData("S-1-12-1-3570441440-1258291440-2196346512-3263431101", true)] // Microsoft Entra ID account
    [InlineData("S-1-5-18", false)]
    [InlineData("S-1-5-20", false)]
    [InlineData(UserSid + "_Classes", false)]
    [InlineData("S-1-5-21-", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_real_user_accounts_have_a_hive_to_target(string? sid, bool expected) =>
        Assert.Equal(expected, UserHiveResolver.IsUserSid(sid));

    [Fact]
    public void Hive_choice_targets_the_signed_in_user_only_when_it_is_another_loaded_account()
    {
        const string fallback = "PC Checker is running as the administrator account PC\\Admin, not as PC\\Sam who is signed in.";

        var same = UserHiveResolver.Choose(UserSid, UserSid, "PC\\Sam", _ => true, null);
        Assert.False(same.IsRedirected);
        Assert.Null(same.Note);

        var other = UserHiveResolver.Choose(AdminSid, UserSid, "PC\\Sam", _ => true, fallback);
        Assert.True(other.IsRedirected);
        Assert.Equal(UserSid, other.Sid);
        Assert.Contains("PC\\Sam", other.Note);

        Assert.Equal(UserHiveTarget.Own(fallback), UserHiveResolver.Choose(AdminSid, UserSid, "PC\\Sam", _ => false, fallback));
        Assert.Equal(UserHiveTarget.Own(fallback), UserHiveResolver.Choose(AdminSid, null, null, _ => true, fallback));
        Assert.Equal(UserHiveTarget.Own(fallback), UserHiveResolver.Choose(AdminSid, "S-1-5-18", null, _ => true, fallback));
        Assert.Equal(UserHiveTarget.Own(fallback), UserHiveResolver.Choose(AdminSid, UserSid, null, _ => throw new UnauthorizedAccessException(), fallback));
        Assert.Equal(UserHiveTarget.Own(fallback), UserHiveResolver.Choose(null, UserSid, null, _ => true, fallback));
    }

    [Fact]
    public void Hkcu_paths_map_onto_hkey_users_with_classes_in_their_own_hive()
    {
        Assert.Equal((UserSid + "_Classes", @"CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32"),
            UserHiveResolver.MapPath(UserSid, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", classesHiveLoaded: true));
        Assert.Equal((UserSid + "_Classes", string.Empty), UserHiveResolver.MapPath(UserSid, @"SOFTWARE\CLASSES", classesHiveLoaded: true));
        Assert.Equal((UserSid, @"Software\Classes\CLSID\{x}"), UserHiveResolver.MapPath(UserSid, @"Software\Classes\CLSID\{x}", classesHiveLoaded: false));
        Assert.Equal((UserSid, DebloatCatalog.ExplorerAdvanced), UserHiveResolver.MapPath(UserSid, "\\" + DebloatCatalog.ExplorerAdvanced, classesHiveLoaded: true));
        Assert.Equal((UserSid, @"Software\ClassesExtra\x"), UserHiveResolver.MapPath(UserSid, @"Software\ClassesExtra\x", classesHiveLoaded: true));
    }

    [Fact]
    public async Task Per_user_captures_are_kept_per_account()
    {
        var (engine, system) = Create();
        var perUser = Tweak("explorer.extensions");
        var machine = Tweak("explorer.longpaths");
        system.Memory.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "HideFileExt", 1);

        system.CurrentUserScopeId = UserSid;
        Assert.Equal($"explorer.extensions@{UserSid}", engine.CaptureId(perUser));
        Assert.Equal("explorer.longpaths", engine.CaptureId(machine));
        await engine.ApplyAsync(perUser, default);
        Assert.True(engine.Captures.Has($"explorer.extensions@{UserSid}"));
        Assert.False(engine.Captures.Has("explorer.extensions"));

        // Another account (or the app's own) has no capture of its own, so its revert uses the Windows default.
        system.CurrentUserScopeId = null;
        Assert.Contains("Windows default", (await engine.RevertAsync(perUser, default)).Message);
        Assert.True(engine.Captures.Has($"explorer.extensions@{UserSid}"));

        system.CurrentUserScopeId = UserSid;
        Assert.Contains("saved before", (await engine.RevertAsync(perUser, default)).Message);
        Assert.False(engine.Captures.Has($"explorer.extensions@{UserSid}"));
    }

    // ---- restore point -------------------------------------------------------------------------------------------

    [Fact]
    public void Restore_point_problems_are_known_before_the_batch_starts()
    {
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(RestorePointProblem.ProtectionOff, DebloatRestorePointRules.Assess(false, null, now, limitLifted: true));
        Assert.Equal(RestorePointProblem.FrequencyLimit, DebloatRestorePointRules.Assess(true, now.AddHours(-3), now, limitLifted: false));
        Assert.Equal(RestorePointProblem.None, DebloatRestorePointRules.Assess(true, now.AddHours(-3), now, limitLifted: true));
        Assert.Equal(RestorePointProblem.None, DebloatRestorePointRules.Assess(true, now.AddHours(-25), now, limitLifted: false));
        Assert.Equal(RestorePointProblem.None, DebloatRestorePointRules.Assess(null, null, now, limitLifted: false));
        Assert.Equal(RestorePointProblem.None, DebloatRestorePointRules.Assess(true, DateTimeOffset.MinValue, now, limitLifted: false));
    }

    [Fact]
    public void A_pc_that_already_allows_more_restore_points_per_day_is_not_blocked()
    {
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, DebloatRestorePointRules.WindowFor(0));
        Assert.Equal(TimeSpan.FromHours(24), DebloatRestorePointRules.WindowFor(null));
        Assert.Equal(TimeSpan.FromMinutes(60), DebloatRestorePointRules.WindowFor(60));

        Assert.Equal(RestorePointProblem.None, DebloatRestorePointRules.Assess(true, now.AddMinutes(-5), now, limitLifted: false, DebloatRestorePointRules.WindowFor(0)));
        Assert.Equal(RestorePointProblem.None, DebloatRestorePointRules.Assess(true, now.AddHours(-2), now, limitLifted: false, DebloatRestorePointRules.WindowFor(60)));
        Assert.Equal(RestorePointProblem.FrequencyLimit, DebloatRestorePointRules.Assess(true, now.AddMinutes(-30), now, limitLifted: false, DebloatRestorePointRules.WindowFor(60)));
    }

    [Fact]
    public void The_one_per_day_limit_points_to_the_setting_that_lifts_it()
    {
        var text = DebloatRestorePointRules.Describe(RestorePointProblem.FrequencyLimit, "C:");
        Assert.Contains("Allow more than one restore point per day", text);
    }

    [Fact]
    public void A_refused_restore_point_only_offers_turning_on_protection_when_that_can_help()
    {
        string noNewPoint = PcChecker.Core.Restore.WindowsRestorePointService.NoNewPointMessage;
        Assert.Equal(RestorePointProblem.FrequencyLimit, DebloatRestorePointRules.ProblemAfterFailure(noNewPoint, RestorePointProblem.None, protectionOn: true));
        Assert.Equal(RestorePointProblem.ProtectionOff, DebloatRestorePointRules.ProblemAfterFailure("Access denied", RestorePointProblem.ProtectionOff));
        Assert.Equal(RestorePointProblem.Failed, DebloatRestorePointRules.ProblemAfterFailure("Windows took more than two minutes", RestorePointProblem.None, protectionOn: true));

        Assert.False(DebloatRestorePointRules.CanTurnOnHelp(RestorePointProblem.FrequencyLimit, protectionOn: true));
        Assert.False(DebloatRestorePointRules.CanTurnOnHelp(RestorePointProblem.Failed, protectionOn: true));
        Assert.True(DebloatRestorePointRules.CanTurnOnHelp(RestorePointProblem.Failed, protectionOn: null));
        Assert.True(DebloatRestorePointRules.CanTurnOnHelp(RestorePointProblem.ProtectionOff, protectionOn: false));
    }

    [Fact]
    public void A_change_that_did_not_stick_gets_a_reason_that_fits_what_the_tweak_does()
    {
        var app = Tweak("ai.copilotapp");
        var appResult = DebloatResultClassifier.Classify(new TweakRunResult(app.Id, app.Title, TweakOutcome.Applied, "Removed."), app,
            new TweakState(app.Id, TweakStatus.NotApplied, "Installed"), revert: false);
        Assert.Equal(DebloatResultKind.Failed, appResult.Kind);
        Assert.Equal(DebloatResultClassifier.AppNotRemovedMessage, appResult.Message);

        Assert.Equal(DebloatResultClassifier.FeatureNotChangedMessage, DebloatResultClassifier.NotKeptMessageFor(Tweak("ai.recallfeature")));
        Assert.Equal(DebloatResultClassifier.NotKeptMessage, DebloatResultClassifier.NotKeptMessageFor(Tweak("privacy.advertisingid")));
        Assert.Equal(DebloatResultClassifier.NotKeptMessage, DebloatResultClassifier.NotKeptMessageFor(null));
        var delegated = DebloatCatalog.All.First(t => t.DelegatesToOptimizationId is not null);
        Assert.Equal(DebloatResultClassifier.DelegatedNotKeptMessage, DebloatResultClassifier.NotKeptMessageFor(delegated));
    }

    [Fact]
    public void Restore_point_dialog_explains_every_choice()
    {
        var text = DebloatRestorePointRules.DialogText(RestorePointProblem.ProtectionOff, "C:", null, needRestorePoint: 2, total: 5, canTurnOn: true);
        Assert.Contains("System Protection is off for C:", text);
        Assert.Contains("Turn on System Protection", text);
        Assert.Contains("the 3 tweaks that can be reverted from this page run; the 2 that cannot be undone here", text);
        Assert.Contains("Cancel: nothing changes.", text);

        var onlyOneWay = DebloatRestorePointRules.DialogText(RestorePointProblem.Failed, "C:", "Windows took more than two minutes.", 3, 3, canTurnOn: false);
        Assert.Contains("Windows could not create the restore point this batch needs: Windows took more than two minutes.", onlyOneWay);
        Assert.DoesNotContain("Continue without", onlyOneWay);
        Assert.False(DebloatRestorePointRules.CanContinueWithout(3, 3));
        Assert.True(DebloatRestorePointRules.CanContinueWithout(0, 1));
    }

    [Fact]
    public async Task A_batch_refused_for_lack_of_a_restore_point_changes_nothing_and_says_so()
    {
        var svc = new Probe { RefuseRestorePoint = true };
        var result = await svc.ApplyAsync(["apps.clipchamp", "explorer.thispc"], null, default);

        Assert.True(result.BlockedByRestorePoint);
        Assert.Contains("System Protection is off", result.RestorePointBlockedReason);
        Assert.All(result.Results, r => Assert.Equal(TweakOutcome.Skipped, r.Outcome));
        Assert.All(result.Results, r => Assert.StartsWith("Not started", r.Message));
        Assert.Contains("Microsoft.BingNews", svc.SampleSystem.Apps);
        Assert.Equal(0, svc.SampleSystem.SettingsBroadcasts);
    }

    [Fact]
    public async Task Continuing_without_a_restore_point_runs_only_what_can_be_reverted_here()
    {
        var svc = new Probe { RefuseRestorePoint = true };
        svc.SampleSystem.Apps.Add("Clipchamp.Clipchamp");
        var result = await svc.ApplyAsync(["apps.clipchamp", "explorer.thispc"], new DebloatRunOptions(WithoutRestorePoint: true), null, default);

        Assert.False(result.BlockedByRestorePoint);
        Assert.Equal([false], svc.RestorePointFlags);
        var app = result.Results.Single(r => r.TweakId == "apps.clipchamp");
        Assert.Equal(TweakOutcome.Skipped, app.Outcome);
        Assert.Equal(DebloatServiceBase.NeedsRestorePointMessage, app.Message);
        Assert.Contains("Clipchamp.Clipchamp", svc.SampleSystem.Apps);
        Assert.Equal(TweakOutcome.Applied, result.Results.Single(r => r.TweakId == "explorer.thispc").Outcome);
        Assert.Equal(1, svc.SampleSystem.SettingsBroadcasts); // running programs are told about the change
    }

    [Fact]
    public async Task Capture_file_that_cannot_be_written_stops_the_tweak_before_anything_changes()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "pcchecker-capture-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocker, "not a folder");
        try
        {
            var system = new InMemoryDebloatSystem();
            system.Memory.SetDword(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "LaunchTo", 2);
            var engine = new DebloatEngine(system, new DebloatCaptureBook(Path.Combine(blocker, "sub", DebloatCaptureBook.FileName)));

            var result = await engine.ApplyAsync(Tweak("explorer.thispc"), default);
            Assert.Equal(TweakOutcome.Failed, result.Outcome);
            Assert.StartsWith("Nothing was changed: the undo information could not be saved to debloat-capture.json", result.Message);
            Assert.Equal("2", system.Registry.Read(DebloatHive.CurrentUser, DebloatCatalog.ExplorerAdvanced, "LaunchTo")!.Value);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    /// <summary>Debloat service over the demo PC that can refuse the restore point like Windows does with System Protection off.</summary>
    private sealed class Probe : DebloatServiceBase
    {
        private readonly IOptimizationService _optimization = new DemoOptimizationService([]);

        public Probe()
            : base(new DebloatEngine(DemoDebloatService.CreateSampleSystem(), new DebloatCaptureBook()))
        {
            SampleSystem.DelayMs = 0;
        }

        public InMemoryDebloatSystem SampleSystem => (InMemoryDebloatSystem)Engine.System;

        public bool RefuseRestorePoint { get; init; }

        public List<bool> RestorePointFlags { get; } = [];

        public int DelegatedCalls { get; private set; }

        public override Task<OperationResult> RestartExplorerAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok("ok"));

        public override OperationResult OpenSettings(string settingsUri) => OperationResult.Ok("ok");

        public override Task<OperationResult> TurnOnSystemProtectionAsync(CancellationToken cancellationToken) => Task.FromResult(OperationResult.Ok("on"));

        protected override async Task<OperationResult<IReadOnlyList<TweakRunResult>>> RunGuardedAsync(string description, IReadOnlyList<DebloatTweak> tweaks, bool revert,
            bool createRestorePoint, Func<CancellationToken, Task<IReadOnlyList<TweakRunResult>>> work, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
        {
            RestorePointFlags.Add(createRestorePoint);
            if (RefuseRestorePoint && createRestorePoint && WindowsDebloatService.RequiresRestorePoint(tweaks, revert))
            {
                return OperationResult<IReadOnlyList<TweakRunResult>>.Fail($"{description} was not started because System Protection is off.");
            }

            var results = await work(cancellationToken);
            return OperationResult<IReadOnlyList<TweakRunResult>>.Ok(results, "done");
        }

        protected override Task<OperationResult> RunDelegatedAsync(string optimizationId, bool revert, CancellationToken cancellationToken)
        {
            DelegatedCalls++;
            return revert ? _optimization.RevertAsync(optimizationId, cancellationToken) : _optimization.ApplyAsync(optimizationId, cancellationToken);
        }

        protected override Task<ActionState> GetDelegatedStateAsync(string optimizationId, CancellationToken cancellationToken) =>
            _optimization.GetStateAsync(optimizationId, cancellationToken);
    }
}
