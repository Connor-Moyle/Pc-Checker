namespace PcChecker.Core.Logging;

/// <summary>Log level names used in settings; map 1:1 to Serilog levels.</summary>
public enum LogLevelSetting
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Fatal,
}

public enum LogRollingInterval
{
    Hour,
    Day,
    Infinite,
}

/// <summary>User-configurable logging behaviour.</summary>
public sealed class LoggingSettings
{
    public const string DefaultOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{Category}] {Message:lj}{NewLine}{Exception}";

    public LogLevelSetting MinimumLevel { get; set; } = LogLevelSetting.Information;
    private Dictionary<string, LogLevelSetting> _categoryLevels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-category overrides keyed by category name (case-insensitive, also after loading from JSON).</summary>
    public Dictionary<string, LogLevelSetting> CategoryLevels
    {
        get => _categoryLevels;
        set => _categoryLevels = value is null ? new(StringComparer.OrdinalIgnoreCase) : new(value, StringComparer.OrdinalIgnoreCase);
    }

    public bool FileEnabled { get; set; } = true;
    public string Directory { get; set; } = DefaultDirectory();
    public LogRollingInterval RollingInterval { get; set; } = LogRollingInterval.Day;
    public int FileSizeLimitMb { get; set; } = 10;
    public int RetainedFileCount { get; set; } = 14;
    public bool JsonFormat { get; set; }
    public string OutputTemplate { get; set; } = DefaultOutputTemplate;
    public int InAppBufferSize { get; set; } = 2000;
    public bool RedactSerialNumbers { get; set; }
    public bool LogSensorSamples { get; set; }

    public static string DefaultDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(local, "PcChecker", "logs");
    }
}
