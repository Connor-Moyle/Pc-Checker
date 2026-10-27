namespace PcChecker.Core.Lighting;

/// <summary>
/// Vendor RGB apps that take hold of lighting controllers, and the pure rule that decides which are running. Two
/// programs talking to the same controller fight: colours flicker, OpenRGB does not list the device, or changes are
/// overwritten a moment later.
/// </summary>
public static class RgbConflictRules
{
    /// <summary>The vendor apps PC Checker looks for, by process and service name.</summary>
    public static IReadOnlyList<RgbVendorApp> Catalog { get; } =
    [
        new("icue", "Corsair iCUE", ["Corsair"],
            ["icue", "corsair.service", "icuedevicepluginhost", "corsairdevicecontrolservice"],
            ["CorsairService", "CorsairLLAService", "CorsairDeviceControlService"], null, ControlsFans: true,
            "Controls Corsair lighting, fans and pumps, and keeps Corsair devices to itself while it runs."),
        new("armourycrate", "ASUS Armoury Crate (Aura)", ["ASUS"],
            ["armourycrate", "armourycrate.service", "armourycrate.usersessionhelper", "lightingservice", "aurawallpaperservice"],
            ["LightingService", "ArmouryCrateService", "AuraWallpaperService"], null, ControlsFans: true,
            "Its LightingService keeps writing to the ASUS lighting chip, so other programs' colours are overwritten."),
        new("msicenter", "MSI Center / Mystic Light", ["MSI"],
            ["msi.centralserver", "msi_central_service", "mysticlight", "ledkeeper2", "msi.mysticlight"],
            ["MSI_Central_Service", "Mystic_Light_Service"], null, ControlsFans: true,
            "Holds the MSI Mystic Light controller and resets its effects."),
        new("gigabyte", "Gigabyte Control Center / RGB Fusion", ["Gigabyte"],
            ["rgbfusion", "gcc"], [], PathHint: "gigabyte", ControlsFans: true,
            "Holds the Gigabyte RGB Fusion controller and resets its effects."),
        new("signalrgb", "SignalRGB", [],
            ["signalrgb", "signalrgblauncher"], [], null, ControlsFans: false,
            "Another all-in-one RGB app: it drives the same devices as OpenRGB, so the two overwrite each other."),
        new("nzxtcam", "NZXT CAM", ["NZXT"],
            ["nzxt cam"], [], null, ControlsFans: true,
            "Controls NZXT lighting, fans and pumps, and keeps NZXT devices to itself while it runs."),
        new("synapse", "Razer Synapse", ["Razer"],
            ["razer synapse 3", "razer synapse", "razer synapse service", "razerappengine", "razercentralservice", "rzchromasdkservice"],
            ["Razer Synapse Service", "Razer Chroma SDK Service"], null, ControlsFans: false,
            "Controls Razer lighting and resends its own effects."),
        new("ghub", "Logitech G HUB", ["Logitech"],
            ["lghub", "lghub_agent", "lghub_system_tray"], ["LGHUBUpdaterService"], null, ControlsFans: false,
            "Controls Logitech lighting and resends its own effects."),
        new("steelseries", "SteelSeries GG", ["SteelSeries"],
            ["steelseriesgg", "steelseriesggclient", "steelseriesengine"], [], null, ControlsFans: false,
            "Controls SteelSeries lighting and resends its own effects."),
        new("lconnect", "Lian Li L-Connect", ["Lian Li"],
            ["l-connect 3", "l-connect", "l-connect-service"], [], null, ControlsFans: true,
            "Controls Lian Li fan hubs and Strimer cables and keeps them to itself while it runs."),
        new("ttrgbplus", "Thermaltake TT RGB Plus", ["Thermaltake"],
            ["tt rgb plus"], [], null, ControlsFans: true,
            "Controls Thermaltake fans and lighting and keeps them to itself while it runs."),
    ];

    /// <summary>Lower-case name without ".exe" or folder.</summary>
    public static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var n = name.Trim();
        int slash = n.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            n = n[(slash + 1)..];
        }

        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            n = n[..^4];
        }

        return n.ToLowerInvariant();
    }

    /// <summary>
    /// The catalog apps that are running, from the process list and the names of running services. A process whose
    /// app has a <see cref="RgbVendorApp.PathHint"/> only counts when its path is known and contains the hint.
    /// </summary>
    public static IReadOnlyList<RgbConflict> Detect(IEnumerable<RgbRunningProcess> processes, IEnumerable<string> runningServices)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(runningServices);
        var procs = processes.ToList();
        var services = new HashSet<string>(runningServices.Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
        var result = new List<RgbConflict>();
        foreach (var app in Catalog)
        {
            var matched = procs.Where(p => app.ProcessNames.Contains(NormalizeProcessName(p.Name))
                    && (app.PathHint is null || (p.Path?.Contains(app.PathHint, StringComparison.OrdinalIgnoreCase) ?? false)))
                .ToList();
            var svc = app.ServiceNames.Where(services.Contains).ToList();
            if (matched.Count > 0 || svc.Count > 0)
            {
                result.Add(new RgbConflict(app, matched, svc));
            }
        }

        return result;
    }

    /// <summary>The running app that most likely holds a device of this brand, or null.</summary>
    public static RgbConflict? ConflictFor(string? brand, IEnumerable<RgbConflict> conflicts)
    {
        var list = conflicts.ToList();
        if (!string.IsNullOrWhiteSpace(brand))
        {
            var own = list.FirstOrDefault(c => c.App.Brands.Any(b => brand.StartsWith(b, StringComparison.OrdinalIgnoreCase)));
            if (own is not null)
            {
                return own;
            }
        }

        // Apps that drive every brand (SignalRGB) can hold anything.
        return list.FirstOrDefault(c => c.App.Brands.Count == 0);
    }

    /// <summary>Text for the "Close it for this session" confirmation.</summary>
    public static string CloseConfirmation(RgbConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        var parts = new List<string>
        {
            $"PC Checker closes {conflict.App.Product} until you restart the PC or open it again. Nothing is uninstalled and its settings stay as they are.",
        };
        if (conflict.Services.Count > 0)
        {
            parts.Add($"Its background service{(conflict.Services.Count == 1 ? string.Empty : "s")} ({string.Join(", ", conflict.Services)}) {(conflict.Services.Count == 1 ? "is" : "are")} stopped for this session only; the start-up setting is not changed.");
        }

        if (conflict.App.ControlsFans)
        {
            parts.Add("It may also control fans or a pump: while it is closed they run at their default (hardware) speed.");
        }

        parts.Add("Then press Rescan, and Connect in OpenRGB, to pick the devices up.");
        return string.Join("\n\n", parts);
    }
}
