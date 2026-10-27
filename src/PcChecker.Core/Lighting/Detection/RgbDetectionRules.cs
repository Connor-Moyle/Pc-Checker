using System.Globalization;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Lighting;

/// <summary>A memory module as Windows reports it (Win32_PhysicalMemory).</summary>
public sealed record RgbMemoryModule(string? Slot, string? Manufacturer, string? PartNumber);

/// <summary>A device Windows lists, by its instance ID or hardware IDs, plus the name Windows shows for it.</summary>
public sealed record RgbPnpDevice(string InstanceId, IReadOnlyList<string> HardwareIds, string? FriendlyName);

/// <summary>Raw facts the Windows scan collects; turned into findings by <see cref="RgbDetectionRules.Build"/>.</summary>
/// <param name="Devices">Present USB, HID and PCI devices.</param>
/// <param name="LampArrayDevices">Names (or IDs) of HID devices on the Lighting and Illumination usage page (Windows Dynamic Lighting).</param>
/// <param name="Memory">Installed memory modules.</param>
/// <param name="BoardManufacturer">Motherboard maker (Win32_BaseBoard).</param>
/// <param name="BoardProduct">Motherboard model.</param>
public sealed record RgbScanInput(
    IReadOnlyList<RgbPnpDevice> Devices,
    IReadOnlyList<string> LampArrayDevices,
    IReadOnlyList<RgbMemoryModule> Memory,
    string? BoardManufacturer,
    string? BoardProduct);

/// <summary>
/// Pure detection rules: IDs out of Windows device IDs, the OpenRGB ID tables, RGB memory part numbers and the
/// motherboard brands whose lighting usually sits on the SMBus. Everything here is unit-tested on Linux.
/// </summary>
public static partial class RgbDetectionRules
{
    [GeneratedRegex(@"VID[_&]([0-9A-F]{4,8})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VidPattern();

    [GeneratedRegex(@"PID[_&]([0-9A-F]{4,8})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PidPattern();

    [GeneratedRegex(@"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})&SUBSYS_([0-9A-F]{4})([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PciPattern();

    // RGB memory part numbers (the rules are deliberately narrow; anything else is simply not listed).
    // G.Skill: Trident Z RGB (…GTZR), Trident Z Neo (…GTZN), Trident Z Royal (…GTRG / …GTRS), Trident Z5 RGB (…TZ5R…),
    // Trident Z5 Neo RGB (…TZ5NR…), Trident Z5 CK RGB (…TZ5CR…).
    [GeneratedRegex(@"GTZR|GTZN|GTR[GS]\b|TZ5R|TZ5NR|TZ5CR", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GskillRgb();

    // Corsair: Vengeance RGB (CMR), Vengeance RGB PRO (CMW), Vengeance RGB RS (CMG), Vengeance RGB PRO SL / DDR5 RGB (CMH),
    // Dominator Platinum RGB (CMT).
    [GeneratedRegex(@"^CM[WGHRT]\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CorsairRgb();

    // Kingston FURY Beast / Renegade RGB: the "A" after the series letters marks RGB (KF432C16BBA/8, KF560C36BBEA-16, KF564C32RSA-16).
    [GeneratedRegex(@"^KF\d{3,4}C\d{2}(BB|RB|RS|RW|BW)E?A", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KingstonRgb();

    // HyperX Predator RGB and HyperX FURY RGB (HX432C16PB3A/8, HX436C17FB3AK2/16).
    [GeneratedRegex(@"^HX\d{3}C\d{2}(PB|FB)\dA", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HyperXRgb();

    /// <summary>
    /// Vendor and product ID from a Windows device ID or HID path: "USB\VID_1B1C&amp;PID_0C1C\…",
    /// "\\?\hid#vid_1b1c&amp;pid_0c1c&amp;mi_00#…" or Bluetooth "…_VID&amp;0002046d_PID&amp;b023". False when either is missing.
    /// </summary>
    public static bool TryParseUsbIds(string? deviceId, out ushort vendorId, out ushort productId)
    {
        vendorId = 0;
        productId = 0;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        var vid = VidPattern().Match(deviceId);
        var pid = PidPattern().Match(deviceId);
        return vid.Success && pid.Success && TryLast4(vid.Groups[1].Value, out vendorId) && TryLast4(pid.Groups[1].Value, out productId);
    }

    /// <summary>PCI vendor, device, subsystem vendor and subsystem device from "PCI\VEN_10DE&amp;DEV_2786&amp;SUBSYS_51411458…" (SUBSYS is device then vendor).</summary>
    public static bool TryParsePciIds(string? hardwareId, out (ushort Vendor, ushort Device, ushort SubVendor, ushort SubDevice) ids)
    {
        ids = default;
        if (string.IsNullOrWhiteSpace(hardwareId))
        {
            return false;
        }

        var m = PciPattern().Match(hardwareId);
        if (!m.Success)
        {
            return false;
        }

        ids = (Hex(m.Groups[1].Value), Hex(m.Groups[2].Value), Hex(m.Groups[4].Value), Hex(m.Groups[3].Value));
        return true;
    }

    /// <summary>True when one of the hardware IDs carries the PCI class code of an SMBus controller (0C05).</summary>
    public static bool IsSmbusController(IEnumerable<string> hardwareIds) =>
        hardwareIds.Any(h => h.Contains("CC_0C05", StringComparison.OrdinalIgnoreCase));

    /// <summary>The RGB product line a memory part number belongs to, or null when it is not a known RGB kit.</summary>
    public static string? MemoryRgbLine(string? partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber))
        {
            return null;
        }

        var part = partNumber.Trim();
        if (part.StartsWith("F4-", StringComparison.OrdinalIgnoreCase) || part.StartsWith("F5-", StringComparison.OrdinalIgnoreCase))
        {
            return GskillRgb().IsMatch(part) ? "G.Skill Trident Z RGB" : null;
        }

        if (CorsairRgb().IsMatch(part))
        {
            return "Corsair RGB memory";
        }

        if (KingstonRgb().IsMatch(part))
        {
            return "Kingston FURY RGB";
        }

        if (HyperXRgb().IsMatch(part))
        {
            return "HyperX RGB memory";
        }

        return null;
    }

    /// <summary>Brand from a memory product line ("Kingston FURY RGB" → "Kingston").</summary>
    private static string MemoryBrand(string line) => line.Split(' ')[0];

    /// <summary>
    /// Motherboard brands whose lighting is often on the SMBus or a Super I/O chip that only OpenRGB reaches. Returns the
    /// lighting system's name, or null. A board that already showed a USB lighting controller does not need this hint.
    /// </summary>
    public static string? BoardLightingSystem(string? boardManufacturer)
    {
        if (string.IsNullOrWhiteSpace(boardManufacturer))
        {
            return null;
        }

        var m = boardManufacturer.ToUpperInvariant();
        if (m.Contains("ASROCK"))
        {
            return "ASRock Polychrome RGB";
        }

        if (m.Contains("ASUS"))
        {
            return "ASUS Aura";
        }

        if (m.Contains("GIGABYTE") || m.Contains("AORUS"))
        {
            return "Gigabyte RGB Fusion";
        }

        if (m.Contains("MICRO-STAR") || m.Contains("MSI"))
        {
            return "MSI Mystic Light";
        }

        return null;
    }

    /// <summary>Turns the raw scan facts into the list of RGB hardware, one entry per product (duplicates from several HID interfaces merged).</summary>
    public static IReadOnlyList<RgbHardwareFinding> Build(RgbScanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var found = new Dictionary<string, RgbHardwareFinding>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in input.Devices)
        {
            foreach (var id in device.HardwareIds.Prepend(device.InstanceId))
            {
                if (TryParsePciIds(id, out var pci) && RgbKnownDevices.FindGpu(pci.Vendor, pci.Device, pci.SubVendor, pci.SubDevice) is { } gpu)
                {
                    string key = string.Create(CultureInfo.InvariantCulture, $"gpu:{pci.Vendor:X4}:{pci.Device:X4}:{pci.SubVendor:X4}:{pci.SubDevice:X4}");
                    found.TryAdd(key, new RgbHardwareFinding(key, gpu.Name, gpu.Brand, OpenRgbDeviceTypes.Gpu, RgbFindingSource.GraphicsCard,
                        RgbFindingConfidence.Certain,
                        string.Create(CultureInfo.InvariantCulture, $"Graphics card {pci.Vendor:X4}:{pci.Device:X4}, board {pci.SubVendor:X4}:{pci.SubDevice:X4}"),
                        pci.Vendor, pci.Device));
                    break;
                }

                if (TryParseUsbIds(id, out var vid, out var pid) && RgbKnownDevices.FindUsb(vid, pid) is { } usb)
                {
                    string key = string.Create(CultureInfo.InvariantCulture, $"usb:{vid:X4}:{pid:X4}");
                    var confidence = RgbKnownDevices.IsSharedChipVendor(vid) ? RgbFindingConfidence.Likely : RgbFindingConfidence.Certain;
                    found.TryAdd(key, new RgbHardwareFinding(key, usb.Name, usb.Brand, usb.Type, RgbFindingSource.Usb, confidence,
                        string.Create(CultureInfo.InvariantCulture, $"USB {vid:X4}:{pid:X4}"), vid, pid));
                    break;
                }
            }
        }

        foreach (var lamp in input.LampArrayDevices.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A Dynamic Lighting device already matched by ID keeps its product name.
            if (TryParseUsbIds(lamp, out var vid, out var pid) && found.ContainsKey(string.Create(CultureInfo.InvariantCulture, $"usb:{vid:X4}:{pid:X4}")))
            {
                continue;
            }

            string key = "lamparray:" + lamp.Trim();
            string name = TryParseUsbIds(lamp, out vid, out pid)
                ? string.Create(CultureInfo.InvariantCulture, $"Dynamic Lighting device (USB {vid:X4}:{pid:X4})")
                : lamp.Trim();
            found.TryAdd(key, new RgbHardwareFinding(key, name, "Unknown", OpenRgbDeviceTypes.Light, RgbFindingSource.DynamicLighting,
                RgbFindingConfidence.Certain, "Windows Dynamic Lighting"));
        }

        // One row per memory kit (same part number), listing the slots its sticks sit in.
        var kits = input.Memory
            .Select((module, i) => (Module: module, Slot: string.IsNullOrWhiteSpace(module.Slot) ? $"#{i + 1}" : module.Slot.Trim(), Line: MemoryRgbLine(module.PartNumber)))
            .Where(m => m.Line is not null)
            .GroupBy(m => m.Module.PartNumber!.Trim().ToUpperInvariant());
        foreach (var kit in kits)
        {
            var sticks = kit.ToList();
            string line = sticks[0].Line!;
            string part = sticks[0].Module.PartNumber!.Trim();
            string key = "ram:" + kit.Key;
            string name = sticks.Count == 1 ? $"{line} ({part})" : $"{line} ({part}), {sticks.Count} sticks";
            string slots = string.Join(", ", sticks.Select(s => s.Slot));
            found.TryAdd(key, new RgbHardwareFinding(key, name, MemoryBrand(line), OpenRgbDeviceTypes.Ram,
                RgbFindingSource.Memory, RgbFindingConfidence.Likely, (sticks.Count == 1 ? "Memory slot " : "Memory slots ") + slots));
        }

        if (BoardLightingSystem(input.BoardManufacturer) is { } system)
        {
            string brand = system.Split(' ')[0];
            bool hasUsbBoardController = found.Values.Any(f => f.Source == RgbFindingSource.Usb && f.Type == OpenRgbDeviceTypes.Motherboard
                && string.Equals(f.Vendor, brand, StringComparison.OrdinalIgnoreCase));
            if (!hasUsbBoardController)
            {
                string board = string.IsNullOrWhiteSpace(input.BoardProduct) ? system : $"{system} on {input.BoardProduct.Trim()}";
                found.TryAdd("board", new RgbHardwareFinding("board", board, brand, OpenRgbDeviceTypes.Motherboard, RgbFindingSource.Motherboard,
                    RgbFindingConfidence.Possible, "Motherboard (SMBus or Super I/O chip)"));
            }
        }

        return found.Values
            .OrderBy(f => SourceOrder(f.Source))
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int SourceOrder(RgbFindingSource source) => source switch
    {
        RgbFindingSource.Motherboard => 0,
        RgbFindingSource.Memory => 1,
        RgbFindingSource.GraphicsCard => 2,
        RgbFindingSource.Usb => 3,
        _ => 4,
    };

    private static bool TryLast4(string hex, out ushort value) =>
        ushort.TryParse(hex.Length > 4 ? hex[^4..] : hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static ushort Hex(string hex) => ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
