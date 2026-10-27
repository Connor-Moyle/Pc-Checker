using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PcChecker.Core.Hardware;

/// <summary>
/// Pure lookup tables and decoders for the numeric codes WMI returns (SMBIOS memory types, chassis types, bus types,
/// PnP vendor ids, monitor EDID strings). No Windows dependency so everything here is unit-testable.
/// </summary>
public static partial class HardwareMappings
{
    private static readonly Dictionary<string, string> MonitorVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AUS"] = "ASUS", ["ACI"] = "ASUS", ["DEL"] = "Dell", ["SAM"] = "Samsung", ["GSM"] = "LG", ["LGD"] = "LG Display",
        ["ACR"] = "Acer", ["BNQ"] = "BenQ", ["AOC"] = "AOC", ["HPN"] = "HP", ["HWP"] = "HP", ["LEN"] = "Lenovo",
        ["PHL"] = "Philips", ["MSI"] = "MSI", ["VSC"] = "ViewSonic", ["GIG"] = "Gigabyte", ["GBT"] = "Gigabyte",
        ["AUO"] = "AU Optronics", ["BOE"] = "BOE", ["CMN"] = "Innolux", ["SHP"] = "Sharp", ["SNY"] = "Sony",
        ["NEC"] = "NEC", ["IVM"] = "Iiyama", ["APP"] = "Apple", ["ENC"] = "Eizo", ["XMI"] = "Xiaomi", ["HSD"] = "HannStar",
        ["CTX"] = "CTX", ["EIZ"] = "Eizo", ["MST"] = "MStar", ["SEC"] = "Samsung", ["LPL"] = "LG Philips", ["ALI"] = "Alienware",
        ["HUA"] = "Huawei", ["HEC"] = "Hisense", ["TCL"] = "TCL", ["RZR"] = "Razer", ["CPQ"] = "Compaq", ["IBM"] = "IBM",
    };

    private static readonly Dictionary<string, string> MemoryVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["04CD"] = "G.Skill", ["029E"] = "Corsair", ["0215"] = "Corsair", ["859B"] = "Crucial", ["802C"] = "Micron",
        ["80AD"] = "SK Hynix", ["80CE"] = "Samsung", ["04CB"] = "ADATA", ["0498"] = "Kingston", ["0198"] = "Kingston",
        ["01F7"] = "Transcend", ["8551"] = "Qimonda", ["04CE"] = "Team Group", ["0B0B"] = "Patriot", ["8AC2"] = "Patriot",
        ["0B5A"] = "OLOy", ["04B0"] = "PNY", ["7F7F7F7F7F9B"] = "Crucial", ["0197"] = "Silicon Power", ["8ACE"] = "Lexar",
        ["Kingston"] = "Kingston", ["Micron"] = "Micron", ["Samsung"] = "Samsung", ["Hynix"] = "SK Hynix",
    };

    /// <summary>Win32_PhysicalMemory.SMBIOSMemoryType (preferred) or legacy MemoryType to "DDR5" etc.</summary>
    public static string? MemoryTypeName(int? smbiosMemoryType, int? legacyMemoryType = null)
    {
        var fromSmbios = smbiosMemoryType switch
        {
            0x12 => "DDR", 0x13 => "DDR2", 0x14 => "DDR2 FB-DIMM", 0x18 => "DDR3", 0x19 => "FBD2", 0x1A => "DDR4",
            0x1B => "LPDDR", 0x1C => "LPDDR2", 0x1D => "LPDDR3", 0x1E => "LPDDR4", 0x1F => "Logical non-volatile",
            0x20 => "HBM", 0x21 => "HBM2", 0x22 => "DDR5", 0x23 => "LPDDR5", 0x24 => "HBM3", 0x0F => "SDRAM",
            0x11 => "RDRAM", 0x03 => "DRAM", 0x07 => "RAM", _ => null,
        };
        if (fromSmbios is not null)
        {
            return fromSmbios;
        }

        return legacyMemoryType switch
        {
            20 => "DDR", 21 => "DDR2", 22 => "DDR2 FB-DIMM", 24 => "DDR3", 25 => "FBD2", 26 => "DDR4",
            27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5",
            2 => "DRAM", 9 => "SDRAM", 0 or null => null, _ => $"Type {legacyMemoryType}",
        };
    }

    /// <summary>Win32_PhysicalMemory.FormFactor to "DIMM"/"SODIMM"/…</summary>
    public static string? MemoryFormFactorName(int? formFactor) => formFactor switch
    {
        null or 0 => null, 1 => "Other", 2 => "SIP", 3 => "DIP", 4 => "ZIP", 5 => "SOJ", 6 => "Proprietary", 7 => "SIMM",
        8 => "DIMM", 9 => "TSOP", 10 => "PGA", 11 => "RIMM", 12 => "SODIMM", 13 => "SRIMM", 14 => "SMD", 15 => "SSMP",
        16 => "QFP", 17 => "TQFP", 18 => "SOIC", 19 => "LCC", 20 => "PLCC", 21 => "BGA", 22 => "FPBGA", 23 => "LGA",
        _ => $"Form factor {formFactor}",
    };

    /// <summary>Turns a JEDEC id like "04CD" or an already readable vendor into a friendly name.</summary>
    public static string? MemoryManufacturerName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (MemoryVendors.TryGetValue(trimmed, out var name))
        {
            return name;
        }

        // Long JEDEC continuation strings ("7F7F7F7F7F9B0000") carry the real id in the last non-7F byte pair.
        var hex = trimmed.TrimEnd('0');
        if (hex.Length >= 4 && hex.All(Uri.IsHexDigit))
        {
            var last = hex.Replace("7F", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (last.Length >= 2 && MemoryVendors.TryGetValue(last.Length >= 4 ? last[..4] : "00" + last, out var byTail))
            {
                return byTail;
            }
        }

        return trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("Undefined", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    /// <summary>Win32_SystemEnclosure.ChassisTypes to a short description. Uses the first recognised value.</summary>
    public static string ChassisTypeName(IEnumerable<int>? chassisTypes)
    {
        if (chassisTypes is null)
        {
            return "Unknown";
        }

        foreach (var type in chassisTypes)
        {
            var name = type switch
            {
                3 => "Desktop", 4 => "Low profile desktop", 5 => "Pizza box", 6 => "Mini tower", 7 => "Tower", 8 => "Portable",
                9 => "Laptop", 10 => "Notebook", 11 => "Hand held", 12 => "Docking station", 13 => "All-in-one", 14 => "Sub notebook",
                15 => "Space-saving desktop", 16 => "Lunch box", 17 => "Main server chassis", 18 => "Expansion chassis",
                19 => "Sub chassis", 20 => "Bus expansion chassis", 21 => "Peripheral chassis", 22 => "Storage chassis",
                23 => "Rack mount chassis", 24 => "Sealed-case PC", 25 => "Multi-system chassis", 26 => "Compact PCI",
                27 => "Advanced TCA", 28 => "Blade", 29 => "Blade enclosure", 30 => "Tablet", 31 => "Convertible", 32 => "Detachable",
                33 => "IoT gateway", 34 => "Embedded PC", 35 => "Mini PC", 36 => "Stick PC", _ => null,
            };
            if (name is not null)
            {
                return name;
            }
        }

        return "Unknown";
    }

    /// <summary>True for chassis types that run on a battery (laptops, tablets, convertibles).</summary>
    public static bool IsPortableChassis(IEnumerable<int>? chassisTypes) =>
        chassisTypes is not null && chassisTypes.Any(t => t is 8 or 9 or 10 or 11 or 14 or 30 or 31 or 32);

    /// <summary>Win32_ComputerSystem.PCSystemType to text.</summary>
    public static string? PcSystemTypeName(int? type) => type switch
    {
        null => null, 0 => "Unspecified", 1 => "Desktop", 2 => "Mobile", 3 => "Workstation", 4 => "Enterprise server",
        5 => "Small office server", 6 => "Appliance PC", 7 => "Performance server", 8 => "Slate / tablet", _ => $"Type {type}",
    };

    /// <summary>Win32_Processor.Architecture to text.</summary>
    public static string? ProcessorArchitectureName(int? architecture) => architecture switch
    {
        null => null, 0 => "x86", 1 => "MIPS", 2 => "Alpha", 3 => "PowerPC", 5 => "ARM", 6 => "Itanium", 9 => "x64", 12 => "ARM64", _ => $"Architecture {architecture}",
    };

    /// <summary>GPU vendor from a PnP id such as "PCI\VEN_10DE&amp;DEV_2786…".</summary>
    public static string GpuVendorFromPnpId(string? pnpDeviceId, string? adapterCompatibility = null)
    {
        var ven = ExtractPnpVendorId(pnpDeviceId);
        var byId = ven switch
        {
            "10DE" => "NVIDIA", "1002" => "AMD", "1022" => "AMD", "8086" => "Intel", "15AD" => "VMware", "1414" => "Microsoft",
            "5333" => "S3", "1AF4" => "Virtio", "80EE" => "Oracle VirtualBox", "1234" => "QEMU", "102B" => "Matrox", "1A03" => "ASPEED",
            _ => null,
        };
        if (byId is not null)
        {
            return byId;
        }

        return VendorFromText(adapterCompatibility) ?? "Unknown";
    }

    /// <summary>Normalises vendor text ("Advanced Micro Devices, Inc." → "AMD").</summary>
    public static string? VendorFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        if (t.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (t.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase) || t.StartsWith("AMD", StringComparison.OrdinalIgnoreCase) || t.Contains("ATI ", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (t.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        if (t.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Microsoft";
        return t;
    }

    /// <summary>The 4-hex-digit vendor id from a PCI/USB PnP id, upper-case, or null.</summary>
    public static string? ExtractPnpVendorId(string? pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId))
        {
            return null;
        }

        var m = VendorIdRegex().Match(pnpDeviceId);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"^(?:AMD\s+)?Radeon(?:\(TM\)|\u2122)?\s*(?:(?:\d{3}M|\d{4}S)(?:\s+Graphics)?|Graphics)$", RegexOptions.IgnoreCase)]
    private static partial Regex AmdIntegratedName();

    [GeneratedRegex(@"\bVega\s+(?<n>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex VegaCompute();

    /// <summary>
    /// The one rule for integrated graphics, shared by the inventory, the Overclock page and the sensor pickers.
    /// Integrated graphics share system memory and carry typical names: Intel UHD/Iris/"Arc Graphics", AMD
    /// "Radeon(TM) Graphics", "Radeon 780M"/"890M"/"8060S" and the Vega 3–11 APUs. A big UMA frame buffer does not
    /// make an APU a graphics card, so the name decides, not the memory size.
    /// </summary>
    public static bool IsIntegratedGpu(string? name, string vendor)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var n = name.Trim();
        if (vendor == "Intel" || (vendor.Length == 0 && n.Contains("Intel", StringComparison.OrdinalIgnoreCase)))
        {
            return !n.Contains("Arc", StringComparison.OrdinalIgnoreCase) || n.Contains("Arc Graphics", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Arc(TM) Graphics", StringComparison.OrdinalIgnoreCase);
        }

        if (vendor == "AMD" || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            if (AmdIntegratedName().IsMatch(n))
            {
                return true;
            }

            var vega = VegaCompute().Match(n);
            return vega.Success && int.Parse(vega.Groups["n"].Value, CultureInfo.InvariantCulture) <= 11;
        }

        return n.Contains("Integrated", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Chooses the best VRAM figure. Win32_VideoController.AdapterRAM is a 32-bit value that caps at 4 GB, so the
    /// registry QWORD (HardwareInformation.qwMemorySize) wins when it is present and plausible.
    /// </summary>
    public static long? ResolveVramBytes(long? adapterRam, long? registryQwordBytes)
    {
        const long OneMb = 1024L * 1024;
        if (registryQwordBytes is > OneMb)
        {
            return registryQwordBytes;
        }

        if (adapterRam is > 0)
        {
            return adapterRam;
        }

        return null;
    }

    /// <summary>True when a registry MatchingDeviceId ("pci\ven_10de&amp;dev_2786&amp;subsys_…") refers to the same adapter as a PnP id.</summary>
    public static bool PnpIdMatches(string? pnpDeviceId, string? matchingDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId) || string.IsNullOrWhiteSpace(matchingDeviceId))
        {
            return false;
        }

        var a = NormalizePnp(pnpDeviceId);
        var b = NormalizePnp(matchingDeviceId);
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);

        static string NormalizePnp(string id)
        {
            // Keep "PCI\VEN_xxxx&DEV_xxxx&SUBSYS_xxxxxxxx" and drop the revision and instance suffixes.
            var m = PnpCoreRegex().Match(id.Trim());
            return m.Success ? m.Value.ToUpperInvariant() : id.Trim().ToUpperInvariant();
        }
    }

    /// <summary>Decodes the ushort[] strings WmiMonitorID returns (null-terminated UTF-16 code units).</summary>
    public static string? DecodeMonitorString(IEnumerable<object?>? codeUnits)
    {
        if (codeUnits is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var unit in codeUnits)
        {
            int code = unit switch
            {
                ushort us => us,
                short s => s,
                int i => i,
                uint ui => (int)ui,
                byte b => b,
                char c => c,
                string str when int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0,
            };
            if (code == 0)
            {
                break;
            }

            if (code is >= 32 and < 0xFFFE)
            {
                sb.Append((char)code);
            }
        }

        var text = sb.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>The three-letter EDID manufacturer id ("AUS") to a brand ("ASUS"); unknown codes pass through.</summary>
    public static string? MonitorManufacturerName(string? edidCode)
    {
        if (string.IsNullOrWhiteSpace(edidCode))
        {
            return null;
        }

        var code = edidCode.Trim();
        return MonitorVendors.TryGetValue(code, out var name) ? name : code;
    }

    /// <summary>Diagonal in inches from the EDID image size in centimetres; null when either side is missing.</summary>
    public static double? DiagonalInches(int? widthCm, int? heightCm)
    {
        if (widthCm is not > 0 || heightCm is not > 0)
        {
            return null;
        }

        var cm = Math.Sqrt((double)widthCm.Value * widthCm.Value + (double)heightCm.Value * heightCm.Value);
        return Math.Round(cm / 2.54, 1);
    }

    /// <summary>MSFT_PhysicalDisk.MediaType to "HDD"/"SSD"/"SCM".</summary>
    public static string? StorageMediaTypeName(int? mediaType) => mediaType switch
    {
        3 => "HDD", 4 => "SSD", 5 => "SCM", 0 => "Unspecified", null => null, _ => $"Media {mediaType}",
    };

    /// <summary>MSFT_PhysicalDisk.BusType to "NVMe"/"SATA"/"USB"…</summary>
    public static string? StorageBusTypeName(int? busType) => busType switch
    {
        null => null, 0 => "Unknown", 1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel",
        7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual",
        15 => "File-backed virtual", 16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => $"Bus {busType}",
    };

    /// <summary>MSFT_PhysicalDisk.HealthStatus to text.</summary>
    public static string? StorageHealthName(int? health) => health switch
    {
        0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", 5 => "Unknown", null => null, _ => $"Status {health}",
    };

    /// <summary>Guess HDD/SSD from Win32_DiskDrive data when MSFT_PhysicalDisk is unavailable.</summary>
    public static string? GuessMediaType(string? model, string? wmiMediaType, string? pnpDeviceId)
    {
        var text = $"{model} {pnpDeviceId}";
        if (text.Contains("NVME", StringComparison.OrdinalIgnoreCase) || text.Contains("SSD", StringComparison.OrdinalIgnoreCase) || text.Contains("Solid State", StringComparison.OrdinalIgnoreCase))
        {
            return "SSD";
        }

        if (wmiMediaType is not null && wmiMediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase))
        {
            return "Removable";
        }

        return wmiMediaType is null ? null : "HDD";
    }

    /// <summary>Win32_LogicalDisk.DriveType to text.</summary>
    public static string DriveTypeName(int? driveType) => driveType switch
    {
        2 => "Removable disk", 3 => "Local disk", 4 => "Network drive", 5 => "CD/DVD drive", 6 => "RAM disk", 1 => "No root directory", _ => "Unknown",
    };

    /// <summary>Win32_NetworkAdapter.NetConnectionStatus to text.</summary>
    public static string? NetConnectionStatusName(int? status) => status switch
    {
        null => null, 0 => "Disconnected", 1 => "Connecting", 2 => "Connected", 3 => "Disconnecting", 4 => "Hardware not present",
        5 => "Hardware disabled", 6 => "Hardware malfunction", 7 => "Media disconnected", 8 => "Authenticating",
        9 => "Authentication succeeded", 10 => "Authentication failed", 11 => "Invalid address", 12 => "Credentials required", _ => $"Status {status}",
    };

    /// <summary>Classifies a network adapter as Wi-Fi, Ethernet, Bluetooth, Virtual or Other from its name and ids.</summary>
    public static string NetworkAdapterKind(string? name, string? pnpDeviceId, string? adapterType = null, string? netConnectionId = null)
    {
        var text = $"{name} {netConnectionId}";
        var pnp = pnpDeviceId ?? string.Empty;
        if (text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
        {
            return "Bluetooth";
        }

        if (pnp.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase)
            || pnp.StartsWith("COMPOSITEBUS\\", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || text.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) || text.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TAP-", StringComparison.OrdinalIgnoreCase) || text.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Loopback", StringComparison.OrdinalIgnoreCase) || text.Contains("Miniport", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Tunnel", StringComparison.OrdinalIgnoreCase) || text.Contains("vEthernet", StringComparison.OrdinalIgnoreCase))
        {
            return "Virtual";
        }

        if (text.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) || text.Contains("WiFi", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Wireless", StringComparison.OrdinalIgnoreCase) || text.Contains("802.11", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WLAN", StringComparison.OrdinalIgnoreCase) || (adapterType?.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return "Wi-Fi";
        }

        if (text.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) || text.Contains("GbE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Gigabit", StringComparison.OrdinalIgnoreCase) || text.Contains("LAN", StringComparison.OrdinalIgnoreCase)
            || pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) || (adapterType?.Contains("802.3", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return "Ethernet";
        }

        return pnp.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ? "USB adapter" : "Other";
    }

    /// <summary>Win32_Battery.Chemistry to text.</summary>
    public static string? BatteryChemistryName(int? chemistry) => chemistry switch
    {
        null => null, 1 => "Other", 2 => "Unknown", 3 => "Lead acid", 4 => "Nickel cadmium", 5 => "Nickel metal hydride",
        6 => "Lithium-ion", 7 => "Zinc air", 8 => "Lithium polymer", _ => $"Chemistry {chemistry}",
    };

    /// <summary>Win32_Battery.BatteryStatus to text.</summary>
    public static string? BatteryStatusName(int? status) => status switch
    {
        null => null, 1 => "Discharging", 2 => "On AC power", 3 => "Fully charged", 4 => "Low", 5 => "Critical", 6 => "Charging",
        7 => "Charging (high)", 8 => "Charging (low)", 9 => "Charging (critical)", 10 => "Undefined", 11 => "Partially charged", _ => $"Status {status}",
    };

    /// <summary>Battery wear from design and full-charge capacity (0 when healthy, 100 when dead); null when unknown.</summary>
    public static double? BatteryWearPercent(int? designMwh, int? fullChargeMwh)
    {
        if (designMwh is not > 0 || fullChargeMwh is not >= 0)
        {
            return null;
        }

        var wear = 100.0 * (designMwh.Value - fullChargeMwh.Value) / designMwh.Value;
        return Math.Round(Math.Clamp(wear, 0, 100), 1);
    }

    /// <summary>Hex text for the microcode "Update Revision" registry blob (revision lives in bytes 4..7, little-endian).</summary>
    public static string? MicrocodeRevisionHex(byte[]? blob)
    {
        if (blob is null || blob.Length == 0)
        {
            return null;
        }

        var slice = blob.Length >= 8 ? blob[4..8] : blob;
        Array.Reverse(slice);
        var hex = Convert.ToHexString(slice).TrimStart('0');
        return hex.Length == 0 ? "0" : "0x" + hex;
    }

    /// <summary>Guesses the chipset from a motherboard product name ("ROG STRIX Z790-E" → "Intel Z790").</summary>
    public static string? GuessChipset(string? product, string? cpuManufacturer = null)
    {
        if (string.IsNullOrWhiteSpace(product))
        {
            return null;
        }

        var m = ChipsetRegex().Match(product);
        if (!m.Success)
        {
            return null;
        }

        var code = m.Groups[1].Value.ToUpperInvariant();
        bool amd = code.StartsWith("TRX", StringComparison.Ordinal) || code.StartsWith("WRX", StringComparison.Ordinal) || code[0] == 'A'
            || code[0] == 'X' && code is not ("X299" or "X99" or "X79" or "X58")
            || code[0] == 'B' && code.EndsWith("50", StringComparison.Ordinal);
        bool intel = !amd;
        if (cpuManufacturer is not null)
        {
            if (cpuManufacturer.Contains("AMD", StringComparison.OrdinalIgnoreCase) || cpuManufacturer.Contains("Authentic", StringComparison.OrdinalIgnoreCase))
            {
                amd = true;
                intel = false;
            }
            else if (cpuManufacturer.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            {
                amd = false;
                intel = true;
            }
        }

        if (amd && !intel)
        {
            return "AMD " + code;
        }

        if (intel && !amd)
        {
            return "Intel " + code;
        }

        return code;
    }

    /// <summary>Extracts the key value from a WMI object path ("\\\\.\\root\\cimv2:Win32_LogicalDisk.DeviceID=\"C:\"" → "C:").</summary>
    public static string? ExtractWmiKey(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var m = WmiKeyRegex().Match(reference);
        if (!m.Success)
        {
            return null;
        }

        return m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
    }

    [GeneratedRegex(@"VEN_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VendorIdRegex();

    [GeneratedRegex(@"(?:PCI|USB|HDAUDIO)\\VEN_[0-9A-Fa-f]{4}&DEV_[0-9A-Fa-f]{4}(?:&SUBSYS_[0-9A-Fa-f]{8})?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PnpCoreRegex();

    [GeneratedRegex(@"\b((?:TRX|WRX|[XZBHAQW])\d{2,3}E?)(?:M|I)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChipsetRegex();

    [GeneratedRegex("=\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex WmiKeyRegex();
}
