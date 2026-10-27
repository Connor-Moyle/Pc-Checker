namespace PcChecker.Core.Drivers.Sources;

/// <summary>
/// Intel's official download pages for the products PC Checker maps devices to. Intel publishes no public version
/// feed for its Download Center (its Driver &amp; Support Assistant uses a private service), so Intel devices get a
/// link to the exact product page and the version shown there is compared by the user. Pure.
/// </summary>
public static class IntelDriverCatalog
{
    public const string ArcAndIrisXeGraphicsPage = "https://www.intel.com/content/www/us/en/download/785597/intel-arc-iris-xe-graphics-windows.html";
    public const string LegacyGraphicsPage = "https://www.intel.com/content/www/us/en/download/776137/intel-7th-10th-gen-processor-graphics-windows.html";
    public const string WiFiPage = "https://www.intel.com/content/www/us/en/download/19351/intel-wireless-wi-fi-drivers-for-windows-10-and-windows-11.html";
    public const string BluetoothPage = "https://www.intel.com/content/www/us/en/download/18649/intel-wireless-bluetooth-drivers-for-windows-10-and-windows-11.html";
    public const string SupportAssistantPage = "https://www.intel.com/content/www/us/en/support/intel-driver-support-assistant.html";

    /// <summary>The product page and its title for a mapped product; null for <see cref="IntelProduct.None"/>.</summary>
    public static (string Title, string Url)? PageFor(IntelProduct product) => product switch
    {
        IntelProduct.ArcAndIrisXeGraphics => ("Intel Arc & Iris Xe Graphics driver", ArcAndIrisXeGraphicsPage),
        IntelProduct.LegacyGraphics => ("Intel 7th–10th Gen Processor Graphics driver", LegacyGraphicsPage),
        IntelProduct.WiFi => ("Intel Wireless Wi-Fi driver", WiFiPage),
        IntelProduct.Bluetooth => ("Intel Wireless Bluetooth driver", BluetoothPage),
        _ => null,
    };
}
