using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcChecker.Core.Settings;

/// <summary>
/// Stores settings as indented JSON in <c>%LOCALAPPDATA%\PcChecker\settings.json</c>.
/// Missing or unreadable files fall back to defaults; a corrupt file is kept as a <c>.bak</c> copy.
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsService()
        : this(DefaultDirectory())
    {
    }

    /// <summary>Creates a service that stores settings in an explicit directory (used by tests and portable mode).</summary>
    public JsonSettingsService(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        SettingsDirectory = directory;
        SettingsFilePath = Path.Combine(directory, FileName);
    }

    public AppSettings Current { get; private set; } = AppSettings.CreateDefault();
    public string SettingsDirectory { get; }
    public string SettingsFilePath { get; }

    public event EventHandler<AppSettings>? Changed;

    /// <summary>Default location: %LOCALAPPDATA%\PcChecker.</summary>
    public static string DefaultDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(local, "PcChecker");
    }

    public async Task LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Current = await ReadOrDefaultAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, Current);
    }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var temp = SettingsFilePath + ".tmp";
            await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
            File.Move(temp, SettingsFilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, Current);
    }

    private async Task<AppSettings> ReadOrDefaultAsync()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return AppSettings.CreateDefault();
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(SettingsFilePath).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return AppSettings.CreateDefault();
        }
        catch (UnauthorizedAccessException)
        {
            return AppSettings.CreateDefault();
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return AppSettings.CreateDefault();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return loaded is null ? AppSettings.CreateDefault() : Repair(loaded);
        }
        catch (JsonException)
        {
            BackupCorruptFile();
            return AppSettings.CreateDefault();
        }
    }

    private void BackupCorruptFile()
    {
        try
        {
            var backup = Path.Combine(SettingsDirectory, $"settings.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak");
            File.Copy(SettingsFilePath, backup, overwrite: true);
        }
        catch
        {
            // The backup is a courtesy; never fail loading because of it.
        }
    }

    /// <summary>
    /// Ensures nested objects are never null even if the file set them to null. Also used for settings read from a
    /// setup backup file.
    /// </summary>
    public static AppSettings Repair(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.General ??= new GeneralSettings();
        s.General.RecentSearchItems ??= [];
        s.Updates ??= new UpdateSettings();
        s.RestorePoints ??= new RestorePointSettings();
        s.Monitoring ??= new MonitoringSettings();
        s.Overlay ??= new OverlaySettings();
        s.Overlay.Metrics ??= OverlayMetricIds.CreateDefaultMetrics();
        s.Overlay.Presets ??= [];
        s.Logging ??= new Logging.LoggingSettings();
        // The deserializer creates a case-sensitive dictionary; category names should match regardless of case.
        s.Logging.CategoryLevels = new Dictionary<string, Logging.LogLevelSetting>(
            s.Logging.CategoryLevels ?? new Dictionary<string, Logging.LogLevelSetting>(), StringComparer.OrdinalIgnoreCase);
        s.Claude ??= new ClaudeSettings();
        s.Cleanup ??= new CleanupSettings();
        s.Cleanup.EnabledCategoryIds ??= [];
        s.Overclock ??= new OverclockSettings();
        s.Overclock.Profiles ??= [];
        s.Fans ??= new Fans.FanSettings();
        s.Fans.Profiles ??= [];
        s.Lighting ??= new Lighting.LightingSettings();
        s.Lighting.Profiles ??= [];
        s.Displays ??= new Displays.DisplaySettings();
        s.Displays.Profiles ??= [];
        s.Input ??= new Input.InputSettings();
        s.Input.Profiles ??= [];
        s.Memory ??= new MemorySettings();
        s.Latency ??= new LatencySettings();
        s.Latency.PingTargets ??= ["gateway", "1.1.1.1", "8.8.8.8"];
        s.Startup ??= new StartupSettings();
        s.History ??= new History.HistorySettings();
        s.Network ??= new Network.NetworkSettings();
        s.Network.PacketLossTargets ??= ["gateway", "1.1.1.1", "8.8.8.8"];
        s.Gaming ??= new Gaming.GamingSettings();
        s.Gaming.Profiles ??= [];
        Scheduling.ScheduleRules.EnsureSettings(s);
        s.DriverSources ??= new Drivers.Sources.DriverSourceSettings();
        s.Processes ??= new Processes.ProcessesSettings();
        s.Processes.RecentRunCommands ??= [];
        return s;
    }
}
