namespace PcChecker.Core.Optimization;

/// <summary>The fixed list of optimization actions the app offers.</summary>
public static class OptimizationCatalog
{
    public const string PowerCategory = "Power";
    public const string StartupCategory = "Startup";
    public const string VisualsCategory = "Appearance";
    public const string GamingCategory = "Gaming";
    public const string StorageCategory = "Storage";
    public const string NetworkCategory = "Network";
    public const string PrivacyCategory = "Privacy";
    public const string ServicesCategory = "Services";
    public const string RepairCategory = "Repair";

    public static IReadOnlyList<OptimizationAction> All { get; } =
    [
        new("power.highperformance", "Use the High performance power plan",
            "Keeps the CPU ready to run at full speed. Uses a little more electricity; best for desktops.",
            PowerCategory, RiskLevel.Safe, true, false, false),
        new("power.balanced", "Use the Balanced power plan",
            "The Windows default: good performance while saving power when idle.",
            PowerCategory, RiskLevel.Safe, true, false, false),
        new("power.ultimate", "Enable the Ultimate performance power plan",
            "Removes small power-saving delays for workstation use. Not recommended on laptops.",
            PowerCategory, RiskLevel.Advanced, true, false, true),
        new("visuals.performance", "Adjust visual effects for best performance",
            "Turns off animations, shadows and transparency so the desktop feels snappier on slower PCs.",
            VisualsCategory, RiskLevel.Safe, true, false, false),
        new("visuals.balanced", "Let Windows choose visual effects",
            "Restores the default appearance settings.",
            VisualsCategory, RiskLevel.Safe, true, false, false),
        new("transparency.disable", "Turn off transparency effects",
            "Removes the see-through look of the Start menu and taskbar, which saves GPU work.",
            VisualsCategory, RiskLevel.Safe, true, false, false),
        new("animations.disable", "Turn off window animations",
            "Windows open and close instantly instead of animating.",
            VisualsCategory, RiskLevel.Safe, true, false, false),
        new("faststartup.disable", "Turn off fast startup",
            "Makes shutdown a true shutdown. Fixes some driver and dual-boot problems; boot becomes slightly slower.",
            PowerCategory, RiskLevel.Safe, true, false, false),
        new("faststartup.enable", "Turn on fast startup",
            "Lets Windows boot faster after a shutdown by saving part of the system state.",
            PowerCategory, RiskLevel.Safe, true, false, false),
        new("hibernation.disable", "Turn off hibernation",
            "Frees several gigabytes used by hiberfil.sys. Also turns off fast startup.",
            StorageCategory, RiskLevel.Moderate, true, false, false),
        new("gamemode.enable", "Turn on Game Mode",
            "Windows gives games priority over background tasks while you play.",
            GamingCategory, RiskLevel.Safe, true, false, false),
        new("gamebar.disable", "Turn off the Xbox Game Bar",
            "Stops the Game Bar overlay and its background recording features.",
            GamingCategory, RiskLevel.Safe, true, false, false),
        new("backgroundapps.disable", "Stop Store apps running in the background",
            "Apps from the Microsoft Store will only use resources while open.",
            PrivacyCategory, RiskLevel.Safe, true, false, false),
        new("storagesense.enable", "Turn on Storage Sense",
            "Windows will automatically clear temporary files and empty the recycle bin.",
            StorageCategory, RiskLevel.Safe, true, false, false),
        new("notifications.focus", "Reduce notification interruptions",
            "Turns off tips, suggestions and welcome-experience notifications.",
            PrivacyCategory, RiskLevel.Safe, true, false, false),
        new("trim.enable", "Turn on TRIM for SSDs",
            "Lets the SSD clean up deleted data so it stays fast.",
            StorageCategory, RiskLevel.Safe, true, false, false),
        new("defrag.optimize", "Optimize drives now",
            "Runs TRIM on SSDs and defragments hard disks.",
            StorageCategory, RiskLevel.Safe, false, false, false),
        new("tempfiles.clean", "Clean temporary files",
            "Opens the Cleanup section to remove junk files by category.",
            StorageCategory, RiskLevel.Safe, false, false, false),
        new("dns.cloudflare", "Use Cloudflare DNS (1.1.1.1)",
            "Switches name lookups to a fast public resolver. Each adapter's own DNS servers are saved first and come back with Revert.",
            NetworkCategory, RiskLevel.Advanced, true, false, true),
        new("telemetry.minimal", "Reduce diagnostic data sent to Microsoft",
            "Sets diagnostic data to the required minimum and disables tailored experiences.",
            PrivacyCategory, RiskLevel.Advanced, true, false, true),
        new("sysmain.disable", "Disable SysMain (Superfetch)",
            "Stops the service that preloads apps into memory. Can help on some SSD-only systems; usually best left on.",
            ServicesCategory, RiskLevel.Advanced, true, false, true),
        new("search.indexing.disable", "Disable Windows Search indexing",
            "Stops the background indexer. Searching files becomes slower but background disk activity drops.",
            ServicesCategory, RiskLevel.Advanced, true, false, true),
        new("dism.restorehealth", "Repair the Windows image (DISM)",
            "Runs DISM /RestoreHealth to fix damaged system files from Windows Update. Takes several minutes.",
            RepairCategory, RiskLevel.Advanced, false, false, true),
        new("sfc.scannow", "Check system files (SFC)",
            "Runs sfc /scannow to find and repair corrupted Windows files. Takes several minutes.",
            RepairCategory, RiskLevel.Advanced, false, true, true),
        new("network.reset", "Reset network settings",
            "Resets TCP/IP, Winsock and DNS cache. Fixes many connection problems; you may need to reconnect to Wi-Fi.",
            NetworkCategory, RiskLevel.Advanced, false, true, true),
    ];

    public static OptimizationAction? Find(string id) => All.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<OptimizationAction> ForMode(bool advancedMode) => advancedMode ? All : All.Where(a => !a.AdvancedOnly);

    /// <summary>Actions that cancel each other out (applying one reverts the other).</summary>
    public static IReadOnlyDictionary<string, string> Opposites { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["faststartup.disable"] = "faststartup.enable",
        ["faststartup.enable"] = "faststartup.disable",
        ["visuals.performance"] = "visuals.balanced",
        ["visuals.balanced"] = "visuals.performance",
    };
}
