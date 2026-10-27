using System.Text.RegularExpressions;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>PCI and USB ids read from a Windows hardware id.</summary>
public sealed record HardwareIdParts(string Bus, string VendorId, string DeviceId, string? SubsystemId);

/// <summary>The Intel product download a device maps to.</summary>
public enum IntelProduct
{
    None,
    ArcAndIrisXeGraphics,
    LegacyGraphics,
    WiFi,
    Bluetooth,
}

/// <summary>
/// Maps installed devices to what each vendor's lookup needs: the PCI vendor and device id for NVIDIA, the GPU
/// family for AMD, and the product download for Intel graphics, Wi-Fi and Bluetooth. Pure.
/// </summary>
public static partial class DriverDeviceMapper
{
    public const string NvidiaVendorId = "10DE";
    public const string AmdVendorId = "1002";
    public const string IntelVendorId = "8086";

    /// <summary>USB vendor id of Intel's wireless (Bluetooth) parts.</summary>
    public const string IntelUsbVendorId = "8087";

    [GeneratedRegex(@"^(?<bus>PCI|USB|HDAUDIO)\\(?:VEN|VID)_(?<ven>[0-9A-F]{4})&(?:DEV|PID)_(?<dev>[0-9A-F]{4})(?:&SUBSYS_(?<sub>[0-9A-F]{8}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>Reads the bus, vendor and device id from a hardware or instance id; null for other buses.</summary>
    public static HardwareIdParts? Parse(string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId))
        {
            return null;
        }

        var m = IdPattern().Match(hardwareId.Trim());
        if (!m.Success)
        {
            return null;
        }

        return new HardwareIdParts(
            m.Groups["bus"].Value.ToUpperInvariant(),
            m.Groups["ven"].Value.ToUpperInvariant(),
            m.Groups["dev"].Value.ToUpperInvariant(),
            m.Groups["sub"].Success ? m.Groups["sub"].Value.ToUpperInvariant() : null);
    }

    /// <summary>The first parseable id among the instance id and the hardware ids.</summary>
    public static HardwareIdParts? Parse(DriverInfo driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return Parse(driver.DeviceId) ?? driver.HardwareIds.Select(Parse).FirstOrDefault(p => p is not null);
    }

    /// <summary>True for a connected display adapter (Device Manager class "Display").</summary>
    public static bool IsDisplayAdapter(DriverInfo driver) =>
        driver.IsPresent && (string.Equals(driver.DeviceClass, "Display", StringComparison.OrdinalIgnoreCase)
            || string.Equals(driver.ClassGuid, "{4d36e968-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase));

    /// <summary>Connected PCI display adapters from <paramref name="vendorId"/>.</summary>
    public static IEnumerable<DriverInfo> DisplayAdapters(IEnumerable<DriverInfo> drivers, string vendorId) =>
        drivers.Where(d => IsDisplayAdapter(d) && Parse(d) is { Bus: "PCI" } p && p.VendorId == vendorId);

    /// <summary>The device id NVIDIA's lookup expects: "2786_10DE" for PCI\VEN_10DE&amp;DEV_2786.</summary>
    public static string? NvidiaLookupId(DriverInfo driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return Parse(driver) is { Bus: "PCI", VendorId: NvidiaVendorId } p ? p.DeviceId + "_" + p.VendorId : null;
    }

    /// <summary>
    /// The Intel download that covers this device: Arc and Iris Xe (11th gen and newer, including UHD 7xx), the older
    /// 7th–10th gen graphics package, Wi-Fi or Bluetooth. Decided from the class, the bus vendor and the name.
    /// </summary>
    public static IntelProduct IntelProductFor(DriverInfo driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (!driver.IsPresent)
        {
            return IntelProduct.None;
        }

        var ids = Parse(driver);
        var name = driver.DeviceName ?? string.Empty;
        if (IsDisplayAdapter(driver) && ids?.VendorId == IntelVendorId)
        {
            return IsModernIntelGraphics(name) ? IntelProduct.ArcAndIrisXeGraphics
                : IsLegacyIntelGraphics(name) ? IntelProduct.LegacyGraphics
                : IntelProduct.ArcAndIrisXeGraphics;
        }

        if (string.Equals(driver.DeviceClass, "Bluetooth", StringComparison.OrdinalIgnoreCase)
            && (ids?.VendorId == IntelUsbVendorId || Contains(driver.Provider, "Intel")) && Contains(name, "Bluetooth"))
        {
            return IntelProduct.Bluetooth;
        }

        if (string.Equals(driver.DeviceClass, "Net", StringComparison.OrdinalIgnoreCase) && ids?.VendorId == IntelVendorId
            && (Contains(name, "Wi-Fi") || Contains(name, "Wireless") || Contains(name, "WiFi")))
        {
            return IntelProduct.WiFi;
        }

        return IntelProduct.None;
    }

    /// <summary>Arc, Iris Xe and the UHD 7xx parts of 11th gen and later use the current Intel graphics package.</summary>
    public static bool IsModernIntelGraphics(string name) =>
        ArcWord().IsMatch(name) || Contains(name, "Iris(R) Xe") || Contains(name, "Iris Xe")
        || UhdSeven().IsMatch(name) || (Contains(name, "Intel(R) Graphics") && !Contains(name, "HD"));

    [GeneratedRegex(@"\bArc\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArcWord();

    /// <summary>HD 5xx/6xx, UHD 6xx and Iris Plus 6xx (7th–10th gen) use Intel's older graphics package.</summary>
    public static bool IsLegacyIntelGraphics(string name) =>
        LegacyGraphics().IsMatch(name);

    [GeneratedRegex(@"UHD Graphics 7\d\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UhdSeven();

    [GeneratedRegex(@"(?:U?HD Graphics [56]\d\d|Iris(?:\(R\))? Plus Graphics(?: [56]\d\d)?|HD Graphics\s*$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyGraphics();

    /// <summary>
    /// True for a laptop or other mobile PC: Win32_ComputerSystem.PCSystemType 2 (Mobile), or a portable chassis type
    /// (8 Portable, 9 Laptop, 10 Notebook, 14 Sub Notebook, 30 Tablet, 31 Convertible, 32 Detachable).
    /// </summary>
    public static bool IsLaptop(string? pcSystemType, string? chassisType)
    {
        if (int.TryParse(pcSystemType?.Trim(), out int type) && type == 2)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(pcSystemType)
            && (pcSystemType.Contains("Mobile", StringComparison.OrdinalIgnoreCase) || pcSystemType.Contains("Laptop", StringComparison.OrdinalIgnoreCase)
                || pcSystemType.Contains("Slate", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(chassisType))
        {
            return false;
        }

        foreach (var part in chassisType.Split([',', ';', ' ', '{', '}'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out int chassis) && chassis is 8 or 9 or 10 or 14 or 30 or 31 or 32)
            {
                return true;
            }
        }

        return new[] { "Laptop", "Notebook", "Portable", "Convertible", "Detachable", "Tablet" }
            .Any(word => chassisType.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The device reference used to group offers for an installed driver.</summary>
    public static DriverDeviceRef ToDeviceRef(DriverInfo driver) =>
        new(driver.DeviceId, driver.DeviceName, driver.DeviceClass, driver.Version, driver.Date);

    private static bool Contains(string? text, string value) => text is not null && text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
