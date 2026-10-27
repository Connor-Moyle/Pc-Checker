using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PcChecker.Core.Benchmark;
using PcChecker.Core.Common;
using PcChecker.Core.Displays;
using PcChecker.Core.Fans;
using PcChecker.Core.Gaming;
using PcChecker.Core.Input;
using PcChecker.Core.Lighting;
using PcChecker.Core.Logging;
using PcChecker.Core.Scheduling;
using PcChecker.Core.Settings;
using PcChecker.Core.SetupBackup;
using PcChecker.Core.Updates;
using Xunit;

namespace PcChecker.Core.Tests;

public class SetupBackupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 14, 30, 0, TimeSpan.Zero);
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

    // ----- Catalog -----

    [Fact]
    public void Every_settings_property_belongs_to_exactly_one_section()
    {
        var properties = typeof(AppSettings).GetProperties().Where(p => p.CanWrite).Select(p => p.Name).ToList();
        var covered = SetupSectionCatalog.All.SelectMany(s => s.SettingsProperties).ToList();

        Assert.Equal(covered.Count, covered.Distinct().Count());
        Assert.Equal(properties.OrderBy(p => p), covered.OrderBy(p => p));
        Assert.Equal(SetupSectionCatalog.All.Count, SetupSectionCatalog.All.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Machine_fields_point_at_real_settings()
    {
        var tree = SetupJson.ToNode(AppSettings.CreateDefault());
        foreach (var path in SetupMachineFields.All)
        {
            var parts = path.Split('.');
            var section = Assert.IsType<JsonObject>(tree[parts[0]]);
            Assert.True(section.ContainsKey(parts[1]), path);
        }
    }

    // ----- Export -----

    [Fact]
    public void Export_writes_a_versioned_file_with_every_section()
    {
        var root = SetupExporter.Compose(CustomSettings(), Runs(2), new SetupExportOptions(true), "0.5.0", Now, "DESKTOP-1");

        Assert.Equal(SetupFileFormat.FormatName, root["Format"]!.GetValue<string>());
        Assert.Equal(SetupFileFormat.CurrentSchemaVersion, root["SchemaVersion"]!.GetValue<int>());
        Assert.Equal("0.5.0", root["AppVersion"]!.GetValue<string>());
        Assert.Equal(SetupSectionCatalog.All.Select(s => s.Id), SetupExporter.SectionIds(root));
        Assert.All(root["Sections"]!.AsObject(), p => Assert.Equal(1, p.Value!["Version"]!.GetValue<int>()));
        Assert.Equal(2, root["Sections"]!["benchmarks"]!["Data"]!["Runs"]!.AsArray().Count);
    }

    [Fact]
    public void Benchmark_history_is_left_out_unless_asked_for()
    {
        var root = SetupExporter.Compose(CustomSettings(), Runs(2), new SetupExportOptions(false), "0.5.0", Now, null);

        Assert.DoesNotContain(SetupSectionIds.Benchmarks, SetupExporter.SectionIds(root));
        Assert.Contains(SetupSectionIds.Fans, SetupExporter.SectionIds(root));
    }

    [Fact]
    public void Export_never_contains_the_api_key_or_this_pcs_state()
    {
        var settings = CustomSettings();
        settings.Claude.ProtectedApiKey = "AQAAANCMnd8BFdERjHoAwE-SECRET";
        settings.General.WindowWidth = 1717;
        settings.Overclock.ConsentAccepted = true;
        settings.Fans.ActiveProfileName = "Silent";

        var text = SetupExporter.Export(settings, null, SetupExportOptions.Everything, "0.5.0", Now, null);

        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("ProtectedApiKey", text);
        Assert.DoesNotContain("1717", text);
        Assert.DoesNotContain("ConsentAccepted", text);
        Assert.DoesNotContain("ActiveProfileName", text);
        Assert.Contains("claude-opus-5-5", text); // the rest of the assistant settings are kept
    }

    // ----- Reading and checking -----

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"Format\": \"Something.Else\", \"SchemaVersion\": 1, \"Sections\": {} }")]
    [InlineData("{ \"Format\": \"PcChecker.Setup\", \"Sections\": { \"fans\": { \"Version\": 1, \"Data\": { \"Fans\": {} } } } }")]
    [InlineData("{ \"Format\": \"PcChecker.Setup\", \"SchemaVersion\": 1 }")]
    public void Files_that_are_not_valid_setups_are_refused_with_a_reason(string text)
    {
        var result = SetupFileReader.Parse(text);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void Unknown_and_newer_parts_are_skipped_with_a_note_and_the_rest_is_read()
    {
        const string text = """
            {
              "format": "PcChecker.Setup",
              "schemaVersion": 3,
              "sections": {
                "fans": { "Version": 1, "Data": { "Fans": { "MinimumPercent": 30 } } },
                "lighting": { "Version": 7, "Data": { "Lighting": { "OpenRgbPort": 1234 } } },
                "cloudSync": { "Version": 1, "Data": { "Anything": {} } },
                "displays": "broken"
              }
            }
            """;

        var result = SetupFileReader.Parse(text);

        Assert.True(result.Success, result.Message);
        var doc = result.Value!;
        Assert.Equal([SetupSectionIds.Fans], doc.Sections.Keys);
        Assert.Equal(["lighting", "cloudSync", "displays"], doc.Skipped.Select(s => s.Id));
        Assert.Contains("newer", doc.Skipped[0].Reason);
        Assert.Contains(doc.Notes, n => n.Contains("newer version"));

        var preview = SetupPreviewBuilder.Build(AppSettings.CreateDefault(), [], doc, Now);
        Assert.Equal(4, preview.Sections.Count);
        Assert.All(preview.Sections.Where(s => s.Id != SetupSectionIds.Fans), s => Assert.False(s.CanImport));

        // Asking for a skipped part changes nothing and never throws.
        var outcome = SetupApplier.Apply(AppSettings.CreateDefault(), doc, [SetupSectionIds.Lighting, "cloudSync"], Now);
        Assert.Empty(outcome.AppliedSections);
        Assert.Equal(6742, outcome.Settings.Lighting.OpenRgbPort);
    }

    [Fact]
    public void A_plain_settings_file_is_migrated_without_its_api_key()
    {
        var settings = CustomSettings();
        settings.Claude.ProtectedApiKey = "SECRET-KEY";
        var settingsJson = SetupJson.ToNode(settings).ToJsonString();

        var result = SetupFileReader.Parse(settingsJson);

        Assert.True(result.Success, result.Message);
        var doc = result.Value!;
        Assert.Contains(doc.Notes, n => n.Contains("settings file"));
        Assert.True(doc.Has(SetupSectionIds.App));
        Assert.True(doc.Has(SetupSectionIds.Fans));
        Assert.False(doc.Has(SetupSectionIds.Benchmarks));
        Assert.DoesNotContain("SECRET-KEY", doc.Sections[SetupSectionIds.App].Data.ToJsonString());

        var outcome = SetupApplier.Apply(AppSettings.CreateDefault(), doc, SetupSectionCatalog.All.Select(s => s.Id), Now);
        Assert.Equal("Quiet nights", outcome.Settings.Fans.Profiles[0].Name);
        Assert.Null(outcome.Settings.Claude.ProtectedApiKey);
    }

    // ----- Import -----

    [Fact]
    public void Round_trip_restores_the_whole_setup()
    {
        var source = CustomSettings();
        var text = SetupExporter.Export(source, Runs(3), SetupExportOptions.Everything, "0.5.0", Now, "DESKTOP-1");
        var doc = SetupFileReader.Parse(text).Value!;

        var outcome = SetupApplier.Apply(AppSettings.CreateDefault(), doc, SetupSectionCatalog.All.Select(s => s.Id), Now);

        Assert.Equal(SetupSectionCatalog.SettingsSections.Select(s => s.Id), outcome.AppliedSections);
        var expected = SetupJson.ToNode(source);
        var actual = SetupJson.ToNode(outcome.Settings);
        foreach (var section in SetupSectionCatalog.SettingsSections)
        {
            Assert.True(JsonNode.DeepEquals(SetupJson.SectionData(expected, section), SetupJson.SectionData(actual, section)), section.Id);
        }

        var (runs, unreadable) = SetupApplier.ReadRuns(doc.Sections[SetupSectionIds.Benchmarks]);
        Assert.Equal(3, runs.Count);
        Assert.Equal(0, unreadable);
    }

    [Fact]
    public void Import_keeps_this_pcs_api_key_and_state_even_when_the_file_has_them()
    {
        const string text = """
            { "Format": "PcChecker.Setup", "SchemaVersion": 1, "Sections": {
              "app": { "Version": 1, "Data": { "Claude": { "ProtectedApiKey": "EVIL", "Model": "claude-test" },
                                              "General": { "Theme": "Dark", "WindowWidth": 10 } } },
              "overclock": { "Version": 1, "Data": { "Overclock": { "ConsentAccepted": true, "MaxCpuTempC": 80 } } } } }
            """;
        var current = AppSettings.CreateDefault();
        current.Claude.ProtectedApiKey = "MINE";
        current.General.WindowWidth = 1600;

        var outcome = SetupApplier.Apply(current, SetupFileReader.Parse(text).Value!, [SetupSectionIds.App, SetupSectionIds.Overclock], Now);

        Assert.Equal("MINE", outcome.Settings.Claude.ProtectedApiKey);
        Assert.Equal("claude-test", outcome.Settings.Claude.Model);
        Assert.Equal(AppTheme.Dark, outcome.Settings.General.Theme);
        Assert.Equal(1600, outcome.Settings.General.WindowWidth);
        Assert.False(outcome.Settings.Overclock.ConsentAccepted);
        Assert.Equal(80, outcome.Settings.Overclock.MaxCpuTempC);
    }

    [Fact]
    public void Partial_import_changes_only_the_chosen_sections_and_keeps_missing_fields()
    {
        var doc = SetupFileReader.Parse(SetupExporter.Export(CustomSettings(), null, new SetupExportOptions(false), null, Now, null)).Value!;
        var current = AppSettings.CreateDefault();
        current.Lighting.OpenRgbPort = 9999;

        var outcome = SetupApplier.Apply(current, doc, [SetupSectionIds.Fans], Now);

        Assert.Equal([SetupSectionIds.Fans], outcome.AppliedSections);
        Assert.Single(outcome.Settings.Fans.Profiles);
        Assert.Equal(9999, outcome.Settings.Lighting.OpenRgbPort);
        Assert.Empty(outcome.Settings.Gaming.Profiles);

        // A file that only has some fields of a section leaves the others alone.
        const string sparse = """{ "Format": "PcChecker.Setup", "SchemaVersion": 1, "Sections": { "lighting": { "Version": 1, "Data": { "Lighting": { "AutoConnect": false } } } } }""";
        var sparseOutcome = SetupApplier.Apply(current, SetupFileReader.Parse(sparse).Value!, [SetupSectionIds.Lighting], Now);
        Assert.False(sparseOutcome.Settings.Lighting.AutoConnect);
        Assert.Equal(9999, sparseOutcome.Settings.Lighting.OpenRgbPort);
    }

    [Fact]
    public void A_section_with_unreadable_values_is_skipped_and_the_others_still_import()
    {
        const string text = """
            { "Format": "PcChecker.Setup", "SchemaVersion": 1, "Sections": {
              "fans": { "Version": 1, "Data": { "Fans": { "MinimumPercent": { "oops": 1 } } } },
              "memory": { "Version": 1, "Data": { "Memory": { "LowMemoryPercent": 15 } } } } }
            """;
        var doc = SetupFileReader.Parse(text).Value!;

        var outcome = SetupApplier.Apply(AppSettings.CreateDefault(), doc, [SetupSectionIds.Fans, SetupSectionIds.Memory], Now);
        var preview = SetupPreviewBuilder.Build(AppSettings.CreateDefault(), [], doc, Now);

        Assert.Equal([SetupSectionIds.Memory], outcome.AppliedSections);
        Assert.Equal(15, outcome.Settings.Memory.LowMemoryPercent);
        Assert.Contains(outcome.Notes, n => n.StartsWith("Fans was skipped", StringComparison.Ordinal));
        Assert.Equal(SetupSectionState.Skipped, preview.Sections.Single(s => s.Id == SetupSectionIds.Fans).State);
    }

    [Fact]
    public void Imported_values_are_clamped_to_the_allowed_ranges()
    {
        const string text = """
            { "Format": "PcChecker.Setup", "SchemaVersion": 1, "Sections": {
              "app": { "Version": 1, "Data": { "Monitoring": { "PollIntervalMs": 5, "CpuCritC": 400 } } },
              "fans": { "Version": 1, "Data": { "Fans": { "MinimumPercent": -10 } } },
              "overlay": { "Version": 1, "Data": { "Overlay": { "Opacity": 7 } } },
              "overclock": { "Version": 1, "Data": { "Overclock": { "MaxGpuCoreOffsetMhz": 99999, "MaxGpuPowerLimitPercent": 20 } } },
              "schedules": { "Version": 1, "Data": { "Schedules": { "Jobs": [ { "Job": "Report", "Enabled": true, "Time": "99:99", "KeepReports": 5000 } ] } } } } }
            """;

        var s = SetupApplier.Apply(AppSettings.CreateDefault(), SetupFileReader.Parse(text).Value!, SetupSectionCatalog.All.Select(x => x.Id), Now).Settings;

        Assert.Equal(250, s.Monitoring.PollIntervalMs);
        Assert.Equal(125, s.Monitoring.CpuCritC);
        Assert.Equal(0, s.Fans.MinimumPercent);
        Assert.Equal(1.0, s.Overlay.Opacity);
        Assert.Equal(2000, s.Overclock.MaxGpuCoreOffsetMhz);
        Assert.Equal(100, s.Overclock.MaxGpuPowerLimitPercent);
        Assert.Equal(ScheduledJobIds.All.Count, s.Schedules.Jobs.Count);
        var report = s.Schedules.For(ScheduledJobKind.Report);
        Assert.Null(ScheduleRules.Validate(report));
    }

    [Fact]
    public void A_lower_history_retention_from_a_file_waits_out_the_grace_period()
    {
        const string text = """{ "Format": "PcChecker.Setup", "SchemaVersion": 1, "Sections": { "app": { "Version": 1, "Data": { "History": { "RetentionDays": 7 } } } } }""";
        var current = AppSettings.CreateDefault(); // 90 days

        var history = SetupApplier.Apply(current, SetupFileReader.Parse(text).Value!, [SetupSectionIds.App], Now).Settings.History;

        Assert.Equal(7, history.RetentionDays);
        Assert.Equal(90, history.PreviousRetentionDays);
        Assert.Equal(Now, history.RetentionLoweredAt);
        Assert.Equal(90, history.EffectiveRetention(Now.AddHours(1)));
    }

    [Fact]
    public void Reset_puts_only_the_chosen_sections_back_and_keeps_the_api_key()
    {
        var current = CustomSettings();
        current.Claude.ProtectedApiKey = "MINE";
        current.Fans.ActiveProfileName = "Quiet nights";

        var outcome = SetupApplier.Reset(current, [SetupSectionIds.Fans, SetupSectionIds.App], Now);

        Assert.Empty(outcome.Settings.Fans.Profiles);
        Assert.Equal(20, outcome.Settings.Fans.MinimumPercent);
        Assert.Equal("Quiet nights", outcome.Settings.Fans.ActiveProfileName);
        Assert.Equal(AppTheme.System, outcome.Settings.General.Theme);
        Assert.Equal("MINE", outcome.Settings.Claude.ProtectedApiKey);
        Assert.Single(outcome.Settings.Lighting.Profiles); // not chosen, unchanged
    }

    [Fact]
    public void CopySections_replaces_only_the_chosen_parts_of_the_live_settings()
    {
        var live = AppSettings.CreateDefault();
        var lighting = live.Lighting;
        var source = CustomSettings();

        SetupApplier.CopySections(source, live, [SetupSectionIds.Schedules, SetupSectionIds.Fans]);

        Assert.Same(source.Fans, live.Fans);
        Assert.Same(source.Schedules, live.Schedules);
        Assert.Same(source.Tray, live.Tray);
        Assert.Same(lighting, live.Lighting);
    }

    // ----- Preview -----

    [Fact]
    public void Preview_lists_what_changes_by_name()
    {
        var current = CustomSettings();
        var incoming = CustomSettings();
        incoming.General.Theme = AppTheme.Dark;
        incoming.Fans.Profiles.Add(new FanProfile { Name = "Full blast" });
        incoming.Lighting.Profiles.Clear();
        incoming.Input.Profiles[0] = incoming.Input.Profiles[0] with { Description = "Changed" };
        var doc = SetupFileReader.Parse(SetupExporter.Export(incoming, null, new SetupExportOptions(false), null, Now, null)).Value!;

        var preview = SetupPreviewBuilder.Build(current, [], doc, Now);
        string Lines(string id) => string.Join("\n", preview.Sections.Single(s => s.Id == id).Changes.Select(c => c.Describe()));

        Assert.Contains("General › Theme: System → Dark", Lines(SetupSectionIds.App));
        Assert.Equal("Profiles \"Full blast\": added", Lines(SetupSectionIds.Fans));
        Assert.Equal("Profiles \"Studio white\": removed", Lines(SetupSectionIds.Lighting));
        Assert.Equal("Profiles \"FPS aim\": changed", Lines(SetupSectionIds.Input));
        Assert.Equal(SetupSectionState.Unchanged, preview.Sections.Single(s => s.Id == SetupSectionIds.Games).State);
        Assert.False(preview.Sections.Single(s => s.Id == SetupSectionIds.Games).SelectedByDefault);
        Assert.True(preview.Sections.Single(s => s.Id == SetupSectionIds.Fans).SelectedByDefault);
        Assert.True(preview.HasChanges);
    }

    [Fact]
    public void Preview_counts_new_benchmark_runs_and_merge_adds_only_those()
    {
        var mine = Runs(2);
        var file = mine.Concat(Runs(3)).ToList();
        var doc = SetupFileReader.Parse(SetupExporter.Export(AppSettings.CreateDefault(), file, SetupExportOptions.Everything, null, Now, null)).Value!;

        var row = SetupPreviewBuilder.Build(AppSettings.CreateDefault(), mine, doc, Now).Sections.Single(s => s.Id == SetupSectionIds.Benchmarks);
        var merged = SetupApplier.MergeRuns(mine, SetupApplier.ReadRuns(doc.Sections[SetupSectionIds.Benchmarks]).Runs);

        Assert.Equal("5 runs in the file, 3 new", row.Summary);
        Assert.Equal(3, row.Changes.Count);
        Assert.Equal(5, merged.Count);
        Assert.Equal(merged.Count, merged.Select(r => r.Id).Distinct().Count());
    }

    // ----- Scheduled tasks -----

    [Fact]
    public void Schedule_sync_creates_updates_and_removes_tasks_as_needed()
    {
        var before = new SchedulesSettings();
        before.For(ScheduledJobKind.Health).Enabled = true;
        before.For(ScheduledJobKind.Cleanup).Enabled = true;
        before.For(ScheduledJobKind.Drivers).Enabled = true;
        var after = new SchedulesSettings();
        after.For(ScheduledJobKind.Health).Enabled = true; // same task: nothing to do
        after.For(ScheduledJobKind.Cleanup).Enabled = true;
        after.For(ScheduledJobKind.Cleanup).Time = "21:15"; // changed while on: update
        after.For(ScheduledJobKind.Report).Enabled = true; // newly on: create
        // Drivers turned off: remove

        var steps = SetupScheduleSync.Plan(before, new TraySettings(), after, new TraySettings { StartWithWindows = true });

        Assert.Equal(
            [
                (ScheduleSyncAction.CreateOrUpdate, (ScheduledJobKind?)ScheduledJobKind.Cleanup),
                (ScheduleSyncAction.Delete, ScheduledJobKind.Drivers),
                (ScheduleSyncAction.CreateOrUpdate, ScheduledJobKind.Report),
                (ScheduleSyncAction.SetStartWithWindows, null),
            ],
            steps.Select(s => (s.Action, s.Job)));
        Assert.True(steps[^1].StartWithWindows);
        Assert.Empty(SetupScheduleSync.Plan(after, new TraySettings(), after, new TraySettings()));
    }

    // ----- Service (real files in a temporary folder) -----

    [Fact]
    public async Task Import_backs_up_first_and_undo_restores_the_previous_setup()
    {
        var settings = new JsonSettingsService(Path.Combine(_dir, "app"));
        await settings.LoadAsync();
        settings.Current.Claude.ProtectedApiKey = "MINE";
        settings.Current.Overlay.Hotkey = "Ctrl+Alt+F1";
        await settings.SaveAsync();
        var history = new JsonBenchmarkHistoryStore(Path.Combine(_dir, "app", "benchmarks.json"));
        await history.AddAsync(Runs(1)[0], CancellationToken.None);
        var tasks = new RecordingTaskService();
        var journal = new RecordingJournal();
        var store = new FileSetupBackupStore(Path.Combine(_dir, "app", "backups"));
        var service = new SetupBackupService(settings, history, tasks, journal, store, new DemoAppUpdateService(), NullLogger<SetupBackupService>.Instance);

        var source = CustomSettings();
        source.Overlay.Hotkey = "Ctrl+Shift+F9";
        var file = Path.Combine(_dir, "setup.pcchecker");
        await File.WriteAllTextAsync(file, SetupExporter.Export(source, Runs(2), SetupExportOptions.Everything, "0.5.0", Now, "OTHER-PC"));

        var preview = await service.PreviewAsync(file, CancellationToken.None);
        Assert.True(preview.Success, preview.Message);
        var chosen = preview.Value!.Sections.Where(s => s.SelectedByDefault).Select(s => s.Id).ToList();
        Assert.Contains(SetupSectionIds.Schedules, chosen);
        Assert.Contains(SetupSectionIds.Benchmarks, chosen);

        var imported = await service.ImportAsync(preview.Value.Document, chosen, "setup.pcchecker", CancellationToken.None);

        Assert.True(imported.Success, imported.Message);
        Assert.True(File.Exists(imported.Value!.BackupPath));
        Assert.StartsWith(Path.Combine(_dir, "app", "backups", "setup-"), imported.Value.BackupPath);
        Assert.Equal("Ctrl+Shift+F9", settings.Current.Overlay.Hotkey);
        Assert.Equal("MINE", settings.Current.Claude.ProtectedApiKey);
        Assert.Equal(3, (await history.LoadAsync(CancellationToken.None)).Count);
        Assert.Contains(tasks.Calls, c => c == "create Report");
        Assert.Contains(journal.Records, r => r.Module == SetupBackupService.JournalModule && r.Reversible);
        Assert.NotNull(service.LastChange);

        // The saved file has the new values too.
        var reloaded = new JsonSettingsService(Path.Combine(_dir, "app"));
        await reloaded.LoadAsync();
        Assert.Equal("Ctrl+Shift+F9", reloaded.Current.Overlay.Hotkey);

        var undone = await service.UndoAsync(CancellationToken.None);

        Assert.True(undone.Success, undone.Message);
        Assert.Equal("Ctrl+Alt+F1", settings.Current.Overlay.Hotkey);
        Assert.Empty(settings.Current.Fans.Profiles);
        Assert.Single(await history.LoadAsync(CancellationToken.None));
        Assert.Contains(tasks.Calls, c => c == "delete Report");
        Assert.Null(service.LastChange);
    }

    [Fact]
    public async Task Import_stops_without_changes_when_the_backup_cannot_be_saved()
    {
        var settings = new JsonSettingsService(Path.Combine(_dir, "app2"));
        await settings.LoadAsync();
        var blocker = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(blocker, "a file where the backups folder should be");
        var service = new SetupBackupService(settings, new DemoBenchmarkHistoryStore(), new RecordingTaskService(), new RecordingJournal(),
            new FileSetupBackupStore(Path.Combine(blocker, "backups")), new DemoAppUpdateService(), NullLogger<SetupBackupService>.Instance);
        var doc = SetupFileReader.Parse(SetupExporter.Export(CustomSettings(), null, new SetupExportOptions(false), null, Now, null)).Value!;

        var result = await service.ImportAsync(doc, [SetupSectionIds.Fans], "x", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Nothing was changed", result.Message);
        Assert.Empty(settings.Current.Fans.Profiles);
        Assert.Null(service.LastChange);
    }

    // ----- Sample data -----

    private static AppSettings CustomSettings()
    {
        var s = AppSettings.CreateDefault();
        s.Fans.MinimumPercent = 25;
        s.Fans.Profiles.Add(new FanProfile
        {
            Name = "Quiet nights",
            Fans =
            {
                ["/lpc/nct6798d/control/0"] = new FanAssignment
                {
                    Mode = FanMode.Curve,
                    Curve = new FanCurve { TemperatureSensorId = "/amdcpu/0/temperature/2", Points = [new(30, 20), new(70, 60), new(85, 100)] },
                },
            },
        });
        s.Lighting.Profiles.Add(new LightingProfile { Name = "Studio white", AllColor = new RgbColor(255, 255, 255) });
        s.Overlay.Presets.Add(new OverlayPreset { Name = "Minimal", Metrics = [new OverlayMetric { Id = OverlayMetricIds.Fps, Label = "FPS" }] });
        s.Gaming.Profiles.Add(new GameProfile { Name = "Elden Ring", ExeName = "eldenring.exe", OverlayPresetName = "Minimal", FanProfileName = "Quiet nights" });
        s.Displays.Profiles.Add(new DisplayProfile { Name = "Desk", Modes = { ["\\\\.\\DISPLAY1"] = new DisplayMode(2560, 1440, 165, 32) }, PrimaryDevice = "\\\\.\\DISPLAY1" });
        s.Input.Profiles.Add(new InputProfile("custom.fps", "FPS aim", "Low sensitivity", new MouseSettings { Speed = 6, EnhancePointerPrecision = false }, new KeyboardSettings(), false));
        s.Memory.LowMemoryPercent = 12;
        s.Overclock.Profiles.Add(new Overclocking.OverclockProfile { Name = "Mild", GpuCoreOffsetMhz = 75 });
        s.Schedules.For(ScheduledJobKind.Report).Enabled = true;
        s.Tray.ShowTemperatureInIcon = true;
        s.Latency.PingTargets = ["gateway", "9.9.9.9"];
        return s;
    }

    private static List<BenchmarkRun> Runs(int count) =>
        Enumerable.Range(0, count).Select(i => new BenchmarkRun(
            Guid.NewGuid(), Now.AddDays(-i), 30, i == 0 ? "Before overclock" : null, new BenchmarkSystemInfo("PC", "CPU", 16, 32L << 30, "GPU", "C:"),
            [new BenchmarkTestResult(BenchmarkTestKind.CpuSingleCore, BenchmarkTestStatus.Completed, 1000 + i, [new BenchmarkMetric("cpu.hash", "Hashing", 900, "MB/s")], null, 10)],
            [])).ToList();

    private sealed class RecordingTaskService : IScheduledTaskService
    {
        public List<string> Calls { get; } = [];

        public bool CanSchedule => true;

        public string? UnavailableReason => null;

        public string? ExecutablePath => @"C:\Program Files\PC Checker\PcChecker.exe";

        public Task<ScheduledTaskStatus> GetStatusAsync(ScheduledJobKind job, CancellationToken cancellationToken) =>
            Task.FromResult(ScheduledTaskStatus.Missing(job.ToString()));

        public Task<OperationResult> CreateOrUpdateAsync(JobSchedule schedule, CancellationToken cancellationToken)
        {
            Calls.Add("create " + schedule.Job);
            return Task.FromResult(OperationResult.Ok());
        }

        public Task<OperationResult> DeleteAsync(ScheduledJobKind job, CancellationToken cancellationToken)
        {
            Calls.Add("delete " + job);
            return Task.FromResult(OperationResult.Ok());
        }

        public Task<ScheduledTaskStatus> GetLogonTaskStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ScheduledTaskStatus.Missing("Start"));

        public Task<OperationResult> SetStartWithWindowsAsync(bool enabled, bool startInTray, CancellationToken cancellationToken)
        {
            Calls.Add("logon " + enabled);
            return Task.FromResult(OperationResult.Ok());
        }
    }

    private sealed class RecordingJournal : IActionJournal
    {
        public List<ActionRecord> Records { get; } = [];

        public string JournalPath => "memory";

        public Task RecordAsync(ActionRecord record)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActionRecord>> ReadAsync(int take = 500) => Task.FromResult<IReadOnlyList<ActionRecord>>(Records);
    }
}
