using System.Globalization;
using PcChecker.Core.Settings;

namespace PcChecker.Core.SetupBackup;

/// <summary>
/// The parts of a setup file, in the order they are shown, and the <see cref="AppSettings"/> properties each one
/// covers. Every property of <see cref="AppSettings"/> belongs to exactly one section (the tests check this, so a new
/// settings section cannot be forgotten). Everything is stored in settings.json except the benchmark history, which
/// has its own file.
/// </summary>
public static class SetupSectionCatalog
{
    public static IReadOnlyList<SetupSectionInfo> All { get; } =
    [
        new(SetupSectionIds.App, "App settings",
            "Theme, language, alerts and limits, restore points, updates, logging, cleanup, driver sources, network and latency tests, history recording.",
            [
                nameof(AppSettings.General), nameof(AppSettings.Updates), nameof(AppSettings.RestorePoints),
                nameof(AppSettings.Monitoring), nameof(AppSettings.Logging), nameof(AppSettings.Claude),
                nameof(AppSettings.Cleanup), nameof(AppSettings.Network), nameof(AppSettings.Latency),
                nameof(AppSettings.Startup), nameof(AppSettings.History), nameof(AppSettings.DriverSources),
                nameof(AppSettings.Processes),
            ]),
        new(SetupSectionIds.Fans, "Fans", "Fan profiles and curves, the lowest speed and start-up options.", [nameof(AppSettings.Fans)]),
        new(SetupSectionIds.Lighting, "Lighting", "Lighting profiles and the OpenRGB connection.", [nameof(AppSettings.Lighting)]),
        new(SetupSectionIds.Overlay, "Overlay", "Overlay layout, metrics, colours, hotkey and saved presets.", [nameof(AppSettings.Overlay)]),
        new(SetupSectionIds.Games, "Game profiles", "What changes while each game runs, and automatic switching.", [nameof(AppSettings.Gaming)]),
        new(SetupSectionIds.Displays, "Display profiles", "Saved display arrangements.", [nameof(AppSettings.Displays)]),
        new(SetupSectionIds.Memory, "Memory", "Low-memory warning. The memory profiles themselves are built in.", [nameof(AppSettings.Memory)]),
        new(SetupSectionIds.Input, "Keyboard and mouse", "Saved keyboard and mouse profiles.", [nameof(AppSettings.Input)]),
        new(SetupSectionIds.Overclock, "Overclock limits and profiles",
            "Safety limits and saved profiles. Profiles are still checked against your limits and the card's own range before they are applied.",
            [nameof(AppSettings.Overclock)], SelectedByDefault: false),
        new(SetupSectionIds.Schedules, "Schedules and tray",
            "Scheduled jobs, tray options and Start with Windows. The matching Windows scheduled tasks are created or removed.",
            [nameof(AppSettings.Schedules), nameof(AppSettings.Tray)]),
        new(SetupSectionIds.Benchmarks, "Benchmark history", "Your saved benchmark runs. Importing adds runs you do not have yet.",
            [], CanReset: false),
    ];

    /// <summary>The section with <paramref name="id"/>, or null when this build does not know it.</summary>
    public static SetupSectionInfo? Find(string? id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The section that owns the <see cref="AppSettings"/> property <paramref name="property"/>, or null.</summary>
    public static SetupSectionInfo? ForProperty(string property) =>
        All.FirstOrDefault(s => s.SettingsProperties.Contains(property, StringComparer.OrdinalIgnoreCase));

    /// <summary>Sections that live in settings.json.</summary>
    public static IEnumerable<SetupSectionInfo> SettingsSections => All.Where(s => !s.IsBenchmarkHistory);
}

/// <summary>
/// Values that belong to this PC or this Windows account rather than to the user's setup. They are never written to a
/// setup file, and an import always keeps this PC's own value (even if a hand-edited file contains one).
/// </summary>
public static class SetupMachineFields
{
    /// <summary>The Claude API key: protected with DPAPI for this Windows account only and never exported.</summary>
    public const string ClaudeApiKey = "Claude.ProtectedApiKey";

    /// <summary>"Property.Field" paths, relative to <see cref="AppSettings"/>.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ClaudeApiKey,
        "General.WindowWidth", "General.WindowHeight", "General.WindowMaximized", "General.WelcomeSeen", "General.RecentSearchItems",
        "Updates.LastCheckedUtc",
        "Logging.Directory",
        "History.PreviousRetentionDays", "History.RetentionLoweredAt",
        "Fans.ActiveProfileName",
        "Lighting.ActiveProfileName",
        "Overclock.ConsentAccepted", "Overclock.ConsentAcceptedUtc", "Overclock.ActiveProfileName", "Overclock.LastSessionClean",

        // Commands typed into "Run new task" can hold passwords, tokens or private paths typed as options.
        "Processes.RecentRunCommands",
    ];

    /// <summary>The machine-only field names inside the settings property <paramref name="property"/>.</summary>
    public static IEnumerable<string> FieldsOf(string property) =>
        All.Where(p => p.StartsWith(property + ".", StringComparison.OrdinalIgnoreCase)).Select(p => p[(property.Length + 1)..]);
}

/// <summary>The fixed parts of the file format.</summary>
public static class SetupFileFormat
{
    /// <summary>Value of the "Format" field that marks a PC Checker setup file.</summary>
    public const string FormatName = "PcChecker.Setup";

    /// <summary>Layout this build writes. Version 0 is a plain settings.json, which is read and converted.</summary>
    public const int CurrentSchemaVersion = 1;

    public const string FileExtension = ".pcchecker";

    /// <summary>Win32 file-picker filter.</summary>
    public const string FileFilter = "PC Checker setup (*.pcchecker)|*.pcchecker|Settings file (*.json)|*.json";

    /// <summary>Largest file read; a setup with a full benchmark history is far smaller.</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>Name of an automatic backup made at <paramref name="time"/>: setup-20261027-143005.pcchecker.</summary>
    public static string AutomaticBackupName(DateTime time) => "setup-" + time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + FileExtension;

    /// <summary>Suggested name for an export made at <paramref name="time"/>.</summary>
    public static string ExportName(DateTime time) => "pc-checker-setup-" + time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + FileExtension;
}
