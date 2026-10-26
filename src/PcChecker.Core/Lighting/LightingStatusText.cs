namespace PcChecker.Core.Lighting;

/// <summary>Plain-English status lines for the Lighting page. Pure so the wording is unit-testable.</summary>
public static class LightingStatusText
{
    public const string DownloadUrl = "https://openrgb.org/";
    public const string NotRunning = "OpenRGB not running — start it with the SDK server enabled";
    public const string NotInstalled = "OpenRGB not installed — download from openrgb.org";
    public const string Disconnected = "Disconnected from OpenRGB";
    public const string Connecting = "Connecting to OpenRGB…";

    /// <summary>"Connected to OpenRGB x.y (N devices)". <paramref name="version"/> may be a protocol version text.</summary>
    public static string Connected(string? version, int deviceCount)
    {
        string devices = deviceCount == 1 ? "1 device" : $"{deviceCount} devices";
        return string.IsNullOrWhiteSpace(version)
            ? $"Connected to OpenRGB ({devices})"
            : $"Connected to OpenRGB {version.Trim()} ({devices})";
    }

    /// <summary>Picks the right line for the current state.</summary>
    public static string For(bool connected, bool installed, string? version, int deviceCount, string? error = null)
    {
        if (connected)
        {
            return Connected(version, deviceCount);
        }

        if (!installed)
        {
            return NotInstalled;
        }

        return string.IsNullOrWhiteSpace(error) ? NotRunning : $"{NotRunning} ({error.Trim()})";
    }

    /// <summary>One-line explanation shown under "Why OpenRGB?".</summary>
    public const string WhyOpenRgb =
        "OpenRGB is a free, open-source program that talks to hundreds of RGB products from different brands — motherboards, RAM, graphics cards, coolers, keyboards, mice and LED strips — through one interface. "
        + "PC Checker connects to its SDK server so you can set colours and effects for everything in one place instead of running a separate app for each brand.";

    /// <summary>Vendor suites users can fall back to when OpenRGB does not support a product.</summary>
    public static IReadOnlyList<(string Vendor, string Product, string Url)> VendorSoftware { get; } =
    [
        ("ASUS", "Armoury Crate", "https://rog.asus.com/armoury-crate/"),
        ("MSI", "MSI Center", "https://www.msi.com/Landing/MSI-Center"),
        ("Gigabyte", "Gigabyte Control Center", "https://www.gigabyte.com/Consumer/Software/GIGABYTE-Control-Center"),
        ("ASRock", "Polychrome RGB", "https://www.asrock.com/microsite/PolychromeRGB/"),
        ("Corsair", "iCUE", "https://www.corsair.com/icue"),
        ("Razer", "Synapse", "https://www.razer.com/synapse"),
        ("Logitech", "G HUB", "https://www.logitechg.com/innovation/g-hub.html"),
        ("SteelSeries", "GG", "https://steelseries.com/gg"),
        ("NZXT", "CAM", "https://nzxt.com/software/cam"),
    ];
}
