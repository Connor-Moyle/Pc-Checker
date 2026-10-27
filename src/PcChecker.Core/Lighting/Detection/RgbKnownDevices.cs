using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Lighting;

/// <summary>An RGB product OpenRGB recognises by USB/HID ID.</summary>
public sealed record RgbKnownUsbDevice(ushort VendorId, ushort ProductId, string Name, string Family)
{
    public string Brand => RgbKnownDevices.BrandOf(Name, VendorId);
    public string Type => RgbKnownDevices.GuessType(Name, Family);
}

/// <summary>A graphics card with RGB that OpenRGB recognises by PCI and subsystem ID.</summary>
public sealed record RgbKnownGpu(ushort VendorId, ushort DeviceId, ushort SubsystemVendorId, ushort SubsystemDeviceId, string Name, string Family)
{
    public string Brand => RgbKnownDevices.BrandOf(Name, SubsystemVendorId);
}

/// <summary>
/// The ID lists in <c>Lighting/Data</c> (built by <c>build/update-rgb-ids.py</c> from OpenRGB's device detectors) and the
/// pure rules around them: which brand and type an entry is, and which vendor IDs are shared chip-maker IDs.
/// </summary>
public static partial class RgbKnownDevices
{
    // Product lines whose names do not say "keyboard", "mouse" or "headset" (checked on lower-case text).
    [GeneratedRegex(@"\b(corsair k\d{2,3}|k\d{2} rgb|blackwidow|huntsman|ornata|cynosa|deathstalker|apex (pro|\d)|alloy|masterkeys|vulcan|wooting|falchion|azoth|claymore|strix flare|strix scope|g213|g413|g512|g513|g810|g815|g910|g915|sk6\d\d|ck5\d\d|mk7\d\d|mk8\d\d)", RegexOptions.CultureInvariant)]
    private static partial Regex KeyboardModel();

    [GeneratedRegex(@"\b(harpoon|dark core|glaive|ironclaw|scimitar|nightsword|m65|sabre|katar|deathadder|basilisk|viper (mini|ultimate|v\d)|naga|mamba|lancehead|pulsefire|rival|sensei|aerox|gladius|chakram|pugio|spatha|keris|kone|burst|model [od]|g502|g903|g703|g403|g303|g203|g102|mm5\d\d|mm7\d\d)\b", RegexOptions.CultureInvariant)]
    private static partial Regex MouseModel();

    [GeneratedRegex(@"\b(virtuoso|void|arctis|blackshark|nari|hs\d{2}|cloud (ii|alpha|revolver|flight|orbit))\b", RegexOptions.CultureInvariant)]
    private static partial Regex HeadsetModel();

    private static readonly Lazy<IReadOnlyDictionary<uint, RgbKnownUsbDevice>> Usb = new(LoadUsb);
    private static readonly Lazy<IReadOnlyDictionary<(ushort, ushort, ushort, ushort), RgbKnownGpu>> Gpus = new(LoadGpus);

    /// <summary>
    /// Vendor IDs owned by chip makers whose controllers many brands build on (STMicro, Holtek, Sinowealth, Microdia,
    /// Nuvoton, QinHeng, Atmel, pid.codes, V-USB, Arduino, China Resource Semico). A match on one of these is "likely",
    /// because an unrelated gadget can reuse the same product ID.
    /// </summary>
    public static IReadOnlySet<ushort> SharedChipVendorIds { get; } = new HashSet<ushort>
    {
        0x0483, 0x04D9, 0x258A, 0x0C45, 0x0416, 0x1A86, 0x03EB, 0x1209, 0x16C0, 0x2341, 0x1A2C,
    };

    /// <summary>Brand names for vendor IDs whose product names do not start with the brand.</summary>
    private static readonly Dictionary<ushort, string> BrandByVendorId = new()
    {
        [0x0951] = "HyperX",
        [0x03F0] = "HyperX",
        [0x0CF2] = "Lian Li",
        [0x1B1C] = "Corsair",
        [0x0B05] = "ASUS",
        [0x1462] = "MSI",
        [0x1E71] = "NZXT",
        [0x1532] = "Razer",
        [0x046D] = "Logitech",
        [0x1038] = "SteelSeries",
        [0x2516] = "Cooler Master",
        [0x264A] = "Thermaltake",
        [0x26CE] = "ASRock",
    };

    private static readonly string[] TwoWordBrands =
    [
        "Lian Li", "Cooler Master", "Red Square", "Das Keyboard", "Mad Catz", "Turtle Beach", "Dark Project", "Sony DualSense",
    ];

    /// <summary>Number of USB/HID entries (for the troubleshooting text and tests).</summary>
    public static int UsbCount => Usb.Value.Count;

    /// <summary>Number of graphics card entries.</summary>
    public static int GpuCount => Gpus.Value.Count;

    /// <summary>The OpenRGB-known product with this exact vendor and product ID, or null.</summary>
    public static RgbKnownUsbDevice? FindUsb(ushort vendorId, ushort productId) =>
        Usb.Value.TryGetValue(((uint)vendorId << 16) | productId, out var hit) ? hit : null;

    /// <summary>The OpenRGB-known graphics card with these exact PCI IDs, or null.</summary>
    public static RgbKnownGpu? FindGpu(ushort vendorId, ushort deviceId, ushort subsystemVendorId, ushort subsystemDeviceId) =>
        Gpus.Value.TryGetValue((vendorId, deviceId, subsystemVendorId, subsystemDeviceId), out var hit) ? hit : null;

    /// <summary>True when the vendor ID belongs to a chip maker other products share (a match is "likely", not certain).</summary>
    public static bool IsSharedChipVendor(ushort vendorId) => SharedChipVendorIds.Contains(vendorId);

    /// <summary>"Corsair Commander Core" → "Corsair"; HyperX and Lian Li IDs map to their brand.</summary>
    public static string BrandOf(string? productName, ushort vendorId)
    {
        if (BrandByVendorId.TryGetValue(vendorId, out var known))
        {
            return known;
        }

        if (string.IsNullOrWhiteSpace(productName))
        {
            return "Unknown";
        }

        var name = productName.Trim();
        foreach (var brand in TwoWordBrands)
        {
            if (name.StartsWith(brand, StringComparison.OrdinalIgnoreCase))
            {
                return brand.Split(' ')[0] == "Sony" ? "Sony" : brand;
            }
        }

        return name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    /// <summary>A friendly device type from the product name and OpenRGB's controller family.</summary>
    public static string GuessType(string? productName, string? family)
    {
        string text = ((productName ?? string.Empty) + " " + (family ?? string.Empty)).ToLowerInvariant();
        bool Has(params string[] words) => words.Any(text.Contains);

        if (Has("mousemat", "mouse mat", "mousepad", "mouse pad", "balteus", "polaris", "goliathus", "firefly", "qck", "mm700", "mm800"))
        {
            return OpenRgbDeviceTypes.MousePad;
        }

        if (Has("headset stand", "headsetstand"))
        {
            return OpenRgbDeviceTypes.HeadsetStand;
        }

        if (Has("keyboard", "keypad", "keys ") || KeyboardModel().IsMatch(text))
        {
            return OpenRgbDeviceTypes.Keyboard;
        }

        if (Has("mouse") || MouseModel().IsMatch(text))
        {
            return OpenRgbDeviceTypes.Mouse;
        }

        // Razer's Kraken is a headset; NZXT's Kraken (below) is a cooler.
        if (Has("headset", "headphone") || HeadsetModel().IsMatch(text) || (text.StartsWith("razer", StringComparison.Ordinal) && Has("kraken")))
        {
            return OpenRgbDeviceTypes.Headset;
        }

        if (Has("microphone", "quadcast", "duocast"))
        {
            return Microphone;
        }

        if (Has("gamepad", "controller (", "dualsense", "dualshock"))
        {
            return OpenRgbDeviceTypes.Gamepad;
        }

        if (Has("kraken", "hydro", "capellix", "cooler", "aio", "wraith", "waterforce", "ryuo", "h100i", "h115i", "h150i", "pump"))
        {
            return OpenRgbDeviceTypes.Cooler;
        }

        if (Has("motherboard", "mystic light", "aura addressable", "rgb fusion", "polychrome", "aura core"))
        {
            return OpenRgbDeviceTypes.Motherboard;
        }

        if (Has("monitor", "display"))
        {
            return Monitor;
        }

        if (Has("strip", "strimer", "light bar", "lightbar"))
        {
            return OpenRgbDeviceTypes.LedStrip;
        }

        if (Has("commander", "lighting node", "hub", "smart device", "fan controller", "riing", "icue link", "nollie", "fan "))
        {
            return LightingController;
        }

        if (Has("speaker", "soundbar"))
        {
            return OpenRgbDeviceTypes.Speaker;
        }

        if (Has("laptop", "legion", "clevo", "omen", "alienware", "notebook"))
        {
            return Laptop;
        }

        return OpenRgbDeviceTypes.Unknown;
    }

    /// <summary>Type words used only by the detection pass (OpenRGB has no such device types).</summary>
    public const string Microphone = "Microphone";

    public const string Monitor = "Monitor";
    public const string LightingController = "Fan and LED controller";
    public const string Laptop = "Laptop lighting";

    /// <summary>Parses the USB table text (tab-separated: vendor, product, name, family; '#' starts a comment).</summary>
    public static IReadOnlyDictionary<uint, RgbKnownUsbDevice> ParseUsbTable(IEnumerable<string> lines)
    {
        var map = new Dictionary<uint, RgbKnownUsbDevice>();
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('#'))
            {
                continue;
            }

            var parts = raw.Split('\t');
            if (parts.Length < 3 || !TryHex(parts[0], out var vid) || !TryHex(parts[1], out var pid) || string.IsNullOrWhiteSpace(parts[2]))
            {
                continue;
            }

            map.TryAdd(((uint)vid << 16) | pid, new RgbKnownUsbDevice(vid, pid, parts[2].Trim(), parts.Length > 3 ? parts[3].Trim() : string.Empty));
        }

        return map;
    }

    /// <summary>Parses the graphics card table text (tab-separated: vendor, device, subsystem vendor, subsystem device, name, family).</summary>
    public static IReadOnlyDictionary<(ushort, ushort, ushort, ushort), RgbKnownGpu> ParseGpuTable(IEnumerable<string> lines)
    {
        var map = new Dictionary<(ushort, ushort, ushort, ushort), RgbKnownGpu>();
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('#'))
            {
                continue;
            }

            var p = raw.Split('\t');
            if (p.Length < 5 || !TryHex(p[0], out var ven) || !TryHex(p[1], out var dev) || !TryHex(p[2], out var sven) || !TryHex(p[3], out var sdev)
                || string.IsNullOrWhiteSpace(p[4]))
            {
                continue;
            }

            map.TryAdd((ven, dev, sven, sdev), new RgbKnownGpu(ven, dev, sven, sdev, p[4].Trim(), p.Length > 5 ? p[5].Trim() : string.Empty));
        }

        return map;
    }

    private static bool TryHex(string text, out ushort value) =>
        ushort.TryParse(text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static IReadOnlyDictionary<uint, RgbKnownUsbDevice> LoadUsb() => ParseUsbTable(ReadResource("rgb-usb-ids.tsv"));

    private static IReadOnlyDictionary<(ushort, ushort, ushort, ushort), RgbKnownGpu> LoadGpus() => ParseGpuTable(ReadResource("rgb-gpu-ids.tsv"));

    private static IEnumerable<string> ReadResource(string fileName)
    {
        var lines = new List<string>();
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PcChecker.Core.Lighting.Data." + fileName);
            if (stream is null)
            {
                return lines;
            }

            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }
        catch (IOException)
        {
            // A damaged resource leaves the table empty; detection then simply finds nothing by ID.
        }

        return lines;
    }
}
