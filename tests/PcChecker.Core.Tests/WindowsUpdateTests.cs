using PcChecker.Core.Drivers;
using PcChecker.Core.WindowsUpdate;
using Xunit;

namespace PcChecker.Core.Tests;

public class UpdateClassifierTests
{
    private const string Lcu = "2026-10 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5066835)";

    [Fact]
    public void Monthly_cumulative_update_is_cumulative_even_though_it_is_a_security_update()
    {
        Assert.Equal(UpdateClassification.Cumulative, UpdateClassifier.Classify([UpdateClassifier.CategoryIds.SecurityUpdates], Lcu));
    }

    [Theory]
    [InlineData("2026-10 Kumulatives Update für Windows 11 Version 24H2 für x64-basierte Systeme (KB5066835)")]
    [InlineData("2026-10 Mise à jour cumulative pour Windows 11 Version 24H2 pour les systèmes x64 (KB5066835)")]
    [InlineData("2026-10 Actualización acumulativa para Windows 11 Version 24H2 para sistemas basados en x64 (KB5066835)")]
    [InlineData("2026-10 Aggiornamento cumulativo per Windows 11 Version 24H2 per sistemi basati su x64 (KB5066835)")]
    [InlineData("2026-10 Atualização Cumulativa para Windows 11 Version 24H2 para sistemas baseados em x64 (KB5066835)")]
    public void Cumulative_updates_are_recognised_in_the_shipped_languages(string title)
    {
        Assert.Equal(UpdateClassification.Cumulative, UpdateClassifier.Classify([UpdateClassifier.CategoryIds.SecurityUpdates], title));
    }

    [Fact]
    public void Category_ids_decide_regardless_of_case_braces_and_title_language()
    {
        Assert.Equal(UpdateClassification.Security, UpdateClassifier.Classify(["{0FA1201D-4330-4FA8-8AE9-B877473B6441}"], "Sicherheitsupdate für Windows"));
        Assert.Equal(UpdateClassification.Definition, UpdateClassifier.Classify([UpdateClassifier.CategoryIds.DefinitionUpdates], "Security Intelligence-Update für Microsoft Defender"));
        Assert.Equal(UpdateClassification.Feature, UpdateClassifier.Classify([UpdateClassifier.CategoryIds.Upgrades], "Windows 11, Version 25H2"));
        Assert.Equal(UpdateClassification.Driver, UpdateClassifier.Classify([UpdateClassifier.CategoryIds.Drivers], "NVIDIA - Display - 32.0.15.8097"));
        Assert.Equal(UpdateClassification.Driver, UpdateClassifier.Classify([], "Anything", isDriver: true));
        Assert.Equal(UpdateClassification.Other, UpdateClassifier.Classify([UpdateClassifier.CategoryIds.UpdateRollups], "Windows Malicious Software Removal Tool x64 - v5.141 (KB890830)"));
        Assert.Equal(UpdateClassification.Other, UpdateClassifier.Classify(null, null));
    }

    [Fact]
    public void English_titles_are_a_fallback_when_categories_are_missing()
    {
        Assert.Equal(UpdateClassification.Definition,
            UpdateClassifier.Classify([], "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.441.205.0)"));
        Assert.Equal(UpdateClassification.Feature, UpdateClassifier.Classify([], "Feature update to Windows 11, version 24H2"));
        Assert.Equal(UpdateClassification.Cumulative, UpdateClassifier.Classify([], Lcu));
    }

    [Theory]
    [InlineData("Critical", UpdateSeverity.Critical)]
    [InlineData(" important ", UpdateSeverity.Important)]
    [InlineData("Moderate", UpdateSeverity.Moderate)]
    [InlineData("LOW", UpdateSeverity.Low)]
    [InlineData("", UpdateSeverity.Unspecified)]
    [InlineData(null, UpdateSeverity.Unspecified)]
    [InlineData("Unspecified", UpdateSeverity.Unspecified)]
    public void Msrc_severity_is_parsed(string? text, UpdateSeverity expected)
    {
        Assert.Equal(expected, UpdateClassifier.ParseSeverity(text));
    }

    [Theory]
    [InlineData(0, UpdateRestartNeed.No)]
    [InlineData(1, UpdateRestartNeed.Yes)]
    [InlineData(2, UpdateRestartNeed.Maybe)]
    [InlineData(7, UpdateRestartNeed.Maybe)]
    public void Reboot_behavior_maps_to_restart_need(int behavior, UpdateRestartNeed expected)
    {
        Assert.Equal(expected, UpdateClassifier.MapRebootBehavior(behavior));
    }

    [Fact]
    public void Kb_article_comes_from_the_ids_first_then_the_title()
    {
        Assert.Equal("KB5066835", UpdateClassifier.ExtractKb("No KB in this title", ["5066835"]));
        Assert.Equal("KB890830", UpdateClassifier.ExtractKb(null, ["KB890830"]));
        Assert.Equal("KB5066835", UpdateClassifier.ExtractKb(Lcu));
        Assert.Equal("KB2267602", UpdateClassifier.ExtractKb("Security Intelligence Update - kb2267602 (Version 1.4)", ["", " "]));
        Assert.Null(UpdateClassifier.ExtractKb("Windows 11, version 25H2"));
        Assert.Null(UpdateClassifier.ExtractKb("  ", ["abc"]));
    }

    [Fact]
    public void Every_classification_has_a_short_label()
    {
        foreach (var value in Enum.GetValues<UpdateClassification>())
        {
            Assert.False(string.IsNullOrWhiteSpace(UpdateClassifier.Label(value)));
        }

        Assert.Equal("Cumulative", UpdateClassifier.Label(UpdateClassification.Cumulative));
    }
}

public class WindowsUpdateErrorTextTests
{
    [Theory]
    [InlineData(0x800F0922u)]
    [InlineData(0x80070070u)]
    [InlineData(0xC1900101u)]
    [InlineData(0x80073712u)]
    [InlineData(0x80072EFDu)]
    [InlineData(0x8024200Du)]
    public void Common_install_errors_are_explained_with_their_code(uint code)
    {
        var text = WuaResultCodes.Explain(unchecked((int)code));
        Assert.True(WuaResultCodes.IsKnown(unchecked((int)code)));
        Assert.Contains($"(error 0x{code:X8})", text);
        Assert.DoesNotContain("Search for this code", text);
    }

    [Fact]
    public void Unknown_errors_get_the_code_and_where_to_look()
    {
        var text = WuaResultCodes.Explain(unchecked((int)0x8024ABCD));
        Assert.Contains("0x8024ABCD", text);
        Assert.Contains("Search for this code", text);
    }

    [Fact]
    public void Disk_space_error_says_what_to_do()
    {
        Assert.Contains("disk space", WuaResultCodes.Explain(unchecked((int)0x80070070)), StringComparison.OrdinalIgnoreCase);
    }
}

public class UpdatePauseRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 30, 15, 500, TimeSpan.Zero);

    [Fact]
    public void Times_are_written_like_settings_writes_them_and_read_back()
    {
        var text = UpdatePauseRules.FormatRegistryTime(new DateTimeOffset(2026, 11, 3, 11, 30, 15, TimeSpan.FromHours(2)));
        Assert.Equal("2026-11-03T09:30:15Z", text);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 9, 30, 15, TimeSpan.Zero), UpdatePauseRules.ParseRegistryTime(text));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero), UpdatePauseRules.ParseRegistryTime("2026-11-03T00:00:00"));
        Assert.Null(UpdatePauseRules.ParseRegistryTime("next tuesday"));
        Assert.Null(UpdatePauseRules.ParseRegistryTime(null));
    }

    [Fact]
    public void Pause_state_follows_the_expiry_time()
    {
        var paused = UpdatePauseRules.ReadState("2026-10-20T08:00:00Z", "2026-11-10T08:00:00Z", Now);
        Assert.True(paused.IsPaused);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero), paused.PausedSince);
        Assert.False(UpdatePauseRules.ReadState("2026-09-01T08:00:00Z", "2026-10-01T08:00:00Z", Now).IsPaused);
        Assert.False(UpdatePauseRules.ReadState(null, null, Now).IsPaused);
        Assert.False(UpdatePauseRules.ReadState("x", "garbage", Now).IsPaused);
    }

    [Fact]
    public void A_new_pause_starts_now_and_lasts_whole_weeks()
    {
        var plan = UpdatePauseRules.Plan(UpdatePauseState.NotPaused, Now, 2, UpdatePauseRules.DefaultMaxPauseDays);
        Assert.True(plan.Allowed);
        Assert.False(plan.IsExtension);
        Assert.False(plan.WasShortened);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 9, 30, 15, TimeSpan.Zero), plan.Start);
        Assert.Equal(plan.Start.AddDays(14), plan.Expiry);
    }

    [Fact]
    public void Extending_keeps_the_start_and_is_capped_at_the_limit()
    {
        var since = Now.AddDays(-10);
        var current = new UpdatePauseState(true, since, Now.AddDays(11));
        var plan = UpdatePauseRules.Plan(current, Now, 3, UpdatePauseRules.DefaultMaxPauseDays);
        Assert.True(plan.Allowed);
        Assert.True(plan.IsExtension);
        Assert.True(plan.WasShortened);
        Assert.Equal(UpdatePauseRules.ParseRegistryTime(UpdatePauseRules.FormatRegistryTime(since)), plan.Start);
        Assert.Equal(plan.Start.AddDays(35), plan.Expiry);
    }

    [Fact]
    public void A_pause_at_the_limit_cannot_be_extended()
    {
        var since = Now.AddDays(-20);
        var current = new UpdatePauseState(true, since, since.AddDays(35));
        var plan = UpdatePauseRules.Plan(current, Now, 1, UpdatePauseRules.DefaultMaxPauseDays);
        Assert.False(plan.Allowed);
        Assert.Contains("as long as Windows allows", plan.Message);
        Assert.Equal(0, UpdatePauseRules.WeeksAvailable(current, Now, UpdatePauseRules.DefaultMaxPauseDays));
    }

    [Fact]
    public void A_running_pause_without_a_readable_start_is_not_extended()
    {
        var current = new UpdatePauseState(true, null, Now.AddDays(7));
        var plan = UpdatePauseRules.Plan(current, Now, 1, UpdatePauseRules.DefaultMaxPauseDays);
        Assert.False(plan.Allowed);
        Assert.Equal(UpdatePauseRules.UnknownStartMessage, plan.Message);
        Assert.True(UpdatePauseRules.HasUnknownStart(current, Now));
        Assert.Equal(0, UpdatePauseRules.WeeksAvailable(current, Now, UpdatePauseRules.DefaultMaxPauseDays));

        // A start in the future reads as unknown too.
        var state = UpdatePauseRules.ReadState("2026-12-01T00:00:00Z", "2026-11-10T00:00:00Z", Now);
        Assert.True(UpdatePauseRules.HasUnknownStart(state, Now));
        Assert.False(UpdatePauseRules.HasUnknownStart(UpdatePauseState.NotPaused, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void Only_one_to_five_weeks_are_allowed(int weeks)
    {
        Assert.False(UpdatePauseRules.Plan(UpdatePauseState.NotPaused, Now, weeks, 35).Allowed);
    }

    [Fact]
    public void Weeks_available_count_down_as_the_pause_grows()
    {
        Assert.Equal(5, UpdatePauseRules.WeeksAvailable(UpdatePauseState.NotPaused, Now, 35));
        Assert.Equal(5, UpdatePauseRules.WeeksAvailable(UpdatePauseState.NotPaused, Now, 1000));
        Assert.Equal(2, UpdatePauseRules.WeeksAvailable(UpdatePauseState.NotPaused, Now, 14));
        var since = Now.AddDays(-3);
        Assert.Equal(3, UpdatePauseRules.WeeksAvailable(new UpdatePauseState(true, since, since.AddDays(14)), Now, 35));
    }

    [Fact]
    public void Max_pause_days_falls_back_to_five_weeks_and_old_builds_go_to_settings()
    {
        Assert.Equal(35, UpdatePauseRules.EffectiveMaxPauseDays(null));
        Assert.Equal(35, UpdatePauseRules.EffectiveMaxPauseDays(0));
        Assert.Equal(35, UpdatePauseRules.EffectiveMaxPauseDays(-5));
        Assert.Equal(60, UpdatePauseRules.EffectiveMaxPauseDays(60));
        Assert.True(UpdatePauseRules.SupportsRegistryPause(26100));
        Assert.True(UpdatePauseRules.SupportsRegistryPause(19041));
        Assert.False(UpdatePauseRules.SupportsRegistryPause(18363));
        Assert.Equal(6, UpdatePauseRules.AllValues.Count);
        Assert.Contains("PauseUpdatesExpiryTime", UpdatePauseRules.AllValues);
    }
}

public class ActiveHoursRulesTests
{
    [Theory]
    [InlineData(8, 17, 9)]
    [InlineData(22, 6, 8)]
    [InlineData(0, 23, 23)]
    [InlineData(23, 0, 1)]
    public void Span_wraps_past_midnight(int start, int end, int expected)
    {
        Assert.Equal(expected, ActiveHoursRules.SpanHours(start, end));
    }

    [Theory]
    [InlineData(8, 17, true)]
    [InlineData(6, 0, true)]
    [InlineData(20, 14, true)]
    [InlineData(8, 8, false)]
    [InlineData(6, 1, false)]
    [InlineData(0, 23, false)]
    [InlineData(-1, 5, false)]
    [InlineData(5, 24, false)]
    public void At_most_eighteen_hours_are_allowed(int start, int end, bool ok)
    {
        Assert.Equal(ok, ActiveHoursRules.Validate(start, end) is null);
    }

    [Fact]
    public void A_policy_limit_shortens_the_allowed_span()
    {
        Assert.Null(ActiveHoursRules.Validate(8, 20, 12));
        Assert.Contains("policy", ActiveHoursRules.Validate(8, 21, 12));
        Assert.Equal(12, ActiveHoursRules.EffectiveMaxSpan(12));
        Assert.Equal(18, ActiveHoursRules.EffectiveMaxSpan(null));
        Assert.Equal(18, ActiveHoursRules.EffectiveMaxSpan(3));
        Assert.Equal(18, ActiveHoursRules.EffectiveMaxSpan(24));
        Assert.Null(ActiveHoursRules.Validate(6, 0, 30));
    }

    [Fact]
    public void Missing_values_use_the_windows_defaults()
    {
        Assert.Equal(new ActiveHours(8, 17, false), ActiveHoursRules.FromRegistry(null, 99, null));
        Assert.Equal(new ActiveHours(9, 1, true), ActiveHoursRules.FromRegistry(9, 1, 1));
        Assert.Equal("08:00 – 17:00", ActiveHoursRules.Describe(new ActiveHours(8, 17, false)));
        Assert.Equal("22:00 – 06:00 (set automatically)", ActiveHoursRules.Describe(new ActiveHours(22, 6, true)));
    }
}

public class UpdateHistoryMapperTests
{
    private static UpdateHistoryRecord Record(string? title, int result, int hr = 0, int operation = 1, DateTime? date = null, params string[] categories) =>
        new(title, operation, result, hr, date ?? new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), categories, null, null);

    [Fact]
    public void Uninstalls_and_blank_entries_are_left_out()
    {
        Assert.Null(UpdateHistoryMapper.Map(Record("Removed update", 2, operation: 2)));
        Assert.Null(UpdateHistoryMapper.Map(Record("  ", 2)));
        Assert.NotNull(UpdateHistoryMapper.Map(Record("Installed update", 2)));
    }

    [Fact]
    public void Failed_installs_are_explained_in_plain_english()
    {
        var entry = UpdateHistoryMapper.Map(Record("2026-09 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5063709)",
            WuaResultCodes.Failed, unchecked((int)0x800F0922), 1, null, UpdateClassifier.CategoryIds.SecurityUpdates))!;
        Assert.Equal(UpdateHistoryOutcome.Failed, entry.Outcome);
        Assert.True(entry.IsFailure);
        Assert.Equal("Failed", entry.ResultText);
        Assert.Equal("KB5063709", entry.KbArticle);
        Assert.Equal(UpdateClassification.Cumulative, entry.Classification);
        Assert.Contains("0x800F0922", entry.ErrorText);
        Assert.Contains("Secure Boot", entry.ErrorText);
    }

    [Fact]
    public void Successful_installs_have_no_error_and_cancelled_ones_say_why()
    {
        var ok = UpdateHistoryMapper.Map(Record("Update", WuaResultCodes.Succeeded))!;
        Assert.Null(ok.ErrorText);
        Assert.False(ok.IsFailure);
        Assert.Equal("Installed", ok.ResultText);

        var cancelled = UpdateHistoryMapper.Map(Record("Update", WuaResultCodes.Aborted))!;
        Assert.True(cancelled.IsFailure);
        Assert.Contains("stopped", cancelled.ErrorText);
        Assert.Equal(UpdateHistoryOutcome.Unknown, UpdateHistoryMapper.MapOutcome(9));
    }

    [Fact]
    public void Entries_are_sorted_newest_first_and_limited()
    {
        var records = Enumerable.Range(1, 150)
            .Select(i => Record($"Update {i}", WuaResultCodes.Succeeded, date: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i)))
            .Append(Record("Old placeholder", WuaResultCodes.Succeeded, date: new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)))
            .Reverse()
            .ToList();
        var mapped = UpdateHistoryMapper.MapAll(records);
        Assert.Equal(100, mapped.Count);
        Assert.Equal("Update 150", mapped[0].Title);
        Assert.Equal(DateTimeKind.Utc, mapped[0].Date!.Value.UtcDateTime.Kind);

        var withPlaceholder = UpdateHistoryMapper.MapAll(records, 500);
        Assert.Equal(151, withPlaceholder.Count);
        Assert.Null(withPlaceholder[^1].Date);
    }

    [Fact]
    public void Last_successful_install_skips_failures()
    {
        var entries = UpdateHistoryMapper.MapAll(
        [
            Record("A", WuaResultCodes.Succeeded, date: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
            Record("B", WuaResultCodes.Failed, unchecked((int)0x80070070), date: new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc)),
            Record("C", WuaResultCodes.SucceededWithErrors, date: new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc)),
        ]);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), UpdateHistoryMapper.LastSuccessfulInstall(entries));
        Assert.Null(UpdateHistoryMapper.LastSuccessfulInstall([]));
    }
}

public class UpdatePolicyRulesTests
{
    private static Dictionary<string, object?> Values(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void No_policy_values_means_not_managed()
    {
        var status = UpdatePolicyRules.Evaluate(UpdatePolicyValues.Empty);
        Assert.False(status.IsManaged);
        Assert.False(status.PauseManaged);
        Assert.False(status.ActiveHoursManaged);
        Assert.Empty(status.Notes);
    }

    [Fact]
    public void Disabling_pause_through_group_policy_makes_pause_read_only()
    {
        var status = UpdatePolicyRules.Evaluate(new UpdatePolicyValues(Values(("SetDisablePauseUXAccess", 1)), Values(), Values()));
        Assert.True(status.IsManaged);
        Assert.True(status.PauseManaged);
        Assert.False(status.ActiveHoursManaged);
        Assert.Contains(status.Notes, n => n.Contains("Pausing"));
    }

    [Fact]
    public void A_policy_pause_start_time_manages_pause_but_an_empty_one_does_not()
    {
        Assert.True(UpdatePolicyRules.Evaluate(new UpdatePolicyValues(Values(("PauseQualityUpdatesStartTime", "2026-10-01")), Values(), Values())).PauseManaged);
        var empty = UpdatePolicyRules.Evaluate(new UpdatePolicyValues(Values(("PauseQualityUpdatesStartTime", ""), ("DeferQualityUpdatesPeriodInDays", 7)), Values(), Values()));
        Assert.False(empty.PauseManaged);
        Assert.Contains(empty.Notes, n => n.Contains("7 day(s)"));
    }

    [Fact]
    public void Group_policy_active_hours_are_shown_with_their_times()
    {
        var status = UpdatePolicyRules.Evaluate(new UpdatePolicyValues(
            Values(("SetActiveHours", 1), ("ActiveHoursStart", 7), ("ActiveHoursEnd", 19)), Values(), Values()));
        Assert.True(status.ActiveHoursManaged);
        Assert.Contains(status.Notes, n => n.Contains("07:00 – 19:00"));
    }

    [Fact]
    public void An_update_server_is_used_only_when_it_is_switched_on()
    {
        var gp = Values(("WUServer", "http://wsus.contoso.local:8530"));
        Assert.True(UpdatePolicyRules.Evaluate(new UpdatePolicyValues(gp, Values(("UseWUServer", 1)), Values())).UsesUpdateServer);
        Assert.False(UpdatePolicyRules.Evaluate(new UpdatePolicyValues(gp, Values(("UseWUServer", 0)), Values())).UsesUpdateServer);
    }

    [Fact]
    public void Device_management_values_that_turn_nothing_on_do_not_lock_the_page()
    {
        var mdm = Values(("PauseQualityUpdates", 0), ("PauseFeatureUpdates", 0), ("AllowAutoUpdate", 2), ("ActiveHoursStart", 99));
        var status = UpdatePolicyRules.Evaluate(new UpdatePolicyValues(Values(), Values(), mdm));
        Assert.True(status.IsManaged);
        Assert.False(status.PauseManaged);
        Assert.False(status.ActiveHoursManaged);
        Assert.False(status.AutomaticUpdatesOff);

        var paused = Values(("PauseQualityUpdates", 1));
        Assert.True(UpdatePolicyRules.Evaluate(new UpdatePolicyValues(Values(), Values(), paused)).PauseManaged);
        var started = Values(("PauseFeatureUpdatesStartTime", "2026-10-01"));
        Assert.True(UpdatePolicyRules.Evaluate(new UpdatePolicyValues(Values(), Values(), started)).PauseManaged);
    }

    [Fact]
    public void The_active_hours_max_range_policy_narrows_the_span()
    {
        var gp = Values(("SetActiveHoursMaxRange", 1), ("ActiveHoursMaxRange", 12));
        var status = UpdatePolicyRules.Evaluate(new UpdatePolicyValues(gp, Values(), Values()));
        Assert.Equal(12, status.ActiveHoursMaxSpan);
        Assert.False(status.ActiveHoursManaged);
        Assert.Contains(status.Notes, n => n.Contains("12 hours"));

        // Without the enabling switch the Group Policy range is ignored; MDM needs none.
        var off = Values(("SetActiveHoursMaxRange", 0), ("ActiveHoursMaxRange", 12));
        Assert.Null(UpdatePolicyRules.Evaluate(new UpdatePolicyValues(off, Values(), Values())).ActiveHoursMaxSpan);
        Assert.Equal(10, UpdatePolicyRules.Evaluate(new UpdatePolicyValues(Values(), Values(), Values(("ActiveHoursMaxRange", 10)))).ActiveHoursMaxSpan);

        // Out-of-range or the usual 18 change nothing.
        Assert.Null(UpdatePolicyRules.Evaluate(new UpdatePolicyValues(Values(), Values(), Values(("ActiveHoursMaxRange", 4)))).ActiveHoursMaxSpan);
        Assert.Null(UpdatePolicyRules.Evaluate(new UpdatePolicyValues(Values(), Values(), Values(("ActiveHoursMaxRange", 18)))).ActiveHoursMaxSpan);
    }

    [Fact]
    public void Device_management_bookkeeping_alone_is_not_a_policy()
    {
        var bookkeeping = Values(("ActiveHoursStart_ProviderSet", 1), ("ActiveHoursStart_WinningProvider", "{GUID}"));
        Assert.False(UpdatePolicyRules.Evaluate(new UpdatePolicyValues(Values(), Values(), bookkeeping)).IsManaged);

        var mdm = Values(("ActiveHoursStart", 9), ("ActiveHoursEnd", 18), ("AllowAutoUpdate", 5));
        var status = UpdatePolicyRules.Evaluate(new UpdatePolicyValues(Values(), Values(), mdm));
        Assert.True(status.IsManaged);
        Assert.True(status.ActiveHoursManaged);
        Assert.True(status.AutomaticUpdatesOff);
    }

    [Fact]
    public void Turning_off_access_to_windows_update_makes_everything_read_only()
    {
        var status = UpdatePolicyRules.Evaluate(new UpdatePolicyValues(Values(("SetDisableUXWUAccess", 1)), Values(("NoAutoUpdate", 1)), Values()));
        Assert.True(status.AccessDisabled);
        Assert.True(status.PauseManaged);
        Assert.True(status.ActiveHoursManaged);
        Assert.True(status.AutomaticUpdatesOff);
        Assert.Contains(status.Notes, n => n.Contains("turned off access"));
    }

    [Fact]
    public void Dword_values_are_read_from_numbers_and_numeric_text()
    {
        var values = Values(("A", 1), ("B", 2L), ("C", " 3 "), ("D", "x"), ("E", null));
        Assert.Equal(1, UpdatePolicyRules.Dword(values, "a"));
        Assert.Equal(2, UpdatePolicyRules.Dword(values, "B"));
        Assert.Equal(3, UpdatePolicyRules.Dword(values, "C"));
        Assert.Null(UpdatePolicyRules.Dword(values, "D"));
        Assert.Null(UpdatePolicyRules.Dword(values, "E"));
        Assert.Null(UpdatePolicyRules.Dword(values, "missing"));
        Assert.True(UpdatePolicyRules.IsServiceDisabled(4));
        Assert.False(UpdatePolicyRules.IsServiceDisabled(3));
    }
}

public class UpdateInstallSummaryTests
{
    private static PendingUpdate Update(string id, bool optional = false, bool needsInput = false) =>
        new(id, "Update " + id, null, 1000, UpdateClassification.Other, UpdateSeverity.Unspecified, UpdateRestartNeed.No, false, null)
        {
            IsOptional = optional,
            NeedsUserInput = needsInput,
        };

    [Fact]
    public void Summary_counts_installs_and_mentions_the_restart()
    {
        var a = Update("a");
        var b = Update("b");
        var result = new UpdateInstallResult([new(a, true, "Installed.", true), new(b, false, "Not installed: reason", false)], true);
        Assert.Equal("1 of 2 updates installed. Restart Windows to finish.", UpdateInstallSummary.Summarize(result));
        Assert.Equal(["Update a: Installed.", "Update b: Not installed: reason"], UpdateInstallSummary.Lines(result));
        Assert.Equal("The update was installed.", UpdateInstallSummary.Summarize(new UpdateInstallResult([new(a, true, "Installed.", false)], false)));
    }

    [Fact]
    public void Install_all_leaves_out_optional_updates_and_ones_that_need_settings()
    {
        var picked = UpdateInstallSummary.InstallAllSelection([Update("a"), Update("b", optional: true), Update("c", needsInput: true), Update("d")]);
        Assert.Equal(["a", "d"], picked.Select(u => u.Id));
    }
}

public class DemoWindowsUpdateServiceTests
{
    [Fact]
    public async Task Demo_has_status_pending_updates_and_a_history_with_failures()
    {
        var svc = new DemoWindowsUpdateService();
        var status = await svc.GetStatusAsync(CancellationToken.None);
        Assert.NotNull(status.LastCheck);
        Assert.NotNull(status.LastInstall);
        Assert.False(status.Policy.IsManaged);

        var scan = await svc.CheckForUpdatesAsync(null, CancellationToken.None);
        Assert.True(scan.Updates.Count >= 4);
        Assert.DoesNotContain(scan.Updates, u => u.Classification == UpdateClassification.Driver);
        Assert.Contains(scan.Updates, u => u.IsOptional);

        var history = await svc.GetHistoryAsync(100, CancellationToken.None);
        Assert.InRange(history.Entries.Count, 20, 100);
        Assert.Contains(history.Entries, e => e.IsFailure && e.ErrorText is not null);
    }

    [Fact]
    public async Task Demo_pause_resume_and_install_change_only_memory()
    {
        var svc = new DemoWindowsUpdateService();
        Assert.True((await svc.PauseAsync(2, CancellationToken.None)).Success);
        Assert.True((await svc.GetStatusAsync(CancellationToken.None)).Pause.IsPaused);
        Assert.True((await svc.ResumeAsync(CancellationToken.None)).Success);
        Assert.False((await svc.GetStatusAsync(CancellationToken.None)).Pause.IsPaused);
        Assert.False((await svc.SetActiveHoursAsync(1, 23, CancellationToken.None)).Success);

        var scan = await svc.CheckForUpdatesAsync(null, CancellationToken.None);
        var first = scan.Updates[0];
        var result = await svc.InstallAsync([first], null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(1, result.Value!.Installed);
        var after = await svc.CheckForUpdatesAsync(null, CancellationToken.None);
        Assert.DoesNotContain(after.Updates, u => u.Id == first.Id);
    }
}
