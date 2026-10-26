namespace PcChecker.Core.Logging;

/// <summary>Well-known log category names (one per module). The logger derives them from the Core folder namespace.</summary>
public static class LogCategories
{
    public const string Hardware = "Hardware";
    public const string Sensors = "Sensors";
    public const string Health = "Health";
    public const string Drivers = "Drivers";
    public const string Software = "Software";
    public const string Performance = "Performance";
    public const string Optimization = "Optimization";
    public const string Cleanup = "Cleanup";
    public const string Features = "Features";
    public const string Restore = "Restore";
    public const string Overclock = "Overclock";
    public const string Power = "Power";
    public const string Updates = "Updates";
    public const string Assistant = "Assistant";
    public const string Overlay = "Overlay";
    public const string Fans = "Fans";
    public const string Lighting = "Lighting";
    public const string Storage = "Storage";
    public const string Memory = "Memory";
    public const string Displays = "Displays";
    public const string Audio = "Audio";
    public const string Input = "Input";
    public const string Latency = "Latency";
    public const string Startup = "Startup";
    public const string UI = "UI";
    public const string App = "App";

    public static IReadOnlyList<string> All { get; } =
    [
        Hardware, Sensors, Health, Drivers, Software, Performance, Optimization, Cleanup, Features,
        Restore, Overclock, Power, Updates, Assistant, Overlay, Fans, Lighting, Storage, Memory, Displays,
        Audio, Input, Latency, Startup, UI, App,
    ];

    /// <summary>
    /// Maps a .NET logger category (usually a full type name such as <c>PcChecker.Core.Sensors.LibreHardwareSensorService</c>)
    /// to one of the well-known categories, falling back to <see cref="App"/>.
    /// </summary>
    public static string FromSourceContext(string? sourceContext) => LogCategoryResolver.FromSourceContext(sourceContext);
}
