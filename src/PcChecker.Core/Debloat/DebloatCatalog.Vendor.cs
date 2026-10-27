using PcChecker.Core.Optimization;
using static PcChecker.Core.Debloat.DebloatSources;

namespace PcChecker.Core.Debloat;

public static partial class DebloatCatalog
{
    public const string VendorCategory = "AMD & NVIDIA software";

    /// <summary>
    /// AMD and NVIDIA components no tweak may ever disable: FreeSync and display hotkeys (AMD External Events
    /// Utility), the X3D CCD parking service, the graphics kernel drivers and the AMD platform (PSP/PPM) drivers,
    /// plus the NVIDIA display driver service. A catalog test enforces this list.
    /// </summary>
    public static IReadOnlyList<string> NeverDisableVendorServices { get; } =
    [
        "AMD External Events Utility", "amd3dvcacheSvc", "amd3dvcache", "amdkmdag", "amdwddmg", "amdpsp", "AmdPPM", "amdgpio2", "amdi2c",
        "NVDisplay.ContainerLocalSystem", "nvlddmkm",
    ];

    /// <summary>
    /// AMD Software (Adrenalin) and NVIDIA background components. Shown only when that vendor's software is installed
    /// (<see cref="DebloatPrecondition.AmdSoftwareInstalled"/>, <see cref="DebloatPrecondition.NvidiaSoftwareInstalled"/>).
    /// Scheduled tasks and services that do not exist on a PC are reported as not present and left alone.
    /// </summary>
    private static IEnumerable<DebloatTweak> VendorTweaks() =>
    [
        Tweak("amd.uep", VendorCategory, "Leave the AMD User Experience Program",
            "Stops the AMD User Experience Program launcher service that sends anonymous usage data to AMD. Also untick \"AMD User Experience Program\" under AMD Software → Settings → Preferences.",
            RiskLevel.Safe, From(VendorApp),
            [Service("AUEPLauncher", DebloatStartMode.Disabled, DebloatStartMode.Automatic)])
        with { Precondition = DebloatPrecondition.AmdSoftwareInstalled },

        Tweak("amd.updatecheck", VendorCategory, "Stop AMD Software checking for updates at sign-in",
            "Disables AMD's update-check task. AMD Software still checks when you open it (Settings → System → Check for updates).",
            RiskLevel.Safe, From(VendorApp),
            [DisableTask(@"\AMDInstallLauncher")])
        with { Precondition = DebloatPrecondition.AmdSoftwareInstalled },

        Tweak("amd.link", VendorCategory, "Stop AMD Link update tasks",
            "Disables the AMD Link update tasks. Only matters if you never stream games to a phone or TV with AMD Link.",
            RiskLevel.Safe, From(VendorApp),
            [DisableTask(@"\AMDLinkUpdate"), DisableTask(@"\ModifyLinkUpdate")])
        with { Precondition = DebloatPrecondition.AmdSoftwareInstalled },

        Tweak("amd.dvr", VendorCategory, "Don't start AMD recording (Instant Replay) at sign-in",
            "Disables the task that starts AMD's recording and streaming component when you sign in, so Instant Replay does not keep a background buffer.",
            RiskLevel.Moderate, From(VendorApp),
            [DisableTask(@"\StartDVR")],
            warning: "Instant Replay, recording and streaming hotkeys stop working until you open AMD Software.")
        with { Precondition = DebloatPrecondition.AmdSoftwareInstalled },

        Tweak("amd.autostart", VendorCategory, "Don't start AMD Software at sign-in",
            "Disables the task that starts the AMD Software tray and overlay when you sign in. The driver, FreeSync and your saved game settings keep working.",
            RiskLevel.Moderate, From(VendorApp),
            [DisableTask(@"\StartCN")],
            warning: "The Alt+R overlay, performance metrics hotkey and tray icon are gone until you open AMD Software.")
        with { Precondition = DebloatPrecondition.AmdSoftwareInstalled },

        Tweak("amd.crashdefender", VendorCategory, "Set AMD Crash Defender to manual",
            "Stops the AMD Crash Defender service starting with Windows. It collects information after a graphics driver crash.",
            RiskLevel.Advanced, From(VendorApp),
            [Service("amdfendrmgr", DebloatStartMode.Manual, DebloatStartMode.Automatic)],
            advancedOnly: true,
            warning: "Driver crash reports for AMD are lost. Leave it on while you are troubleshooting crashes.")
        with { Precondition = DebloatPrecondition.AmdSoftwareInstalled },

        Tweak("nvidia.telemetry", VendorCategory, "Turn off NVIDIA telemetry",
            "Opts out of NVIDIA's usage data collection in the driver and control panel, and disables the old NVIDIA telemetry container service where it still exists.",
            RiskLevel.Safe, From(Optimizer, VendorApp),
            [
                Machine(@"SOFTWARE\NVIDIA Corporation\NvControlPanel2\Client", "OptInOrOutPreference", 0, null),
                Machine(@"SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID44231", 0, null),
                Machine(@"SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID64640", 0, null),
                Machine(@"SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID66610", 0, null),
                Service("NvTelemetryContainer", DebloatStartMode.Disabled, DebloatStartMode.Automatic),
            ])
        with { Precondition = DebloatPrecondition.NvidiaSoftwareInstalled },
    ];
}
