using static PcChecker.Core.Shell.SearchText;

namespace PcChecker.Core.Shell;

/// <summary>
/// Every option quick search can jump to, with the page it lives on. Titles are the labels exactly as the page shows
/// them (the tests check each one against the page's XAML), so the app can scroll the option into view. Keep this
/// list in step with <c>SettingsPage.xaml</c> when options are added, renamed or removed.
/// </summary>
public static class SettingsSearchCatalog
{
    public const string SettingsPageKey = "settings";

    public static IReadOnlyList<SettingSearchEntry> All { get; } =
    [
        // General
        S("Advanced mode", "General", T("expert riskier hidden options")),
        S("Run a quick health check when the app opens", "General", T("startup scan score")),
        S("Electricity price per kWh", "General", T("energy cost money power bill")),
        S("Currency symbol", "General", T("money dollar euro pound")),

        // Your setup
        S("Back up your setup", "Your setup", T("export backup save profiles settings new pc move transfer pcchecker file")),
        S("Restore from a backup file", "Your setup", T("import restore load profiles settings new pc reinstall")),
        S("Reset one part", "Your setup", T("defaults fans overlay lighting profiles section")),

        // Appearance
        S("Theme", "Appearance", T("dark mode light mode colours colors")),
        S("Language", "Appearance", T("translate translation locale")),

        // Monitoring
        S("Temperature alerts", "Monitoring", T("warnings overheating notify")),
        S("Sensor refresh (milliseconds)", "Monitoring", T("poll interval update rate")),
        S("History kept (minutes)", "Monitoring", T("graph chart duration")),
        S("Temperature limits (°C)", "Monitoring", T("thresholds warn critical cpu gpu drive hot")),
        S("Warn when free memory is low", "Monitoring", T("ram")),
        S("Low memory means less than this % free", "Monitoring", T("ram threshold")),
        S("Warn when many programs start with Windows", "Monitoring", T("startup apps boot")),
        S("'Many' means more than this number of startup items", "Monitoring", T("startup threshold")),

        // Tray and startup
        S("Start with Windows", "Tray and startup", T("autostart login sign in boot")),
        S("When started with Windows, stay in the tray", "Tray and startup", T("hidden background")),
        S("Start minimized", "Tray and startup", T("taskbar")),
        S("Show the PC Checker icon in the tray", "Tray and startup", T("notification area system tray")),
        S("Minimize to the tray", "Tray and startup", T("hide background")),
        S("Close button keeps PC Checker in the tray", "Tray and startup", T("exit background")),
        S("Show the temperature as the tray icon", "Tray and startup", T("tray temperature")),
        S("Temperature alerts as notifications", "Tray and startup", T("toast warnings")),

        // Schedules
        S("Schedules", "Schedules", T("task scheduler daily weekly automatic jobs")),
        S("Notify only when something needs attention", "Schedules", T("notifications quiet")),

        // Updates
        S("Check for updates", "Updates", T("new version upgrade")),
        S("Update channel", "Updates", T("beta stable")),
        S("Check for updates automatically", "Updates", T("auto update")),
        S("When a new version is available", "Updates", T("auto update download install skip version prompt")),

        // Assistant
        S("Share scan results with the assistant", "Assistant", T("claude ai privacy context")),

        // Page settings
        S("Create a restore point before making changes", "Restore points", T("system restore backup snapshot safety")),
        S("Also create one after changes succeed", "Restore points", T("system restore backup")),
        S("Allow more than one restore point per day", "Restore points", T("system restore frequency limit")),
        S("Hand fans back to the motherboard when the app closes", "Fans", T("fan control revert automatic exit")),
        S("Never go below (%)", "Fans", T("fan minimum speed floor")),
        S("Apply the active fan profile at startup", "Fans", T("fan curve profile")),
        S("Server address", "Lighting (OpenRGB)", T("openrgb host rgb")),
        S("Server port", "Lighting (OpenRGB)", T("openrgb port rgb")),
        S("Connect automatically when the Lighting page opens", "Lighting (OpenRGB)", T("openrgb rgb auto connect")),
        S("Ping targets", "Latency checks", T("dns gateway router")),
        S("Pings per target", "Latency checks", T("ping count")),
        S("GPU core offset limit (± MHz)", "Overclock limits", T("overclocking clock safety")),
        S("GPU memory offset limit (± MHz)", "Overclock limits", T("overclocking vram safety")),
        S("Highest GPU power limit (%)", "Overclock limits", T("overclocking watts safety")),
        S("Highest GPU temperature (°C)", "Overclock limits", T("overclocking heat safety")),
        S("Highest CPU temperature (°C)", "Overclock limits", T("overclocking heat safety")),

        // Privacy and logs
        S("Settings file", "Privacy and logs", T("json folder location")),
        S("Log folder", "Privacy and logs", T("logs location")),
        S("Diagnostics", "Privacy and logs", T("zip bug report support export")),
        S("Reset to defaults", "Settings", T("factory reset restore defaults")),

        // About
        S("Show welcome again", "About", T("introduction tour first start getting started")),
        S("Show licence texts", "About", T("license third party open source")),

        // Options that live on their own pages
        new("Toggle hotkey", "Overlay", "overlay-settings", T("shortcut keyboard fps overlay")),
        new("Opacity", "Overlay", "overlay-settings", T("transparency overlay")),
        new("Minimum level", "Logs", "logs", T("logging verbosity debug")),
        new("Hide serial numbers and MAC addresses in logs", "Logs", "logs", T("privacy redact logging")),
        new("Dry run", "Cleanup", "cleanup", T("preview simulate junk files")),
    ];

    private static SettingSearchEntry S(string title, string group, string keywords) => new(title, group, SettingsPageKey, keywords);
}
