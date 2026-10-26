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
    public const string UI = "UI";
    public const string App = "App";

    public static IReadOnlyList<string> All { get; } =
    [
        Hardware, Sensors, Health, Drivers, Software, Performance, Optimization, Cleanup, Features,
        Restore, Overclock, Power, Updates, Assistant, Overlay, UI, App,
    ];

    /// <summary>
    /// Maps a .NET logger category (usually a full type name such as <c>PcChecker.Core.Sensors.LibreHardwareSensorService</c>)
    /// to one of the well-known categories, falling back to <see cref="App"/>.
    /// </summary>
    public static string FromSourceContext(string? sourceContext)
    {
        if (string.IsNullOrWhiteSpace(sourceContext))
        {
            return App;
        }

        var parts = sourceContext.Split('.', StringSplitOptions.RemoveEmptyEntries);
        // PcChecker.Core.<Folder>.<Type> or PcChecker.App.<Folder>.<Type> or PcChecker.Claude.<Type>
        if (parts.Length >= 3 && parts[0].Equals("PcChecker", StringComparison.OrdinalIgnoreCase))
        {
            if (parts[1].Equals("Claude", StringComparison.OrdinalIgnoreCase))
            {
                return Assistant;
            }

            var folder = parts[2];
            var mapped = folder switch
            {
                "WindowsFeatures" => Features,
                "Overclocking" => Overclock,
                "Reports" => App,
                "Settings" => App,
                "Logging" => App,
                "Common" => App,
                "Services" => App,
                "ViewModels" => UI,
                "Views" => UI,
                "Controls" => UI,
                _ => folder,
            };
            return All.FirstOrDefault(c => c.Equals(mapped, StringComparison.OrdinalIgnoreCase)) ?? (parts[1].Equals("App", StringComparison.OrdinalIgnoreCase) ? UI : App);
        }

        return All.FirstOrDefault(c => sourceContext.Contains(c, StringComparison.OrdinalIgnoreCase)) ?? App;
    }
}
