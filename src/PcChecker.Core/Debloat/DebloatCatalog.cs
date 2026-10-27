using PcChecker.Core.Optimization;
using static PcChecker.Core.Debloat.DebloatSources;

namespace PcChecker.Core.Debloat;

/// <summary>
/// The data-driven list of debloat tweaks, grouped by category. Each registry path and value was checked against at
/// least two of the reference tools or Microsoft's policy documentation; Windows defaults are the values a clean
/// install has (null = the value does not exist), used when no "before" value was captured.
/// </summary>
public static partial class DebloatCatalog
{
    public const string PrivacyCategory = "Privacy & telemetry";
    public const string AdsCategory = "Ads & suggestions";
    public const string AiCategory = "AI & Copilot";
    public const string TaskbarCategory = "Taskbar & Start";
    public const string ExplorerCategory = "File Explorer";
    public const string PreferencesCategory = "Windows preferences";
    public const string EdgeCategory = "Edge & OneDrive";
    public const string OtherAppsCategory = "Other apps";
    public const string InstallsCategory = "Automatic installs";
    public const string GamingCategory = "Gaming & Xbox";
    public const string UpdateCategory = "Windows Update";
    public const string ServicesCategory = "Background services";
    public const string AppsCategory = "Apps";

    /// <summary>First Windows 11 build (21H2).</summary>
    public const int Windows11 = 22000;

    /// <summary>Windows 11 22H2.</summary>
    public const int Windows11_22H2 = 22621;

    /// <summary>Windows 11 23H2.</summary>
    public const int Windows11_23H2 = 22631;

    /// <summary>Windows 11 24H2.</summary>
    public const int Windows11_24H2 = 26100;

    /// <summary>Last Windows 10 build (22H2).</summary>
    public const int Windows10Last = 19045;

    public const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    /// <summary>Categories in display order.</summary>
    public static IReadOnlyList<string> Categories { get; } =
    [
        PrivacyCategory, AdsCategory, AiCategory, TaskbarCategory, ExplorerCategory, PreferencesCategory, EdgeCategory, OtherAppsCategory, GamingCategory,
        UpdateCategory, ServicesCategory, InstallsCategory, AppsCategory,
    ];

    public static IReadOnlyList<DebloatTweak> All { get; } =
    [
        .. PrivacyTweaks(), .. MorePrivacyTweaks(), .. AdsTweaks(), .. MoreAdsTweaks(), .. AiTweaks(), .. MoreAiTweaks(),
        .. TaskbarTweaks(), .. MoreTaskbarTweaks(), .. ExplorerTweaks(), .. MoreExplorerTweaks(), .. PreferenceTweaks(),
        .. EdgeTweaks(), .. MoreEdgeTweaks(), .. OtherAppTweaks(), .. GamingTweaks(), .. MoreGamingTweaks(), .. UpdateTweaks(), .. MoreUpdateTweaks(),
        .. ServiceTweaks(), .. MoreServiceTweaks(), .. InstallTweaks(), .. AppTweaks(),
    ];

    private static readonly Dictionary<string, DebloatTweak> ById = All.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

    public static DebloatTweak? Find(string? id) => id is not null && ById.TryGetValue(id, out var t) ? t : null;

    public static IEnumerable<DebloatTweak> ForMode(bool advancedMode) => advancedMode ? All : All.Where(t => !t.AdvancedOnly);

    // ---- builders ----------------------------------------------------------------------------------------------

    private static DebloatTweak Tweak(string id, string category, string title, string description, RiskLevel risk,
        string[] tools, DebloatOperation[] operations, bool advancedOnly = false, bool restart = false, int? minBuild = null,
        int? maxBuild = null, bool explorer = false, string? warning = null) =>
        new(id, category, title, description, risk, advancedOnly, restart, tools, operations, null, minBuild, minBuild >= Windows11)
        {
            MaxWindowsBuild = maxBuild,
            RestartsExplorer = explorer,
            Warning = warning,
        };

    private static DebloatTweak Linked(string id, string category, string title, string description, RiskLevel risk,
        string[] tools, string optimizationId, bool advancedOnly = false, bool restart = false) =>
        new(id, category, title, description, risk, advancedOnly, restart, tools, [], optimizationId, null, false);

    private static string[] From(params string[] tools) => tools;

    private static RegistryValueOp User(string path, string name, int applied, int? windowsDefault) =>
        new(DebloatHive.CurrentUser, path, name, DebloatValueKind.DWord, applied.ToString(System.Globalization.CultureInfo.InvariantCulture), windowsDefault?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static RegistryValueOp Machine(string path, string name, int applied, int? windowsDefault) =>
        new(DebloatHive.LocalMachine, path, name, DebloatValueKind.DWord, applied.ToString(System.Globalization.CultureInfo.InvariantCulture), windowsDefault?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static RegistryValueOp UserText(string path, string name, string applied, string? windowsDefault) =>
        new(DebloatHive.CurrentUser, path, name, DebloatValueKind.String, applied, windowsDefault);

    private static RegistryValueOp MachineText(string path, string name, string applied, string? windowsDefault) =>
        new(DebloatHive.LocalMachine, path, name, DebloatValueKind.String, applied, windowsDefault);

    private static RegistryValueOp ExplorerFlag(string name, int applied, int? windowsDefault) => User(ExplorerAdvanced, name, applied, windowsDefault);

    private static RegistryValueOp ContentFlagOff(string name) => User(ContentDelivery, name, 0, 1);

    private static RegistryValueOp DefaultUserText(string path, string name, string applied, string? windowsDefault) =>
        new(DebloatHive.Users, @".DEFAULT\" + path, name, DebloatValueKind.String, applied, windowsDefault);

    /// <summary>The tweak only applies when one of these keys exists (software installed).</summary>
    private static DebloatRequirement Needs(string missingText, params DebloatKeyRef[] anyOf) => new(missingText, anyOf);

    private static DebloatKeyRef MachineKey(string path) => new(DebloatHive.LocalMachine, path);

    private static DebloatKeyRef UserKey(string path) => new(DebloatHive.CurrentUser, path);

    /// <summary>The App Paths registration of a desktop program, per machine and per user.</summary>
    private static DebloatKeyRef[] AppPath(string exe) =>
    [
        MachineKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe),
        UserKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe),
    ];

    private static ServiceOp Service(string name, DebloatStartMode applied, DebloatStartMode windowsDefault) => new(name, applied, windowsDefault);

    private static ScheduledTaskOp DisableTask(string path) => new(path);
}
