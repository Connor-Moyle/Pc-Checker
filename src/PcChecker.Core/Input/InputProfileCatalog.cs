using PcChecker.Core.Drivers;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Input;

/// <summary>Built-in keyboard/mouse profiles and vendor software lookup. Pure.</summary>
public static class InputProfileCatalog
{
    public static IReadOnlyList<InputProfile> BuiltIn { get; } =
    [
        new("input.profile.default", "Windows default", "Pointer speed 6/11 with Enhance pointer precision on; standard keyboard repeat.", new MouseSettings(), new KeyboardSettings(), true),
        new("input.profile.gaming", "Gaming", "Pointer speed 6/11, pointer acceleration off so your aim is consistent, no pointer trails.",
            new MouseSettings { Speed = 10, EnhancePointerPrecision = false, PointerTrails = 0, ScrollLines = 3 }, new KeyboardSettings { RepeatDelay = 0, RepeatRate = 31 }, true),
        new("input.profile.precision", "Precision work", "Slightly slower pointer with acceleration on for fine control in design and office apps.",
            new MouseSettings { Speed = 8, EnhancePointerPrecision = true }, new KeyboardSettings(), true),
        new("input.profile.accessibility", "Accessibility", "Longer double-click window, pointer trails and Sticky Keys for easier use.",
            new MouseSettings { DoubleClickMs = 900, PointerTrails = 4 }, new KeyboardSettings { StickyKeys = true, RepeatDelay = 3 }, true),
    ];

    private static readonly (string[] Keywords, string Vendor, string Software, string Url)[] VendorTable =
    [
        (["logitech", "logi "], "Logitech", "Logitech G HUB", "https://www.logitechg.com/en-us/innovation/g-hub.html"),
        (["razer"], "Razer", "Razer Synapse", "https://www.razer.com/synapse-4"),
        (["corsair"], "Corsair", "Corsair iCUE", "https://www.corsair.com/us/en/s/icue"),
        (["steelseries"], "SteelSeries", "SteelSeries GG", "https://steelseries.com/gg"),
        (["glorious"], "Glorious", "Glorious Core", "https://www.gloriousgaming.com/pages/software"),
        (["asus", "rog "], "ASUS", "Armoury Crate", "https://rog.asus.com/armoury-crate/"),
        (["hyperx"], "HyperX", "HyperX NGENUITY", "https://hyperx.com/pages/ngenuity"),
        (["zowie"], "Zowie", "Zowie (settings are on-device)", "https://zowie.benq.com/"),
        (["microsoft"], "Microsoft", "Mouse and Keyboard Center", "https://www.microsoft.com/accessories/en-us/products/mouse-keyboard-center"),
        (["wooting"], "Wooting", "Wootility", "https://wooting.io/wootility"),
        (["keychron"], "Keychron", "Keychron Launcher", "https://launcher.keychron.com/"),
    ];

    public static IReadOnlyList<VendorLink> VendorLinksFor(IEnumerable<InputDeviceInfo> devices)
    {
        var links = new List<VendorLink>();
        foreach (var device in devices ?? [])
        {
            var text = $"{device.Name} {device.Manufacturer} {device.Description}".ToLowerInvariant();
            foreach (var (keywords, vendor, software, url) in VendorTable)
            {
                if (keywords.Any(k => text.Contains(k, StringComparison.Ordinal)) && links.All(l => l.Vendor != vendor))
                {
                    links.Add(new VendorLink(vendor, device.Kind, url, $"{software}: buttons, DPI, lighting and firmware for {vendor} devices."));
                }
            }
        }

        if (links.All(l => l.Vendor != "Microsoft"))
        {
            links.Add(new VendorLink("Microsoft", "Keyboard", "https://learn.microsoft.com/windows/powertoys/keyboard-manager", "PowerToys Keyboard Manager: remap keys and shortcuts on any keyboard."));
        }

        return links;
    }
}
