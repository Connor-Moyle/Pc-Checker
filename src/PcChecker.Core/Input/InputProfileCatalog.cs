using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Input;

/// <summary>Built-in keyboard/mouse profiles and vendor software lookup. Pure.</summary>
public static class InputProfileCatalog
{
    public const string DefaultProfileId = "input.profile.default";
    public const string GamingProfileId = "input.profile.gaming";
    public const string PrecisionProfileId = "input.profile.precision";
    public const string AccessibilityProfileId = "input.profile.accessibility";

    public static IReadOnlyList<InputProfile> BuiltIn { get; } =
    [
        new(DefaultProfileId, "Windows default", "Pointer speed 6/11 with Enhance pointer precision on; standard keyboard repeat. Use this to undo any other profile.",
            new MouseSettings(), new KeyboardSettings(), true),
        new(GamingProfileId, "Gaming", "Pointer speed 6/11, pointer acceleration off so your aim is consistent, no pointer trails, shortest key repeat delay.",
            new MouseSettings { Speed = 10, EnhancePointerPrecision = false, PointerTrails = 0, ScrollLines = 3 }, new KeyboardSettings { RepeatDelay = 0, RepeatRate = 31 }, true),
        new(PrecisionProfileId, "Precision work", "Slightly slower pointer (5/11) with acceleration on for fine control in design, photo and office apps.",
            new MouseSettings { Speed = 8, EnhancePointerPrecision = true }, new KeyboardSettings(), true),
        new(AccessibilityProfileId, "Accessibility", "Longer double-click window, pointer trails, a longer repeat delay and Sticky Keys for easier use.",
            new MouseSettings { DoubleClickMs = 900, PointerTrails = 4 }, new KeyboardSettings { StickyKeys = true, RepeatDelay = 3 }, true),
    ];

    /// <summary>Finds a built-in profile by id, or null.</summary>
    public static InputProfile? FindBuiltIn(string? id) =>
        id is null ? null : BuiltIn.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Keyword → vendor software table. Keywords are matched case-insensitively against the device name, maker and description.</summary>
    private static readonly (string[] Keywords, string Vendor, string Software, string Url)[] VendorTable =
    [
        (["logitech", "logi ", "logi-"], "Logitech", "Logitech G HUB", "https://www.logitechg.com/en-us/innovation/g-hub.html"),
        (["razer"], "Razer", "Razer Synapse", "https://www.razer.com/synapse-4"),
        (["corsair"], "Corsair", "Corsair iCUE", "https://www.corsair.com/us/en/s/icue"),
        (["steelseries"], "SteelSeries", "SteelSeries GG", "https://steelseries.com/gg"),
        (["glorious"], "Glorious", "Glorious Core", "https://www.gloriousgaming.com/pages/software"),
        (["asus", "rog ", "tuf gaming"], "ASUS", "Armoury Crate", "https://rog.asus.com/armoury-crate/"),
        (["hyperx"], "HyperX", "HyperX NGENUITY", "https://hyperx.com/pages/ngenuity"),
        (["zowie"], "Zowie", "Zowie (settings are on-device)", "https://zowie.benq.com/"),
        (["microsoft"], "Microsoft", "Mouse and Keyboard Center", "https://www.microsoft.com/accessories/en-us/products/mouse-keyboard-center"),
        (["wooting"], "Wooting", "Wootility", "https://wooting.io/wootility"),
        (["keychron"], "Keychron", "Keychron Launcher", "https://launcher.keychron.com/"),
        (["cooler master", "coolermaster"], "Cooler Master", "MasterPlus+", "https://www.coolermaster.com/catalog/software/"),
        (["roccat", "turtle beach"], "ROCCAT / Turtle Beach", "Swarm", "https://www.turtlebeach.com/pages/swarm"),
        (["endgame gear"], "Endgame Gear", "Endgame Gear configuration software", "https://www.endgamegear.com/en-us/gaming-mice"),
        (["pulsar"], "Pulsar", "Pulsar Fusion", "https://www.pulsar.gg/pages/software"),
        (["lamzu"], "LAMZU", "LAMZU configuration software", "https://lamzu.com/pages/software"),
        (["ducky"], "Ducky", "Ducky (on-board macros)", "https://www.duckychannel.com.tw/"),
        (["cherry"], "CHERRY", "CHERRY Utility", "https://www.cherry.de/en-gb/service/software"),
        (["finalmouse"], "Finalmouse", "Finalmouse XPanel", "https://finalmouse.com/"),
        (["xtrfy"], "Xtrfy", "Xtrfy (settings are on-device)", "https://xtrfy.com/"),
        (["msi"], "MSI", "MSI Center", "https://www.msi.com/Landing/MSI-Center"),
        (["gigabyte", "aorus"], "GIGABYTE", "GIGABYTE Control Center", "https://www.gigabyte.com/Consumer/Software/GIGABYTE-Control-Center"),
    ];

    /// <summary>
    /// Vendor software links for the detected keyboards and mice, one per vendor, plus PowerToys Keyboard Manager
    /// for key remapping on any keyboard.
    /// </summary>
    public static IReadOnlyList<VendorLink> VendorLinksFor(IEnumerable<InputDeviceInfo>? devices)
    {
        var links = new List<VendorLink>();
        foreach (var device in devices ?? [])
        {
            if (device is null)
            {
                continue;
            }

            var text = $"{device.Name} {device.Manufacturer} {device.Description}".ToLowerInvariant();
            foreach (var (keywords, vendor, software, url) in VendorTable)
            {
                if (keywords.Any(k => text.Contains(k, StringComparison.Ordinal)) && links.All(l => l.Vendor != vendor))
                {
                    links.Add(new VendorLink(vendor, device.Kind, url, $"{software}: buttons, DPI, polling rate, lighting and firmware for {vendor} devices."));
                }
            }
        }

        links.Add(new VendorLink("Microsoft PowerToys", "Keyboard", "https://learn.microsoft.com/windows/powertoys/keyboard-manager",
            "PowerToys Keyboard Manager: remap keys and shortcuts on any keyboard."));
        return links;
    }
}
