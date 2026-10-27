using static PcChecker.Core.Shell.SearchText;

namespace PcChecker.Core.Shell;

/// <summary>Stable ids of the quick actions (also stored in the recent items list).</summary>
public static class QuickActionIds
{
    public const string FullScan = "scan.full";
    public const string CleanTempFiles = "cleanup.scan";
    public const string CheckDrivers = "drivers.check";
    public const string Optimize = "optimize.open";
    public const string ExportReport = "report.export";
    public const string ToggleOverlay = "overlay.toggle";
    public const string LatencyCheck = "latency.check";
    public const string DiskBenchmark = "storage.benchmark";
    public const string HealthCheck = "health.check";
    public const string SpeedCheck = "performance.scan";
    public const string CheckAppUpdates = "app.updates";
    public const string ShowWelcome = "welcome.show";
}

/// <summary>
/// The things quick search can do straight away. Each one runs the same command as the button on its page, so every
/// confirmation and safety step of that page still applies; nothing here changes the PC without asking.
/// </summary>
public static class QuickActionCatalog
{
    public static IReadOnlyList<QuickActionDefinition> All { get; } =
    [
        new(QuickActionIds.FullScan, T("Run a full scan"), "dashboard", T("check everything hardware health performance drivers apps")),
        new(QuickActionIds.CleanTempFiles, T("Clean temporary files"), "cleanup", T("junk temp cache free space disk cleanup")),
        new(QuickActionIds.CheckDrivers, T("Check for driver updates"), "drivers", T("drivers windows update outdated")),
        new(QuickActionIds.Optimize, T("Optimize this PC"), "optimize", T("tweaks faster speed up performance")),
        new(QuickActionIds.ExportReport, T("Export a full report"), "dashboard", T("html json markdown save share")),
        new(QuickActionIds.ToggleOverlay, T("Show or hide the overlay"), null, T("fps counter in game overlay toggle")),
        new(QuickActionIds.LatencyCheck, T("Run a latency check"), "latency", T("dpc input lag ping stutter")),
        new(QuickActionIds.DiskBenchmark, T("Run a disk benchmark"), "storage", T("ssd hdd drive speed test read write")),
        new(QuickActionIds.HealthCheck, T("Check health now"), "health", T("smart errors problems score")),
        new(QuickActionIds.SpeedCheck, T("Run a speed check"), "performance", T("slow slowdown startup services")),
        new(QuickActionIds.CheckAppUpdates, T("Check for PC Checker updates"), "settings", T("new version upgrade")),
        new(QuickActionIds.ShowWelcome, T("Show the welcome again"), null, T("introduction tour getting started")),
    ];

    /// <summary>The action with <paramref name="id"/>, or null.</summary>
    public static QuickActionDefinition? Find(string? id) => All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
}
