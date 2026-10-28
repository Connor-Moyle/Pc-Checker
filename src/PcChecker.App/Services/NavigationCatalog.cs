using PcChecker.App.Views.Pages;
using Wpf.Ui.Controls;

namespace PcChecker.App.Services;

/// <summary>One page. <see cref="Title"/> is the tab label inside its section.</summary>
public sealed record NavigationEntry(string Section, string Title, string FileName, Type PageType)
{
    /// <summary>True for pages that mostly matter in Advanced mode; the tab shows an "Advanced" chip (see SimpleModeRules).</summary>
    public bool IsAdvanced => Core.Shell.SimpleModeRules.IsAdvancedPage(FileName);
}

/// <summary>
/// One item in the side navigation. A section with more than one page shows its pages as tabs above the content;
/// clicking the section opens its first page.
/// </summary>
public sealed record NavigationSection(string Title, SymbolRegular Icon, bool IsFooter, IReadOnlyList<NavigationEntry> Pages)
{
    public bool HasTabs => Pages.Count > 1;

    public Type FirstPage => Pages[0].PageType;
}

/// <summary>
/// The whole navigation in one place: <c>MainWindow</c> builds the side menu from <see cref="Sections"/> and the tab
/// strip from the current section, and the screenshot runner walks <see cref="Pages"/>. Add a page here and nowhere
/// else.
/// </summary>
public static class NavigationCatalog
{
    public static IReadOnlyList<NavigationSection> Sections { get; } =
    [
        Section("Home", SymbolRegular.Home24,
            Page("Dashboard", "dashboard", typeof(DashboardPage)),
            Page("Check-up", "checkup", typeof(CheckupPage))),
        Section("Monitor", SymbolRegular.Temperature24,
            Page("Temperatures", "temperatures", typeof(SensorsPage)),
            Page("Power", "power", typeof(PowerPage)),
            Page("History", "history", typeof(HistoryPage))),
        Section("Task manager", SymbolRegular.TaskListLtr24,
            Page("Processes", "processes", typeof(ProcessesPage)),
            Page("Live usage", "live-usage", typeof(LiveUsagePage)),
            Page("Services", "services", typeof(ServicesPage)),
            Page("Users", "users", typeof(UsersPage))),
        Section("Hardware", SymbolRegular.DeveloperBoard24,
            Page("Overview", "hardware", typeof(HardwarePage)),
            Page("Health", "health", typeof(HealthPage)),
            Page("Storage", "storage", typeof(StoragePage)),
            Page("Memory", "memory", typeof(MemoryPage)),
            Page("Battery", "battery", typeof(BatteryPage))),
        Section("Devices", SymbolRegular.Desktop24,
            Page("Displays", "displays", typeof(DisplaysPage)),
            Page("Audio", "audio", typeof(AudioPage)),
            Page("Keyboard & Mouse", "input", typeof(InputPage))),
        Section("Cooling & RGB", SymbolRegular.WeatherSnowflake24,
            Page("Fans", "fans", typeof(FansPage)),
            Page("Lighting", "lighting", typeof(LightingPage))),
        Section("Apps & Drivers", SymbolRegular.Apps24,
            Page("Apps", "software", typeof(SoftwarePage)),
            Page("Leftovers", "leftovers", typeof(LeftoversPage)),
            Page("Drivers", "drivers", typeof(DriversPage)),
            Page("Old drivers", "old-drivers", typeof(DriverCleanupPage)),
            Page("Windows Update", "windows-update", typeof(WindowsUpdatePage)),
            Page("Startup", "startup", typeof(StartupPage)),
            Page("Scheduled tasks", "scheduled-tasks", typeof(ScheduledTasksPage)),
            Page("Windows Features", "windows-features", typeof(WindowsFeaturesPage))),
        Section("Clean up", SymbolRegular.Broom24,
            Page("Cleanup", "cleanup", typeof(CleanupPage)),
            Page("Disk space", "disk-space", typeof(DiskSpacePage)),
            Page("Debloat", "debloat", typeof(DebloatPage)),
            Page("Right-click menu", "context-menu", typeof(ContextMenuPage)),
            Page("Registry", "registry", typeof(RegistryPage))),
        Section("Security", SymbolRegular.ShieldCheckmark24,
            Page("Security", "security", typeof(SecurityPage)),
            Page("Privacy", "privacy", typeof(PrivacyPage))),
        Section("Performance", SymbolRegular.TopSpeed24,
            Page("Speed check", "performance", typeof(PerformancePage)),
            Page("Optimize", "optimize", typeof(OptimizePage)),
            Page("Benchmark", "benchmark", typeof(BenchmarkPage)),
            Page("Overclock", "overclock", typeof(OverclockPage))),
        Section("Gaming", SymbolRegular.Games24,
            Page("Overlay", "overlay-settings", typeof(OverlayPage)),
            Page("Latency", "latency", typeof(LatencyPage)),
            Page("Missing DLLs", "missing-dlls", typeof(MissingDllsPage))),
        Section("Network", SymbolRegular.Wifi124,
            Page("Network", "network", typeof(NetworkPage)),
            Page("Connections", "connections", typeof(ConnectionsPage)),
            Page("Hosts file", "hosts", typeof(HostsPage))),
        Section("Ask Claude", SymbolRegular.Chat24,
            Page("Ask Claude", "assistant", typeof(AssistantPage))),
        Footer("Logs", SymbolRegular.DocumentText24,
            Page("Logs", "logs", typeof(LogsPage))),
        Footer("Settings", SymbolRegular.Settings24,
            Page("Settings", "settings", typeof(SettingsPage))),
    ];

    /// <summary>Every page in menu order, sections first and footer last.</summary>
    public static IReadOnlyList<NavigationEntry> Pages { get; } = Sections.SelectMany(s => s.Pages).ToList();

    /// <summary>The section that owns <paramref name="pageType"/>, or null for a page that is not in the menu.</summary>
    public static NavigationSection? SectionOf(Type pageType) =>
        Sections.FirstOrDefault(s => s.Pages.Any(p => p.PageType == pageType));

    private static (string Title, string FileName, Type PageType) Page(string title, string fileName, Type pageType) =>
        (title, fileName, pageType);

    private static NavigationSection Section(string title, SymbolRegular icon, params (string Title, string FileName, Type PageType)[] pages) =>
        new(title, icon, false, pages.Select(p => new NavigationEntry(title, p.Title, p.FileName, p.PageType)).ToList());

    private static NavigationSection Footer(string title, SymbolRegular icon, params (string Title, string FileName, Type PageType)[] pages) =>
        new(title, icon, true, pages.Select(p => new NavigationEntry(title, p.Title, p.FileName, p.PageType)).ToList());
}
