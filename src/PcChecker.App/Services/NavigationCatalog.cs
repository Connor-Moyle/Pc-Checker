using PcChecker.App.Views.Pages;

namespace PcChecker.App.Services;

/// <summary>One entry in the side navigation.</summary>
public sealed record NavigationEntry(string Group, string Title, string FileName, Type PageType);

/// <summary>
/// The navigation order in one place. <c>MainWindow.xaml</c> lays the same items out with group headers; keep both in
/// sync when adding a page. The screenshot runner walks this list.
/// </summary>
public static class NavigationCatalog
{
    public static IReadOnlyList<NavigationEntry> Pages { get; } =
    [
        new("Overview", "Dashboard", "dashboard", typeof(DashboardPage)),
        new("Monitoring", "Temperatures", "temperatures", typeof(SensorsPage)),
        new("Monitoring", "Power", "power", typeof(PowerPage)),
        new("Monitoring", "Overlay", "overlay-settings", typeof(OverlayPage)),
        new("Hardware", "Hardware", "hardware", typeof(HardwarePage)),
        new("Hardware", "Health", "health", typeof(HealthPage)),
        new("Hardware", "Storage", "storage", typeof(StoragePage)),
        new("Hardware", "Memory", "memory", typeof(MemoryPage)),
        new("Hardware", "Displays", "displays", typeof(DisplaysPage)),
        new("Hardware", "Audio", "audio", typeof(AudioPage)),
        new("Hardware", "Keyboard & Mouse", "input", typeof(InputPage)),
        new("Hardware", "Fans", "fans", typeof(FansPage)),
        new("Hardware", "Lighting", "lighting", typeof(LightingPage)),
        new("Software", "Drivers", "drivers", typeof(DriversPage)),
        new("Software", "Software", "software", typeof(SoftwarePage)),
        new("Software", "Startup", "startup", typeof(StartupPage)),
        new("Software", "Windows Features", "windows-features", typeof(WindowsFeaturesPage)),
        new("Software", "Cleanup", "cleanup", typeof(CleanupPage)),
        new("Performance", "Performance", "performance", typeof(PerformancePage)),
        new("Performance", "Optimize", "optimize", typeof(OptimizePage)),
        new("Performance", "Latency", "latency", typeof(LatencyPage)),
        new("Performance", "Overclock", "overclock", typeof(OverclockPage)),
        new("Assistant", "Ask Claude", "assistant", typeof(AssistantPage)),
        new("Footer", "Logs", "logs", typeof(LogsPage)),
        new("Footer", "Settings", "settings", typeof(SettingsPage)),
    ];
}
