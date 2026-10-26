namespace PcChecker.Core.Logging;

/// <summary>
/// Pure mapping between .NET logger names (type full names), the short category names in <see cref="LogCategories"/>
/// and the namespace prefixes Serilog overrides use. No Windows dependency.
/// </summary>
public static class LogCategoryResolver
{
    private static readonly Dictionary<string, string> FolderOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WindowsFeatures"] = LogCategories.Features,
        ["Overclocking"] = LogCategories.Overclock,
        ["Reports"] = LogCategories.App,
        ["Settings"] = LogCategories.App,
        ["Logging"] = LogCategories.App,
        ["Common"] = LogCategories.App,
        ["Services"] = LogCategories.App,
        ["ViewModels"] = LogCategories.UI,
        ["Views"] = LogCategories.UI,
        ["Controls"] = LogCategories.UI,
        ["Converters"] = LogCategories.UI,
        ["Helpers"] = LogCategories.App,
    };

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        [LogCategories.Hardware] = "Hardware inventory scans",
        [LogCategories.Sensors] = "Temperatures, loads and sensor polling",
        [LogCategories.Health] = "Health checks, SMART and event log analysis",
        [LogCategories.Drivers] = "Driver scans, updates and installs",
        [LogCategories.Software] = "Installed programs and uninstalls",
        [LogCategories.Performance] = "Slowdown analysis",
        [LogCategories.Optimization] = "Optimization actions applied or reverted",
        [LogCategories.Cleanup] = "Temporary file scanning and cleaning",
        [LogCategories.Features] = "Windows optional features and inbox apps",
        [LogCategories.Restore] = "System Restore points and change guarding",
        [LogCategories.Overclock] = "Overclocking, stress tests and the safety watchdog",
        [LogCategories.Power] = "Power usage and power plans",
        [LogCategories.Updates] = "App update checks and installs",
        [LogCategories.Assistant] = "The Claude assistant feature",
        [LogCategories.Overlay] = "The in-game overlay",
        [LogCategories.Fans] = "Fan readings, curves and profiles",
        [LogCategories.Lighting] = "RGB lighting through OpenRGB",
        [LogCategories.Storage] = "Disk tests, benchmarks and repairs",
        [LogCategories.Memory] = "RAM tests and memory profiles",
        [LogCategories.Displays] = "Monitor settings and profiles",
        [LogCategories.Audio] = "Sound devices and volumes",
        [LogCategories.Input] = "Keyboard and mouse settings",
        [LogCategories.Latency] = "Latency analysis and fixes",
        [LogCategories.Startup] = "Startup items and boot configuration",
        [LogCategories.UI] = "Pages and view models",
        [LogCategories.App] = "Application lifecycle, settings and logging",
    };

    /// <summary>
    /// Maps a logger name such as <c>PcChecker.Core.Sensors.LibreHardwareSensorService</c> to a well-known category.
    /// Names that are already a category (for example "Sensors") map to themselves; anything unknown becomes <see cref="LogCategories.App"/>.
    /// </summary>
    public static string FromSourceContext(string? sourceContext)
    {
        if (string.IsNullOrWhiteSpace(sourceContext))
        {
            return LogCategories.App;
        }

        var trimmed = sourceContext.Trim();
        var exact = LogCategories.All.FirstOrDefault(c => c.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("PcChecker", StringComparison.OrdinalIgnoreCase))
        {
            if (parts[1].Equals("Claude", StringComparison.OrdinalIgnoreCase))
            {
                return LogCategories.Assistant;
            }

            if (parts.Length == 2)
            {
                return LogCategories.App;
            }

            var folder = parts[2];
            var mapped = FolderOverrides.TryGetValue(folder, out var o) ? o : folder;
            var known = LogCategories.All.FirstOrDefault(c => c.Equals(mapped, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                return known;
            }

            return parts[1].Equals("App", StringComparison.OrdinalIgnoreCase) ? LogCategories.UI : LogCategories.App;
        }

        return LogCategories.All.FirstOrDefault(c => trimmed.Contains(c, StringComparison.OrdinalIgnoreCase)) ?? LogCategories.App;
    }

    /// <summary>
    /// The namespace prefix that produces a category, for example "Sensors" → <c>PcChecker.Core.Sensors</c> and
    /// "Features" → <c>PcChecker.Core.WindowsFeatures</c>. Returns the input unchanged when it already looks like a namespace.
    /// </summary>
    public static string ToNamespacePrefix(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "PcChecker";
        }

        if (category.Contains('.'))
        {
            return category;
        }

        return category switch
        {
            LogCategories.Features => "PcChecker.Core.WindowsFeatures",
            LogCategories.Overclock => "PcChecker.Core.Overclocking",
            LogCategories.Assistant => "PcChecker.Claude",
            LogCategories.UI => "PcChecker.App.ViewModels",
            LogCategories.App => "PcChecker.App",
            LogCategories.Overlay => "PcChecker.App.Services",
            _ => "PcChecker.Core." + category,
        };
    }

    /// <summary>A plain-English description of what a category contains, for the settings page.</summary>
    public static string Describe(string category) =>
        Descriptions.TryGetValue(category ?? string.Empty, out var d) ? d : "Other log lines";
}
