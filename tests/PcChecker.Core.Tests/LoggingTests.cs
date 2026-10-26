using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Logging;
using Serilog.Events;
using Xunit;

namespace PcChecker.Core.Tests;

public class LoggingTests : IDisposable
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

    // ---------------------------------------------------------------- levels and categories

    [Theory]
    [InlineData(LogLevelSetting.Verbose, LogEventLevel.Verbose)]
    [InlineData(LogLevelSetting.Debug, LogEventLevel.Debug)]
    [InlineData(LogLevelSetting.Information, LogEventLevel.Information)]
    [InlineData(LogLevelSetting.Warning, LogEventLevel.Warning)]
    [InlineData(LogLevelSetting.Error, LogEventLevel.Error)]
    [InlineData(LogLevelSetting.Fatal, LogEventLevel.Fatal)]
    public void Level_mapping_round_trips(LogLevelSetting setting, LogEventLevel serilog)
    {
        Assert.Equal(serilog, SerilogLogService.ToSerilogLevel(setting));
        Assert.Equal(setting, SerilogLogService.FromSerilogLevel(serilog));
    }

    [Theory]
    [InlineData("PcChecker.Core.Sensors.LibreHardwareSensorService", LogCategories.Sensors)]
    [InlineData("PcChecker.Core.WindowsFeatures.WindowsFeatureService", LogCategories.Features)]
    [InlineData("PcChecker.Core.Overclocking.WindowsOverclockService", LogCategories.Overclock)]
    [InlineData("PcChecker.Core.Settings.JsonSettingsService", LogCategories.App)]
    [InlineData("PcChecker.Core.Logging.JsonActionJournal", LogCategories.App)]
    [InlineData("PcChecker.Core.Fans.LibreHardwareFanControlService", LogCategories.Fans)]
    [InlineData("PcChecker.Core.Storage.WindowsStorageToolsService", LogCategories.Storage)]
    [InlineData("PcChecker.Core.Latency.WindowsLatencyService", LogCategories.Latency)]
    [InlineData("PcChecker.App.ViewModels.DashboardViewModel", LogCategories.UI)]
    [InlineData("PcChecker.Claude.ClaudeAssistantService", LogCategories.Assistant)]
    [InlineData("PcChecker.App.App", LogCategories.App)]
    [InlineData("PcChecker.App.Services.DialogService", LogCategories.App)]
    [InlineData("Sensors", LogCategories.Sensors)]
    [InlineData("Something.Unrelated", LogCategories.App)]
    [InlineData(null, LogCategories.App)]
    public void Category_is_derived_from_namespace(string? source, string expected)
    {
        Assert.Equal(expected, LogCategories.FromSourceContext(source));
        Assert.Equal(expected, LogCategoryResolver.FromSourceContext(source));
    }

    [Theory]
    [InlineData(LogCategories.Sensors, "PcChecker.Core.Sensors")]
    [InlineData(LogCategories.Features, "PcChecker.Core.WindowsFeatures")]
    [InlineData(LogCategories.Overclock, "PcChecker.Core.Overclocking")]
    [InlineData(LogCategories.Assistant, "PcChecker.Claude")]
    [InlineData(LogCategories.UI, "PcChecker.App.ViewModels")]
    [InlineData("PcChecker.Core.Health", "PcChecker.Core.Health")]
    public void Category_maps_to_namespace_prefix(string category, string expected)
    {
        Assert.Equal(expected, LogCategoryResolver.ToNamespacePrefix(category));
    }

    [Fact]
    public void Every_category_has_a_description_and_the_list_has_no_duplicates()
    {
        Assert.Equal(LogCategories.All.Count, LogCategories.All.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var category in LogCategories.All)
        {
            Assert.NotEqual("Other log lines", LogCategoryResolver.Describe(category));
        }
    }

    [Fact]
    public void Category_override_wins_over_minimum()
    {
        var settings = new LoggingSettings { MinimumLevel = LogLevelSetting.Warning };
        settings.CategoryLevels["Sensors"] = LogLevelSetting.Debug;
        settings.CategoryLevels["PcChecker.Core.Health"] = LogLevelSetting.Verbose;
        Assert.Equal(LogLevelSetting.Debug, SerilogLogService.EffectiveLevel(settings, "Sensors"));
        Assert.Equal(LogLevelSetting.Verbose, SerilogLogService.EffectiveLevel(settings, "Health"));
        Assert.Equal(LogLevelSetting.Warning, SerilogLogService.EffectiveLevel(settings, "Drivers"));
        Assert.Equal(LogLevelSetting.Verbose, SerilogLogService.PipelineFloor(settings));
    }

    // ---------------------------------------------------------------- in-memory sink

    [Fact]
    public void In_memory_sink_keeps_order_respects_capacity_and_raises_events()
    {
        var sink = new InMemoryLogSink(capacity: 200);
        int raised = 0;
        sink.EntryLogged += (_, _) => raised++;
        for (int i = 0; i < 250; i++)
        {
            sink.Push(new LogEntry(DateTimeOffset.Now, LogLevelSetting.Information, "App", "entry " + i, null));
        }

        var snapshot = sink.Snapshot();
        Assert.Equal(200, snapshot.Count);
        Assert.Equal("entry 50", snapshot[0].Message);
        Assert.Equal("entry 249", snapshot[^1].Message);
        Assert.Equal(250, raised);

        sink.Capacity = 100;
        Assert.Equal(100, sink.Count);
        Assert.Equal("entry 150", sink.Snapshot()[0].Message);

        sink.Capacity = 1;
        Assert.Equal(InMemoryLogSink.MinimumCapacity, sink.Capacity);

        sink.Clear();
        Assert.Empty(sink.Snapshot());
    }

    [Fact]
    public void Log_service_buffers_entries_and_raises_events()
    {
        using var svc = new SerilogLogService(new LoggingSettings { FileEnabled = false, MinimumLevel = LogLevelSetting.Information, InAppBufferSize = 100 });
        var received = new List<LogEntry>();
        svc.EntryLogged += (_, e) => received.Add(e);
        var logger = svc.CreateLogger<LoggingTests>();
        logger.LogInformation("hello {Name}", "world");
        logger.LogDebug("hidden");

        var snapshot = svc.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("hello \"world\"", snapshot[0].Message);
        Assert.Equal(LogLevelSetting.Information, snapshot[0].Level);
        Assert.Equal(LogCategories.App, snapshot[0].Category);
        Assert.Single(received);
        svc.Clear();
        Assert.Empty(svc.Snapshot());
    }

    [Fact]
    public void Reconfigure_changes_levels_for_existing_loggers()
    {
        using var svc = new SerilogLogService(new LoggingSettings { FileEnabled = false, MinimumLevel = LogLevelSetting.Warning });
        var logger = svc.CreateLogger<LoggingTests>();
        logger.LogInformation("before");
        Assert.Empty(svc.Snapshot());
        svc.Reconfigure(new LoggingSettings { FileEnabled = false, MinimumLevel = LogLevelSetting.Information });
        logger.LogInformation("after");
        Assert.Single(svc.Snapshot());
        Assert.Equal("after", svc.Snapshot()[0].Message);
    }

    [Fact]
    public void Reconfigurable_factory_routes_old_loggers_to_the_new_pipeline()
    {
        var first = new InMemoryLogSink(100);
        var second = new InMemoryLogSink(100);
        var firstLogger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(first).CreateLogger();
        var secondLogger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(second).CreateLogger();
        using var factory = new ReconfigurableLoggerFactory(firstLogger);
        var logger = factory.CreateLogger("PcChecker.Core.Health.Test");

        logger.LogInformation("one");
        factory.Swap(secondLogger);
        logger.LogInformation("two");

        Assert.Single(first.Snapshot());
        Assert.Equal("one", first.Snapshot()[0].Message);
        Assert.Single(second.Snapshot());
        Assert.Equal("two", second.Snapshot()[0].Message);
        Assert.Equal(1, factory.Version);
    }

    [Fact]
    public void Buffer_respects_capacity()
    {
        using var svc = new SerilogLogService(new LoggingSettings { FileEnabled = false, InAppBufferSize = 100 });
        var logger = svc.CreateLogger("Health");
        for (int i = 0; i < 150; i++)
        {
            logger.LogWarning("entry {Index}", i);
        }

        Assert.Equal(100, svc.Snapshot().Count);
        Assert.All(svc.Snapshot(), e => Assert.Equal(LogCategories.Health, e.Category));
    }

    [Fact]
    public void Category_overrides_let_one_area_log_below_the_global_minimum()
    {
        var settings = new LoggingSettings { FileEnabled = false, MinimumLevel = LogLevelSetting.Warning };
        settings.CategoryLevels[LogCategories.Sensors] = LogLevelSetting.Debug;
        using var svc = new SerilogLogService(settings);
        svc.CreateLogger("PcChecker.Core.Sensors.Poller").LogDebug("sensor debug");
        svc.CreateLogger("PcChecker.Core.Health.Analyzer").LogDebug("health debug");
        svc.CreateLogger("PcChecker.Core.Health.Analyzer").LogWarning("health warning");

        var messages = svc.Snapshot().Select(e => e.Message).ToList();
        Assert.Equal(["sensor debug", "health warning"], messages);
    }

    // ---------------------------------------------------------------- redaction

    [Theory]
    [InlineData("Disk serial S4EWNX0R123456A detected", "Disk serial [redacted] detected")]
    [InlineData("Serial Number: ABCD1234EFGH", "Serial Number: [redacted]")]
    [InlineData("S/N=WD-WCC4N1234567", "S/N=[redacted]")]
    [InlineData("MAC 00:1A:2B:3C:4D:5E online", "MAC [redacted] online")]
    [InlineData("serial 1234567", "serial 1234567")]
    [InlineData("Scan finished in 3 s", "Scan finished in 3 s")]
    [InlineData("", "")]
    public void Serial_numbers_and_macs_are_redacted(string input, string expected)
    {
        Assert.Equal(expected, LogRedaction.RedactSerials(input));
        Assert.Equal(expected != input, LogRedaction.ContainsSensitiveToken(input));
    }

    [Fact]
    public void Pipeline_redacts_when_the_setting_is_on()
    {
        using var svc = new SerilogLogService(new LoggingSettings { FileEnabled = false, RedactSerialNumbers = true });
        svc.CreateLogger("Hardware").LogInformation("Drive {Model} serial {Serial}", "Samsung 990 PRO", "S6Z2NJ0T123456X");
        var entry = Assert.Single(svc.Snapshot());
        Assert.DoesNotContain("S6Z2NJ0T123456X", entry.Message);
        Assert.Contains(LogRedaction.Mask, entry.Message);
        Assert.Contains("Samsung 990 PRO", entry.Message);
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public void Logging_settings_default_template_is_usable_and_round_trips_json()
    {
        var settings = new LoggingSettings();
        Assert.Contains("{Message", settings.OutputTemplate);
        Assert.Contains("{Category}", settings.OutputTemplate);
        settings.CategoryLevels["Drivers"] = LogLevelSetting.Debug;
        settings.Directory = Path.Combine(_dir, "logs");
        settings.JsonFormat = true;

        var options = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var json = JsonSerializer.Serialize(settings, options);
        var back = JsonSerializer.Deserialize<LoggingSettings>(json, options)!;
        Assert.Equal(settings.OutputTemplate, back.OutputTemplate);
        Assert.Equal(LogLevelSetting.Debug, back.CategoryLevels["drivers"]);
        Assert.Equal(settings.Directory, back.Directory);
        Assert.True(back.JsonFormat);
        Assert.Equal(settings.InAppBufferSize, back.InAppBufferSize);
    }

    // ---------------------------------------------------------------- filter

    [Fact]
    public void Filter_matches_level_category_and_text()
    {
        var entries = new[]
        {
            new LogEntry(DateTimeOffset.Now, LogLevelSetting.Debug, "Sensors", "poll ok", null),
            new LogEntry(DateTimeOffset.Now, LogLevelSetting.Warning, "Health", "disk hot", null),
            new LogEntry(DateTimeOffset.Now, LogLevelSetting.Error, "Drivers", "install failed", "System.TimeoutException: catalog"),
        };

        Assert.Equal(3, LogFilter.None.Apply(entries).Count());
        Assert.Equal(2, new LogFilter(LogLevelSetting.Warning).Apply(entries).Count());
        Assert.Single(new LogFilter(Category: "health").Apply(entries));
        Assert.Single(new LogFilter(Search: "CATALOG").Apply(entries));
        Assert.Empty(new LogFilter(LogLevelSetting.Error, "Sensors").Apply(entries));
        Assert.True(LogFilter.None.IsEmpty);
        Assert.False(new LogFilter(Search: "x").IsEmpty);

        var counts = LogFilter.Count(entries);
        Assert.Equal(new LogCounts(3, 1, 1), counts);
    }

    // ---------------------------------------------------------------- files and export

    [Fact]
    public async Task File_sink_writes_and_export_zips_everything()
    {
        var logDir = Path.Combine(_dir, "logs");
        var settingsPath = Path.Combine(_dir, "settings.json");
        var journalPath = Path.Combine(_dir, "actions.jsonl");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(settingsPath, """{ "General": { "Theme": "Dark" }, "Claude": { "Model": "x", "ProtectedApiKey": "SECRET-VALUE" } }""");
        var journal = new JsonActionJournal(journalPath);
        await journal.RecordAsync(ActionRecord.Create("Cleanup", "Delete", "user.temp", "Succeeded"));

        using var svc = new SerilogLogService(new LoggingSettings { FileEnabled = true, Directory = logDir, MinimumLevel = LogLevelSetting.Information });
        svc.Export.SettingsFilePath = settingsPath;
        svc.Export.JournalPath = journalPath;
        svc.Export.AppVersion = "1.2.3";
        svc.CreateLogger<LoggingTests>().LogInformation("to file");
        Assert.NotEmpty(Directory.GetFiles(logDir, "*.log"));
        Assert.NotNull(svc.CurrentLogFile());

        var zip = await svc.ExportZipAsync(Path.Combine(_dir, "export", "logs.zip"), CancellationToken.None);
        Assert.True(File.Exists(zip));
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains(names, n => n.StartsWith(DiagnosticsExport.LogsFolder + "/", StringComparison.Ordinal) && n.EndsWith(".log", StringComparison.Ordinal));
        Assert.Contains(DiagnosticsExport.JournalEntry, names);
        Assert.Contains(DiagnosticsExport.SettingsEntry, names);
        Assert.Contains(DiagnosticsExport.BufferEntry, names);
        Assert.Contains(DiagnosticsExport.SummaryEntry, names);

        using var settingsReader = new StreamReader(archive.GetEntry(DiagnosticsExport.SettingsEntry)!.Open());
        var exportedSettings = await settingsReader.ReadToEndAsync();
        Assert.DoesNotContain("SECRET-VALUE", exportedSettings);
        Assert.Contains("\"Dark\"", exportedSettings);

        using var summaryReader = new StreamReader(archive.GetEntry(DiagnosticsExport.SummaryEntry)!.Open());
        var summary = await summaryReader.ReadToEndAsync();
        Assert.Contains("1.2.3", summary);

        using var bufferReader = new StreamReader(archive.GetEntry(DiagnosticsExport.BufferEntry)!.Open());
        Assert.Contains("to file", await bufferReader.ReadToEndAsync());
    }

    [Fact]
    public void Strip_secrets_blanks_keys_anywhere_and_survives_bad_json()
    {
        var json = """{ "Claude": { "ProtectedApiKey": "abc", "Model": "m" }, "Nested": [ { "apiKey": "k" } ], "Keep": 1 }""";
        var stripped = DiagnosticsExport.StripSecrets(json);
        using var doc = JsonDocument.Parse(stripped);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Claude").GetProperty("ProtectedApiKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Nested")[0].GetProperty("apiKey").ValueKind);
        Assert.Equal("m", doc.RootElement.GetProperty("Claude").GetProperty("Model").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("Keep").GetInt32());

        Assert.Contains("could not be parsed", DiagnosticsExport.StripSecrets("{ not json"));
        Assert.Equal("{}", DiagnosticsExport.StripSecrets(null));
    }

    [Fact]
    public void System_summary_lists_version_and_logging_settings()
    {
        var settings = new LoggingSettings { MinimumLevel = LogLevelSetting.Debug };
        settings.CategoryLevels["Sensors"] = LogLevelSetting.Verbose;
        var summary = DiagnosticsExport.BuildSystemSummary("9.9.9", settings, DateTimeOffset.Now, "Mode: Demo");
        Assert.Contains("App version: 9.9.9", summary);
        Assert.Contains("Minimum level: Debug", summary);
        Assert.Contains("Sensors: Verbose", summary);
        Assert.Contains("Mode: Demo", summary);
    }

    // ---------------------------------------------------------------- action journal

    [Fact]
    public async Task Action_journal_round_trips_newest_first()
    {
        var journal = new JsonActionJournal(Path.Combine(_dir, "actions.jsonl"));
        await journal.RecordAsync(ActionRecord.Create("Optimization", "Apply", "power.balanced", "Succeeded", reversible: true, undoHint: "Revert"));
        await journal.RecordAsync(ActionRecord.Create("Cleanup", "Delete", "user.temp", "Succeeded"));
        var read = await journal.ReadAsync();
        Assert.Equal(2, read.Count);
        Assert.Equal("Cleanup", read[0].Module);
        Assert.Equal("Optimization", read[1].Module);
        Assert.True(read[1].Reversible);
        Assert.Equal("Revert", read[1].UndoHint);
        Assert.Single(await journal.ReadAsync(1));
    }

    [Fact]
    public async Task Action_journal_skips_damaged_lines()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "actions.jsonl");
        await File.WriteAllTextAsync(path, "garbage\n{\"half\":\n");
        var journal = new JsonActionJournal(path);
        await journal.RecordAsync(ActionRecord.Create("Drivers", "Install", "x", "Succeeded"));
        var read = await journal.ReadAsync();
        Assert.Single(read);
        Assert.Equal(2, journal.LastSkippedLines);
    }

    [Fact]
    public async Task Action_journal_rotates_when_the_size_limit_is_reached()
    {
        var path = Path.Combine(_dir, "actions.jsonl");
        var journal = new JsonActionJournal(path) { RotationLimitBytes = 600 };
        for (int i = 0; i < 6; i++)
        {
            await journal.RecordAsync(ActionRecord.Create("Cleanup", "Delete", "category-" + i, "Succeeded", before: new string('x', 60)));
        }

        var archives = journal.ArchivedFiles();
        Assert.NotEmpty(archives);
        Assert.All(archives, a => Assert.StartsWith("actions-", Path.GetFileName(a)));
        var live = await journal.ReadAsync();
        Assert.NotEmpty(live);
        Assert.True(new FileInfo(path).Length < 600);
    }

    [Fact]
    public void Rotation_rules_are_pure()
    {
        Assert.False(JournalRotation.ShouldRotate(0, 100));
        Assert.False(JournalRotation.ShouldRotate(1000, 100, limitBytes: 2000));
        Assert.True(JournalRotation.ShouldRotate(1950, 100, limitBytes: 2000));
        Assert.False(JournalRotation.ShouldRotate(1950, 100, limitBytes: 0));
        var rotated = JournalRotation.RotatedPath(Path.Combine("c", "actions.jsonl"), new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero));
        Assert.Equal("actions-20240506-070809.jsonl", Path.GetFileName(rotated));
        Assert.Equal("actions-*.jsonl", JournalRotation.ArchivePattern(Path.Combine("c", "actions.jsonl")));
        var second = JournalRotation.RotatedPath(Path.Combine("c", "actions.jsonl"), new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero), sequence: 1);
        Assert.Equal("actions-20240506-070809-2.jsonl", Path.GetFileName(second));
    }

    // ---------------------------------------------------------------- demo

    [Fact]
    public void Demo_samples_cover_levels_and_journal_records()
    {
        using var svc = new SerilogLogService(new LoggingSettings { FileEnabled = false, MinimumLevel = LogLevelSetting.Verbose });
        DemoLogSamples.WriteSampleLines(svc);
        var levels = svc.Snapshot().Select(e => e.Level).Distinct().ToList();
        Assert.Contains(LogLevelSetting.Warning, levels);
        Assert.Contains(LogLevelSetting.Error, levels);
        Assert.Contains(svc.Snapshot(), e => e.Exception is not null);
        Assert.Contains(svc.Snapshot(), e => e.Category == LogCategories.Sensors);

        var records = DemoLogSamples.SampleRecords(DateTimeOffset.Now);
        Assert.True(records.Count >= 6);
        Assert.Contains(records, r => r.Reversible && r.RestorePointSequence is not null);
        Assert.Contains(records, r => r.Outcome.StartsWith("Failed", StringComparison.Ordinal));
    }
}
