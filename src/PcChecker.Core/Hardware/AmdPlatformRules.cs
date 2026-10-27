using PcChecker.Core.Common;
using PcChecker.Core.Health;

namespace PcChecker.Core.Hardware;

/// <summary>A device as Win32_PnPSignedDriver reports it (name and driver provider).</summary>
public sealed record PlatformDevice(string Name, string? Provider);

/// <summary>State of a Windows service: null fields mean unknown, <see cref="Exists"/> false means not installed.</summary>
public sealed record PlatformServiceState(bool Exists, bool? Running, bool? Automatic);

/// <summary>What the AMD platform checks need to know about this PC.</summary>
public sealed record AmdPlatformFacts(
    string? CpuName,
    string? ChipsetSoftwareVersion,
    IReadOnlyList<PlatformDevice> Devices,
    PlatformServiceState? VCacheService,
    bool? GameBarInstalled)
{
    public static AmdPlatformFacts None { get; } = new(null, null, [], null, null);
}

/// <summary>
/// Pure AMD platform rules: AMD Chipset Software / PPM provisioning / PSP checks, the dual-CCD X3D scheduling
/// requirements (Balanced plan, core parking, Game Mode, Game Bar, the 3D V-Cache Performance Optimizer), and the
/// warnings shown before an action that would undo them.
/// </summary>
public static class AmdPlatformRules
{
    public const string ChipsetDownloadUrl = "https://www.amd.com/en/support/download/drivers.html";
    public const string VCacheServiceName = "amd3dvcacheSvc";
    public const string VCacheDeviceName = "AMD 3D V-Cache Performance Optimizer";
    public const string PpmDeviceName = "AMD PPM Provisioning File Driver";

    /// <summary>The warning dual-CCD X3D owners see before Game Bar is removed or turned off.</summary>
    public const string GameBarX3dWarning =
        "AMD Ryzen 9 X3D CPUs need Game Bar to move games onto the V-Cache cores. Without it, games run on both CCDs and lose frames.";

    /// <summary>Action ids that undo the X3D scheduling setup.</summary>
    public static IReadOnlyList<string> X3dConflictingActions { get; } =
        ["power.highperformance", "power.ultimate", "latency.coreparking.off", "gamebar.disable"];

    /// <summary>
    /// The warning to show before an action on a dual-CCD X3D CPU, or null when nothing conflicts. AMD's driver parks
    /// the frequency CCD while a game runs; that needs the Balanced plan, core parking and Game Bar game detection.
    /// </summary>
    public static string? ActionWarning(string? actionId, string? cpuName)
    {
        if (actionId is null || !CpuTraits.IsDualCcdX3D(cpuName))
        {
            return null;
        }

        return actionId.ToLowerInvariant() switch
        {
            "power.highperformance" or "power.ultimate" =>
                "Your Ryzen 9 X3D needs the Balanced power plan: AMD's driver uses it to park the second CCD while a game runs. High performance and Ultimate keep both CCDs awake, so games spread across them and lose frames.",
            "latency.coreparking.off" =>
                "Your Ryzen 9 X3D relies on core parking: AMD's driver parks the frequency CCD so games stay on the V-Cache cores. Turning parking off costs frames in games.",
            "gamebar.disable" or "apps.gamebar" => GameBarX3dWarning,
            _ => null,
        };
    }

    /// <summary>The socket-specific chipset download hint ("AM5", "AM4", "sTR5"), or the generic page.</summary>
    public static string ChipsetHint(string? cpuName)
    {
        var socket = CpuTraits.Parse(cpuName).Socket;
        return socket is "AM4" or "AM5" or "sTR5" or "sWRX8" or "sTRX4" or "TR4"
            ? $"Download AMD Chipset Software for socket {socket} from {ChipsetDownloadUrl} (or use AMD's Auto-Detect and Install tool)."
            : $"Download AMD Chipset Software from {ChipsetDownloadUrl}, or the chipset driver from your laptop maker.";
    }

    /// <summary>Health items for an AMD CPU: chipset software, PPM provisioning, and the X3D optimizer. Empty for other CPUs.</summary>
    public static IReadOnlyList<HealthItem> ChipsetItems(AmdPlatformFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var traits = CpuTraits.Parse(facts.CpuName);
        if (!traits.IsAmd)
        {
            return [];
        }

        var items = new List<HealthItem>();
        var metrics = new Dictionary<string, string>();
        if (facts.ChipsetSoftwareVersion is { Length: > 0 } v)
        {
            metrics["AMD Chipset Software"] = v;
        }

        bool ppm = HasAmdDevice(facts.Devices, "PPM Provisioning");
        bool psp = HasAmdDevice(facts.Devices, "AMD PSP");
        bool chipset = facts.ChipsetSoftwareVersion is { Length: > 0 };
        var generic = facts.Devices
            .Where(d => (Has(d.Name, "AMD GPIO") || Has(d.Name, "AMD SMBus") || Has(d.Name, "AMD I2C") || Has(d.Name, "AMD PSP")) && !IsAmdProvider(d.Provider))
            .Select(d => d.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!chipset && !ppm)
        {
            items.Add(new HealthItem("Drivers", HealthStatus.Warning, "AMD chipset driver is not installed",
                "Windows is using its own generic drivers for the AMD platform. AMD Chipset Software adds the power-plan tuning (PPM provisioning) that tells Windows which cores boost highest, plus the PSP, GPIO and SMBus drivers.",
                ChipsetHint(facts.CpuName), metrics));
        }
        else if (!ppm && traits.Generation >= ZenGeneration.Zen2)
        {
            items.Add(new HealthItem("Drivers", HealthStatus.Warning, "AMD power-plan tuning (PPM provisioning) is missing",
                "AMD Chipset Software is installed, but the \"AMD PPM Provisioning File Driver\" device is not. Without it Windows may not prefer the fastest cores.",
                "Reinstall AMD Chipset Software. " + ChipsetHint(facts.CpuName), metrics));
        }

        if (generic.Count > 0 && (chipset || ppm))
        {
            items.Add(new HealthItem("Drivers", HealthStatus.Warning, "Some AMD platform devices use generic drivers",
                $"{string.Join(", ", generic)} {(generic.Count == 1 ? "uses" : "use")} a Windows driver instead of AMD's.",
                "Reinstall AMD Chipset Software so every platform device gets AMD's driver.", metrics));
        }

        if (traits.IsDualCcdX3D)
        {
            bool optimizer = facts.Devices.Any(d => Has(d.Name, "3D V-Cache"));
            var svc = facts.VCacheService;
            if (!optimizer || svc is { Exists: false })
            {
                items.Add(new HealthItem("Drivers", HealthStatus.Warning, "AMD 3D V-Cache Performance Optimizer is missing",
                    "Your dual-CCD X3D CPU needs this AMD driver to keep games on the V-Cache cores. Without it games spread across both CCDs and lose frames.",
                    "Install the latest AMD Chipset Software, then keep the Balanced power plan, Game Mode on and Xbox Game Bar installed. " + ChipsetHint(facts.CpuName), metrics));
            }
            else if (svc is { Running: false } or { Automatic: false })
            {
                items.Add(new HealthItem("Drivers", HealthStatus.Warning, "The AMD 3D V-Cache service is not running",
                    $"The {VCacheServiceName} service must run (start type Automatic) for games to be moved onto the V-Cache cores.",
                    "Open Services, set \"AMD 3D V-Cache Performance Optimizer Service\" to Automatic and start it, or reinstall AMD Chipset Software.", metrics));
            }
        }

        if (items.Count == 0)
        {
            items.Add(new HealthItem("Drivers", HealthStatus.Good, "AMD chipset driver is installed",
                chipset ? $"AMD Chipset Software {facts.ChipsetSoftwareVersion} is installed and the platform devices use AMD's drivers." : "AMD's platform drivers are installed.",
                null, metrics));
        }

        return items;
    }

    /// <summary>
    /// Plain-English status of the X3D scheduling requirements, for the Latency page. Null when the CPU is not a
    /// dual-CCD X3D. <paramref name="problems"/> lists what is wrong (empty when everything is set up).
    /// </summary>
    public static string? X3dSchedulingSummary(string? cpuName, bool balancedPlan, bool? coreParkingAllowed, bool? gameMode, AmdPlatformFacts? facts, out IReadOnlyList<string> problems)
    {
        var list = new List<string>();
        problems = list;
        if (!CpuTraits.IsDualCcdX3D(cpuName))
        {
            return null;
        }

        if (!balancedPlan)
        {
            list.Add("switch back to the Balanced power plan");
        }

        if (coreParkingAllowed == false)
        {
            list.Add("allow core parking again (undo \"Stop CPU cores from parking\")");
        }

        if (gameMode == false)
        {
            list.Add("turn Game Mode on");
        }

        if (facts?.GameBarInstalled == false)
        {
            list.Add("reinstall Xbox Game Bar from the Microsoft Store");
        }

        if (facts is not null && facts.Devices.Count > 0 && !facts.Devices.Any(d => Has(d.Name, "3D V-Cache")))
        {
            list.Add("install AMD Chipset Software (it adds the 3D V-Cache Performance Optimizer)");
        }
        else if (facts?.VCacheService is { Running: false } or { Automatic: false } or { Exists: false })
        {
            list.Add($"start the {VCacheServiceName} service and set it to Automatic");
        }

        return list.Count == 0
            ? "Your Ryzen 9 X3D is set up the way AMD's driver needs: Balanced plan, core parking, Game Mode and Game Bar. Keep it this way so games stay on the V-Cache cores."
            : "AMD's driver moves games onto the V-Cache CCD only with the Balanced plan, core parking, Game Mode and Game Bar. To fix it: " + string.Join("; ", list) + ".";
    }

    private static bool HasAmdDevice(IEnumerable<PlatformDevice> devices, string part) =>
        devices.Any(d => Has(d.Name, part) && (d.Provider is null || IsAmdProvider(d.Provider)));

    /// <summary>True for "Advanced Micro Devices, Inc.", "AMD" and "ATI Technologies Inc.".</summary>
    public static bool IsAmdProvider(string? provider) =>
        provider is not null && (Has(provider, "Advanced Micro Devices") || provider.Trim().Equals("AMD", StringComparison.OrdinalIgnoreCase)
            || Has(provider, "ATI Technologies"));

    private static bool Has(string? text, string part) => text is not null && text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
