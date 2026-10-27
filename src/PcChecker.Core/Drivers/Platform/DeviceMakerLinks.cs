using System.Text.RegularExpressions;
using PcChecker.Core.Drivers.Sources;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>Which maker's page is right for a network, audio or Bluetooth device.</summary>
public enum DeviceMakerKind
{
    None,
    IntelEthernet,
    Killer,
    RealtekEthernet,

    /// <summary>MediaTek, Qualcomm, Realtek or AMD (RZ6xx) Wi-Fi: the PC or board maker's page.</summary>
    WiFiFromPcMaker,

    /// <summary>Realtek, MediaTek or Qualcomm Bluetooth: the PC or board maker's page.</summary>
    BluetoothFromPcMaker,

    /// <summary>Realtek HD Audio or USB audio, Intel Smart Sound: the board or PC maker's page, never a generic Realtek driver.</summary>
    AudioFromPcMaker,
}

/// <summary>
/// Network, audio and Bluetooth devices and the page their driver should come from. Intel Ethernet and Killer
/// (Intel) have Intel download pages; Realtek Ethernet has Realtek's download page; Wi-Fi and Bluetooth from
/// MediaTek, Qualcomm and Realtek, and onboard audio, come from the PC or motherboard maker, whose drivers carry
/// their own settings and tuning. None of these makers publishes a version feed, so every offer is a link with the
/// installed version in its note. Intel Wi-Fi and Bluetooth are left to <see cref="IntelDriverProvider"/>. Pure.
/// </summary>
public static partial class DeviceMakerLinks
{
    public const string IntelEthernetPage = "https://www.intel.com/content/www/us/en/download/727998/intel-network-adapter-driver-for-microsoft-windows-11.html";
    public const string KillerPage = "https://www.intel.com/content/www/us/en/download/19779/intel-killer-performance-suite.html";
    public const string RealtekEthernetPage = "https://www.realtek.com/Download/List?cate_id=584";

    private const string RealtekPci = "10EC";
    private const string RealtekUsb = "0BDA";
    private static readonly string[] MediaTekIds = ["14C3", "0E8D"];
    private static readonly string[] QualcommIds = ["17CB", "168C", "0CF3"];

    /// <summary>The kind of page for a connected device; <see cref="DeviceMakerKind.None"/> for everything else.</summary>
    public static DeviceMakerKind KindOf(DriverInfo driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (!driver.IsPresent)
        {
            return DeviceMakerKind.None;
        }

        var ids = DriverDeviceMapper.Parse(driver) ?? HdAudioCodec(driver);
        var name = driver.DeviceName ?? string.Empty;
        var vendor = ids?.VendorId;
        bool net = Is(driver.DeviceClass, "Net");
        bool wireless = Has(name, "Wi-Fi") || Has(name, "WiFi") || Has(name, "Wireless") || Has(name, "WLAN") || Has(name, "802.11");

        if (net && Has(name, "Killer"))
        {
            return DeviceMakerKind.Killer;
        }

        if (net && !wireless && vendor == DriverDeviceMapper.IntelVendorId && Has(name, "Ethernet"))
        {
            return DeviceMakerKind.IntelEthernet;
        }

        if (net && !wireless && vendor == RealtekPci && (Has(name, "Ethernet") || Has(name, "GbE") || Has(name, "Family Controller")))
        {
            return DeviceMakerKind.RealtekEthernet;
        }

        if (net && wireless && vendor is not null && (vendor == RealtekPci || vendor == RealtekUsb || MediaTekIds.Contains(vendor) || QualcommIds.Contains(vendor)))
        {
            return DeviceMakerKind.WiFiFromPcMaker;
        }

        if (Is(driver.DeviceClass, "Bluetooth") && vendor is not null && (vendor == RealtekUsb || MediaTekIds.Contains(vendor) || QualcommIds.Contains(vendor)))
        {
            return DeviceMakerKind.BluetoothFromPcMaker;
        }

        if (IsAudio(driver) && ids is not null
            && ((ids.Bus == "HDAUDIO" && ids.VendorId == RealtekPci) || (ids.Bus == "USB" && ids.VendorId == RealtekUsb)
                || (ids.Bus == "PCI" && ids.VendorId == DriverDeviceMapper.IntelVendorId && Has(name, "Smart Sound"))))
        {
            return DeviceMakerKind.AudioFromPcMaker;
        }

        return DeviceMakerKind.None;
    }

    /// <summary>The offer for a device, pointing at <paramref name="makerPage"/> for the kinds that come from the PC or board maker.</summary>
    public static DriverOffer? OfferFor(DriverInfo driver, PlatformLink? makerPage, string? makerName)
    {
        ArgumentNullException.ThrowIfNull(driver);
        var who = makerName ?? "your PC maker";
        var source = makerName ?? "PC maker";
        var kind = KindOf(driver);
        var installed = driver.Version is null ? string.Empty : $" You have {driver.Version}.";
        var maker = makerPage?.Url ?? SupportPageBuilder.General(PcMaker.Unknown, who, string.Empty).Url;
        return kind switch
        {
            DeviceMakerKind.IntelEthernet => Page(driver, "Intel", "Intel Ethernet driver", IntelEthernetPage,
                "Intel's network adapter driver for Windows 11. Check the version on Intel's site." + installed),
            DeviceMakerKind.Killer => Page(driver, "Intel", "Intel Killer Performance Suite", KillerPage,
                "Killer is part of Intel now; its drivers and the Killer suite are on Intel's site." + installed),
            DeviceMakerKind.RealtekEthernet => Page(driver, "Realtek", "Realtek Ethernet driver", RealtekEthernetPage,
                "Realtek's page lists the Windows 11 driver for its PCIe Ethernet chips. Check the version there." + installed),
            DeviceMakerKind.WiFiFromPcMaker => Page(driver, source, $"Wi-Fi driver from {who}", maker,
                "Wi-Fi from this chip maker comes through your PC or board maker (or Windows Update), not a public download." + installed),
            DeviceMakerKind.BluetoothFromPcMaker => Page(driver, source, $"Bluetooth driver from {who}", maker,
                "Bluetooth from this chip maker comes through your PC or board maker (or Windows Update)." + installed),
            DeviceMakerKind.AudioFromPcMaker => Page(driver, source, $"Audio driver from {who}", maker,
                "Use your board or PC maker's audio driver: it has the jack, microphone and effects settings for your model. Generic Realtek drivers can lose them." + installed),
            _ => null,
        };
    }

    private static DriverOffer Page(DriverInfo driver, string source, string title, string url, string note) =>
        new("maker:" + driver.DeviceId, DriverSourceKind.DeviceMakers, title, null, DriverVersionScheme.None, null, DriverOfferAction.OpenPage)
        {
            Url = url,
            FallbackPageUrl = url,
            Note = note,
            SourceName = source,
        };

    /// <summary>The codec maker of an HD Audio function ("HDAUDIO\FUNC_01&amp;VEN_10EC&amp;DEV_0897" → 10EC).</summary>
    public static HardwareIdParts? HdAudioCodec(DriverInfo driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        foreach (var id in driver.HardwareIds.Prepend(driver.DeviceId))
        {
            var m = HdAudioId().Match(id ?? string.Empty);
            if (m.Success)
            {
                return new HardwareIdParts("HDAUDIO", m.Groups["ven"].Value.ToUpperInvariant(), m.Groups["dev"].Value.ToUpperInvariant(), null);
            }
        }

        return null;
    }

    [GeneratedRegex(@"^HDAUDIO\\FUNC_\d{2}&VEN_(?<ven>[0-9A-F]{4})&DEV_(?<dev>[0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HdAudioId();

    private static bool IsAudio(DriverInfo driver) => Is(driver.DeviceClass, "MEDIA") || Is(driver.DeviceClass, "AudioEndpoint");

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Has(string? text, string value) => text is not null && text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
