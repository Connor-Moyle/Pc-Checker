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
    [InlineData("PcChecker.App.ViewModels.DashboardViewModel", LogCategories.UI)]
    [InlineData("PcChecker.Claude.ClaudeAssistantService", LogCategories.Assistant)]
    [InlineData("PcChecker.App.App", LogCategories.App)]
    [InlineData("PcChecker.App.Services.DialogService", LogCategories.App)]
    [InlineData(null, LogCategories.App)]
    public void Category_is_derived_from_namespace(string? source, string expected)
    {
        Assert.Equal(expected, LogCategories.FromSourceContext(source));
    }

    [Fact]
    public void Category_override_wins_over_minimum()
    {
        var settings = new LoggingSettings { MinimumLevel = LogLevelSetting.Warning };
        settings.CategoryLevels["Sensors"] = LogLevelSetting.Debug;
        Assert.Equal(LogLevelSetting.Debug, SerilogLogService.EffectiveLevel(settings, "Sensors"));
        Assert.Equal(LogLevelSetting.Warning, SerilogLogService.EffectiveLevel(settings, "Health"));
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
    }

    [Fact]
    public async Task File_sink_writes_and_export_zips()
    {
        using var svc = new SerilogLogService(new LoggingSettings { FileEnabled = true, Directory = _dir, MinimumLevel = LogLevelSetting.Information });
        svc.CreateLogger<LoggingTests>().LogInformation("to file");
        var zip = await svc.ExportZipAsync(Path.Combine(_dir, "export", "logs.zip"), CancellationToken.None);
        Assert.True(File.Exists(zip));
        Assert.NotEmpty(Directory.GetFiles(_dir, "*.log"));
    }

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
    }

    [Fact]
    public async Task Action_journal_skips_damaged_lines()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "actions.jsonl");
        await File.WriteAllTextAsync(path, "garbage\n");
        var journal = new JsonActionJournal(path);
        await journal.RecordAsync(ActionRecord.Create("Drivers", "Install", "x", "Succeeded"));
        var read = await journal.ReadAsync();
        Assert.Single(read);
    }
}
