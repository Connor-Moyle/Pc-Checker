using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Scheduling;

/// <summary>Keeps the last result of every job so Settings → Schedules can show "last run" and the next run can compare.</summary>
public interface IScheduledJobStore
{
    /// <summary>Folder that holds <c>&lt;job&gt;-last.json</c>.</summary>
    string Directory { get; }

    ScheduledJobResult? Load(ScheduledJobKind job);

    IReadOnlyDictionary<ScheduledJobKind, ScheduledJobResult> LoadAll();

    void Save(ScheduledJobResult result);

    /// <summary>Raised after <see cref="Save"/> (on the saving thread).</summary>
    event EventHandler<ScheduledJobResult>? Saved;
}

/// <summary>Stores results as JSON in <c>%LOCALAPPDATA%\PcChecker\schedules</c>. Unreadable files count as "never ran".</summary>
public sealed class JsonScheduledJobStore : IScheduledJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger _logger;
    private readonly object _gate = new();

    public JsonScheduledJobStore(ILogger<JsonScheduledJobStore>? logger = null, string? directory = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        Directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory;
    }

    public string Directory { get; }

    public event EventHandler<ScheduledJobResult>? Saved;

    public static string DefaultDirectory() => Path.Combine(JsonSettingsService.DefaultDirectory(), "schedules");

    public static string FileName(ScheduledJobKind job) => ScheduledJobIds.Id(job) + "-last.json";

    public ScheduledJobResult? Load(ScheduledJobKind job)
    {
        var path = Path.Combine(Directory, FileName(job));
        lock (_gate)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var result = JsonSerializer.Deserialize<ScheduledJobResult>(File.ReadAllText(path), JsonOptions);
                return result is not null && result.Job == job ? result : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                _logger.LogWarning(ex, "Could not read the last {Job} result from {Path}.", job, path);
                return null;
            }
        }
    }

    public IReadOnlyDictionary<ScheduledJobKind, ScheduledJobResult> LoadAll()
    {
        var all = new Dictionary<ScheduledJobKind, ScheduledJobResult>();
        foreach (var job in ScheduledJobIds.All)
        {
            if (Load(job) is { } result)
            {
                all[job] = result;
            }
        }

        return all;
    }

    public void Save(ScheduledJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var path = Path.Combine(Directory, FileName(result.Job));
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(result, JsonOptions));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not save the {Job} result to {Path}.", result.Job, path);
            }
        }

        Saved?.Invoke(this, result);
    }
}

/// <summary>Demo mode: sample "last run" results for every job, kept in memory only.</summary>
public sealed class DemoScheduledJobStore : IScheduledJobStore
{
    private readonly Dictionary<ScheduledJobKind, ScheduledJobResult> _results;
    private readonly object _gate = new();

    public DemoScheduledJobStore()
    {
        _results = Samples(DateTimeOffset.Now).ToDictionary(r => r.Job);
    }

    public string Directory => Path.Combine(JsonSettingsService.DefaultDirectory(), "demo", "schedules");

    public event EventHandler<ScheduledJobResult>? Saved;

    public ScheduledJobResult? Load(ScheduledJobKind job)
    {
        lock (_gate)
        {
            return _results.GetValueOrDefault(job);
        }
    }

    public IReadOnlyDictionary<ScheduledJobKind, ScheduledJobResult> LoadAll()
    {
        lock (_gate)
        {
            return new Dictionary<ScheduledJobKind, ScheduledJobResult>(_results);
        }
    }

    public void Save(ScheduledJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            _results[result.Job] = result;
        }

        Saved?.Invoke(this, result);
    }

    /// <summary>Last week's runs on the sample gaming desktop.</summary>
    public static IReadOnlyList<ScheduledJobResult> Samples(DateTimeOffset now)
    {
        var lastSunday = now.Date.AddDays(-(((int)now.DayOfWeek + 7) % 7 == 0 ? 7 : (int)now.DayOfWeek));
        DateTimeOffset At(int days, int hour, int minute) => new DateTimeOffset(lastSunday.AddDays(days).AddHours(hour).AddMinutes(minute), now.Offset);
        var health = At(0, 10, 0);
        var cleanup = At(0, 10, 30);
        var drivers = At(-4, 18, 0);
        var report = At(0, 11, 0);
        var reportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PC Checker Reports",
            "PC Checker report - GAMING-RIG - " + report.ToString("yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture) + ".html");
        return
        [
            new ScheduledJobResult(ScheduledJobKind.Health, health, health.AddSeconds(41), true, false, "Health score 86 (no change)",
                "2 items need a look, none of them new.", ["WD Blue 4 TB: 8 reallocated sectors", "Unknown device without a driver"], "schedule")
            {
                HealthScore = 86,
            },
            new ScheduledJobResult(ScheduledJobKind.Cleanup, cleanup, cleanup.AddSeconds(23), true, false, "Cleanup freed 2.31 GB",
                "Removed 4,812 files and freed 2.31 GB. 11 Safe categories checked.", ["37 files were in use and left alone"], "schedule")
            {
                FreedBytes = 2_480_000_000,
            },
            new ScheduledJobResult(ScheduledJobKind.Drivers, drivers, drivers.AddSeconds(58), true, true, "2 driver updates available",
                "Open Drivers to review and install them. Nothing was installed automatically.",
                ["Realtek Semiconductor Corp. - Audio - 6.0.9600.1", "Intel - Net - 2.1.4.3"], "schedule")
            {
                UpdatesFound = 2,
            },
            new ScheduledJobResult(ScheduledJobKind.Report, report, report.AddSeconds(76), true, false, "Report saved",
                "Saved to " + reportPath + ". Removed 1 older report.", [], "schedule")
            {
                OutputPath = reportPath,
            },
        ];
    }
}
