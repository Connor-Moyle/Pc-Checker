using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;

namespace PcChecker.Core.Drivers;

/// <summary>Pure rules for flagging drivers and building vendor support links.</summary>
public static class DriverRules
{
    public const int OldDriverDays = 365 * 3;

    /// <summary>Deep link to the Windows Update page that lists optional driver updates.</summary>
    public const string OptionalUpdatesUri = "ms-settings:windowsupdate-optionalupdates";

    /// <summary>Device Manager snap-in.</summary>
    public const string DeviceManagerCommand = "devmgmt.msc";

    public sealed record Flag(Severity Severity, string Title, string Detail);

    /// <summary>Returns the issues found for one driver (empty when it looks fine).</summary>
    public static IReadOnlyList<Flag> Evaluate(DriverInfo driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        var flags = new List<Flag>();

        if (driver.HasProblem)
        {
            var d = DeviceProblemCodes.Describe(driver.ProblemCode);
            flags.Add(new Flag(driver.ProblemCode is 22 or 45 ? Severity.Low : Severity.High,
                $"Device problem (code {driver.ProblemCode})", $"{d.Title} {d.Suggestion}"));
        }

        if (!driver.IsSigned)
        {
            flags.Add(new Flag(Severity.Medium, "Unsigned driver",
                "This driver has no valid digital signature, so Windows cannot verify who made it."));
        }

        if (driver.AgeDays is { } age && age >= OldDriverDays && !driver.IsInbox && !IsAmdChipsetComponent(driver))
        {
            flags.Add(new Flag(Severity.Low, "Old driver",
                $"This driver is about {age / 365} years old. A newer version may be available from the manufacturer."));
        }

        return flags;
    }

    /// <summary>True when a driver is worth showing in an "attention" list.</summary>
    public static bool NeedsAttention(DriverInfo driver) => Evaluate(driver).Count > 0;

    /// <summary>Third-party drivers older than <see cref="OldDriverDays"/>. Inbox drivers are never "old": Windows services them.</summary>
    public static bool IsOld(DriverInfo driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return !driver.IsInbox && driver.AgeDays is { } age && age >= OldDriverDays && !IsAmdChipsetComponent(driver);
    }

    /// <summary>
    /// AMD System-class drivers (SMBus, GPIO, I2C, PCI, UART, PSP…) ship inside AMD Chipset Software, which keeps
    /// component drivers with old INF dates for years. They are not "old": the whole chipset package is updated
    /// from amd.com, never a single component.
    /// </summary>
    public static bool IsAmdChipsetComponent(DriverInfo driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        bool system = string.Equals(driver.DeviceClass?.Trim(), "System", StringComparison.OrdinalIgnoreCase)
            || string.Equals(driver.ClassGuid?.Trim('{', '}', ' '), "4d36e97d-e325-11ce-bfc1-08002be10318", StringComparison.OrdinalIgnoreCase);
        return system && AmdPlatformRules.IsAmdProvider(driver.Provider);
    }

    /// <summary>The plain-English note shown for an AMD chipset component instead of "Old driver".</summary>
    public const string AmdChipsetComponentNote =
        "Part of AMD Chipset Software. AMD keeps some component drivers unchanged for years; update the whole chipset package from amd.com instead of this one driver.";

    /// <summary>Third-party drivers that were staged into the driver store (oemN.inf) can be removed with pnputil.</summary>
    public static bool CanRollBack(DriverInfo driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        return !driver.IsInbox && DriverInfoBuilder.IsOemInf(driver.InfName);
    }

    /// <summary>
    /// Why removing this device's third-party package is refused even though <see cref="CanRollBack"/> allows it, or
    /// null when it may go ahead. Removing a package takes it off every device that uses it, so it is refused for the
    /// classes Windows needs to start and to reach disks, keyboard and mouse (a RAID or VMD storage controller has no
    /// inbox fallback and the PC would no longer boot), and for packages other connected devices also use (a chipset
    /// package typically drives dozens of system devices).
    /// </summary>
    public static string? RollbackBlockReason(DriverInfo driver, IEnumerable<DriverInfo> allDrivers)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(allDrivers);
        const string deviceManager = "Use Device Manager → Properties → Driver → Roll Back Driver for this device instead.";
        if (DriverCleanupRules.IsProtectedClass(driver.DeviceClass, driver.ClassGuid))
        {
            return $"{driver.DeviceName} is a kind of device Windows needs to start or to reach your disks, keyboard and mouse ({driver.DeviceClass}). "
                + $"Removing its driver package could leave Windows unable to start, so PC Checker does not do it. {deviceManager}";
        }

        var inf = driver.InfName?.Trim();
        if (string.IsNullOrEmpty(inf))
        {
            return null;
        }

        var others = allDrivers
            .Where(d => d.IsPresent
                && !string.Equals(d.DeviceId, driver.DeviceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.InfName?.Trim(), inf, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.DeviceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (others.Count == 0)
        {
            return null;
        }

        var names = string.Join(", ", others.Take(3)) + (others.Count > 3 ? $" and {others.Count - 3} more" : string.Empty);
        return $"{inf} also drives {others.Count} other connected device(s) ({names}). Removing it would take the driver off all of them, so PC Checker does not do it. {deviceManager}";
    }

    /// <summary>
    /// Vendor download pages for the machine described by a hardware inventory: GPU vendors, CPU vendor (chipset),
    /// motherboard and system manufacturer, plus storage and peripheral makers. Empty when nothing is known.
    /// </summary>
    public static IReadOnlyList<VendorLink> VendorLinksFor(HardwareInventory? inventory)
    {
        if (inventory is null)
        {
            return [];
        }

        var names = new List<string?>();
        foreach (var gpu in inventory.Graphics)
        {
            // An AMD GPU needs AMD Software (Radeon), not the chipset page; the CPU decides the chipset link below.
            if (Overclocking.GpuVendorDetector.Detect(gpu) == Overclocking.GpuVendor.Amd)
            {
                names.Add("Radeon");
                continue;
            }

            names.Add(gpu.Vendor);
            names.Add(gpu.Manufacturer);
            names.Add(gpu.Name);
        }

        foreach (var cpu in inventory.Processors)
        {
            names.Add(cpu.Manufacturer);
            names.Add(cpu.Name);
        }

        names.Add(inventory.Motherboard?.Manufacturer);
        names.Add(inventory.Motherboard?.Name);
        names.Add(inventory.System?.Manufacturer);
        foreach (var disk in inventory.PhysicalDisks)
        {
            names.Add(disk.Manufacturer);
            names.Add(disk.Name);
        }

        foreach (var nic in inventory.NetworkAdapters)
        {
            names.Add(nic.Manufacturer);
        }

        foreach (var audio in inventory.AudioDevices)
        {
            names.Add(audio.Manufacturer);
        }

        foreach (var input in inventory.InputDevices)
        {
            names.Add(input.Manufacturer);
        }

        var links = BuildVendorLinks(names).ToList();
        var cpuName = inventory.Processors.FirstOrDefault()?.Name;
        for (int i = 0; i < links.Count; i++)
        {
            if (links[i].Vendor == "AMD" && links[i].Component == AmdChipsetComponent)
            {
                links[i] = links[i] with { Note = AmdPlatformRules.ChipsetHint(cpuName) };
            }
        }

        return links;
    }

    public const string AmdChipsetComponent = "Chipset";
    public const string AmdGraphicsComponent = "Graphics (AMD Software: Adrenalin Edition)";

    /// <summary>Known vendor download pages keyed by a case-insensitive vendor keyword.</summary>
    public static IReadOnlyList<(string Keyword, string Vendor, string Component, string Url, string Note)> VendorTable { get; } =
    [
        ("nvidia", "NVIDIA", "Graphics", "https://www.nvidia.com/Download/index.aspx", "GeForce drivers are also available through the NVIDIA app."),
        ("radeon", "AMD", AmdGraphicsComponent, AmdPlatformRules.ChipsetDownloadUrl,
            "Install Radeon drivers with AMD Software: Adrenalin Edition. When switching between NVIDIA and AMD, remove the old driver first with the AMD Cleanup Utility or Display Driver Uninstaller."),
        ("amd", "AMD", AmdChipsetComponent, AmdPlatformRules.ChipsetDownloadUrl, "AMD Chipset Software: use AMD's Auto-Detect and Install tool or pick your socket (AM4/AM5)."),
        ("advanced micro devices", "AMD", AmdChipsetComponent, AmdPlatformRules.ChipsetDownloadUrl, "AMD Chipset Software: use AMD's Auto-Detect and Install tool or pick your socket (AM4/AM5)."),
        ("intel", "Intel", "Graphics / Chipset / Network", "https://www.intel.com/content/www/us/en/support/detect.html", "The Intel Driver & Support Assistant detects your hardware."),
        ("realtek", "Realtek", "Audio / Network", "https://www.realtek.com/en/downloads", "Prefer the driver from your motherboard or laptop maker."),
        ("asus", "ASUS", "Motherboard / Laptop", "https://www.asus.com/support/download-center/", "Search for your exact model."),
        ("msi", "MSI", "Motherboard / Laptop", "https://www.msi.com/support/download", "Search for your exact model."),
        ("gigabyte", "GIGABYTE", "Motherboard", "https://www.gigabyte.com/Support", "Search for your exact model."),
        ("asrock", "ASRock", "Motherboard", "https://www.asrock.com/support/index.asp", "Search for your exact model."),
        ("dell", "Dell", "System", "https://www.dell.com/support/home/", "Use Dell SupportAssist or enter your Service Tag."),
        ("hp", "HP", "System", "https://support.hp.com/drivers", "Use HP Support Assistant or enter your model."),
        ("hewlett", "HP", "System", "https://support.hp.com/drivers", "Use HP Support Assistant or enter your model."),
        ("lenovo", "Lenovo", "System", "https://support.lenovo.com/", "Use Lenovo Vantage or enter your serial number."),
        ("acer", "Acer", "System", "https://www.acer.com/support", "Search for your model."),
        ("microsoft", "Microsoft", "Surface", "https://support.microsoft.com/surface", "Surface drivers arrive through Windows Update."),
        ("samsung", "Samsung", "Storage", "https://semiconductor.samsung.com/consumer-storage/support/tools/", "Samsung Magician keeps SSD firmware current."),
        ("western digital", "Western Digital", "Storage", "https://support.wdc.com/", "WD Dashboard checks SSD firmware."),
        ("seagate", "Seagate", "Storage", "https://www.seagate.com/support/", "SeaTools checks drive firmware."),
        ("logitech", "Logitech", "Peripherals", "https://support.logi.com/", "Logitech G HUB or Options+ manage device software."),
        ("qualcomm", "Qualcomm", "Network", "https://www.qualcomm.com/support", "Prefer the driver from your laptop maker."),
        ("mediatek", "MediaTek", "Network", "https://www.mediatek.com/support", "Prefer the driver from your laptop maker."),
        ("broadcom", "Broadcom", "Network", "https://www.broadcom.com/support/download-search", "Prefer the driver from your laptop maker."),
    ];

    /// <summary>Builds vendor links from the names of the GPU, CPU, motherboard and system manufacturers.</summary>
    public static IReadOnlyList<VendorLink> BuildVendorLinks(IEnumerable<string?> manufacturerNames)
    {
        var links = new List<VendorLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in manufacturerNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            foreach (var entry in VendorTable)
            {
                if (name.Contains(entry.Keyword, StringComparison.OrdinalIgnoreCase) && seen.Add(entry.Vendor + "|" + entry.Component))
                {
                    links.Add(new VendorLink(entry.Vendor, entry.Component, entry.Url, entry.Note));
                    break;
                }
            }
        }

        return links;
    }
}
