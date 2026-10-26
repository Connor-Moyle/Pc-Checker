namespace PcChecker.Core.Health;

/// <summary>Plain-English descriptions and suggestions for Device Manager (Configuration Manager) problem codes.</summary>
public static class DeviceProblemCodes
{
    public sealed record Description(int Code, string Title, string Suggestion);

    private static readonly Dictionary<int, Description> Codes = new()
    {
        [1] = new(1, "This device is not configured correctly.", "Update the driver from Device Manager or install the manufacturer's driver."),
        [2] = new(2, "The driver for this device might be corrupted.", "Reinstall the driver."),
        [3] = new(3, "The driver for this device might be corrupted, or the system may be low on memory.", "Close other programs, then reinstall the driver."),
        [4] = new(4, "This device is not working properly; one of its drivers or the registry may be corrupted.", "Reinstall the driver."),
        [5] = new(5, "The driver for this device needs a resource that Windows cannot manage.", "Update the driver or check the BIOS settings for the device."),
        [6] = new(6, "The boot configuration for this device conflicts with other devices.", "Check for resource conflicts in Device Manager."),
        [7] = new(7, "Cannot filter.", "Reinstall the driver."),
        [8] = new(8, "The driver loader for the device is missing.", "Reinstall the driver."),
        [9] = new(9, "Windows cannot identify this hardware because it does not have a valid hardware ID.", "Contact the hardware manufacturer; the device may be faulty."),
        [10] = new(10, "This device cannot start.", "Try a different port or cable, then update or reinstall the driver."),
        [11] = new(11, "This device failed.", "Reinstall the driver or replace the device."),
        [12] = new(12, "This device cannot find enough free resources that it can use.", "Disable another device that uses the same resources, or move the card to another slot."),
        [13] = new(13, "This device cannot work properly until you restart your computer.", "Restart the computer."),
        [14] = new(14, "This device cannot work properly until you restart your computer.", "Restart the computer."),
        [15] = new(15, "This device is causing a resource conflict.", "Check for resource conflicts in Device Manager."),
        [16] = new(16, "Windows cannot identify all the resources this device uses.", "Enter the required resource settings manually or update the driver."),
        [17] = new(17, "This device is asking for an unknown resource type.", "Reinstall the driver."),
        [18] = new(18, "Reinstall the drivers for this device.", "Uninstall the device, then scan for hardware changes."),
        [19] = new(19, "Windows cannot start this hardware device because its configuration information in the registry is incomplete or damaged.", "Uninstall and reinstall the device; if that fails, restore a restore point."),
        [20] = new(20, "Windows could not load one of the drivers for this device.", "Reinstall the driver."),
        [21] = new(21, "Windows is removing this device.", "Wait a few seconds, then refresh Device Manager; restart if it persists."),
        [22] = new(22, "This device is disabled.", "Enable the device in Device Manager if you need it."),
        [23] = new(23, "The system failure for this device is unknown.", "Reinstall the driver."),
        [24] = new(24, "This device is not present, is not working properly, or does not have all its drivers installed.", "Check the connection and reinstall the driver."),
        [25] = new(25, "Windows is still setting up this device.", "Restart the computer."),
        [26] = new(26, "Windows is still setting up this device.", "Restart the computer."),
        [27] = new(27, "This device does not have valid log configuration.", "Reinstall the driver."),
        [28] = new(28, "The drivers for this device are not installed.", "Install the manufacturer's driver, or let Windows Update find one."),
        [29] = new(29, "This device is disabled because the firmware of the device did not give it the required resources.", "Enable the device in the BIOS/UEFI settings."),
        [30] = new(30, "This device is using an interrupt resource that another device is using.", "Resolve the conflict in Device Manager."),
        [31] = new(31, "This device is not working properly because Windows cannot load the drivers required for this device.", "Update or reinstall the driver."),
        [32] = new(32, "A driver (service) for this device has been disabled.", "Re-enable the related service or reinstall the driver."),
        [33] = new(33, "Windows cannot determine which resources are required for this device.", "Configure the device through its own setup program or the BIOS."),
        [34] = new(34, "Windows cannot determine the settings for this device.", "Set the device's resources manually using the manufacturer's documentation."),
        [35] = new(35, "Your computer's system firmware does not include enough information to properly configure and use this device.", "Update the BIOS/UEFI firmware."),
        [36] = new(36, "This device is requesting a PCI interrupt but is configured for an ISA interrupt (or the reverse).", "Change the interrupt settings in the BIOS."),
        [37] = new(37, "Windows cannot initialize the device driver for this hardware.", "Reinstall the driver."),
        [38] = new(38, "Windows cannot load the device driver for this hardware because a previous instance of the device driver is still in memory.", "Restart the computer."),
        [39] = new(39, "Windows cannot load the device driver for this hardware. The driver may be corrupted or missing.", "Reinstall the driver."),
        [40] = new(40, "Windows cannot access this hardware because its service key information in the registry is missing or recorded incorrectly.", "Reinstall the driver."),
        [41] = new(41, "Windows successfully loaded the device driver for this hardware but cannot find the hardware device.", "Check that the device is connected and powered, then reinstall the driver."),
        [42] = new(42, "Windows cannot load the device driver for this hardware because there is a duplicate device already running in the system.", "Restart the computer."),
        [43] = new(43, "Windows has stopped this device because it has reported problems.", "Reconnect the device, try another port, update the driver; the device may be faulty."),
        [44] = new(44, "An application or service has shut down this hardware device.", "Restart the computer."),
        [45] = new(45, "Currently, this hardware device is not connected to the computer.", "Reconnect the device; this entry is harmless if the device is no longer used."),
        [46] = new(46, "Windows cannot gain access to this hardware device because the operating system is in the process of shutting down.", "No action needed; it should work after the next start."),
        [47] = new(47, "Windows cannot use this hardware device because it has been prepared for safe removal, but it has not been removed from the computer.", "Unplug the device and plug it in again, or restart."),
        [48] = new(48, "The software for this device has been blocked from starting because it is known to have problems with Windows.", "Get an updated driver from the manufacturer."),
        [49] = new(49, "Windows cannot start new hardware devices because the system hive is too large.", "Uninstall devices you no longer use."),
        [50] = new(50, "Windows cannot apply all of the properties for this device.", "Reinstall the driver."),
        [51] = new(51, "This device is currently waiting on another device or set of devices to start.", "Fix the device it depends on, then restart."),
        [52] = new(52, "Windows cannot verify the digital signature for the drivers required for this device.", "Install a signed driver from the manufacturer."),
        [53] = new(53, "This device has been reserved for use by the Windows kernel debugger for the duration of this boot session.", "Disable kernel debugging if you do not need it."),
        [54] = new(54, "This device has failed and is undergoing a reset.", "Wait, then restart if the device does not recover."),
    };

    /// <summary>All documented codes.</summary>
    public static IReadOnlyCollection<int> KnownCodes => Codes.Keys;

    public static bool IsKnown(int code) => Codes.ContainsKey(code);

    /// <summary>Returns a description for any code; unknown codes get a generic entry.</summary>
    public static Description Describe(int code)
    {
        if (code == 0)
        {
            return new Description(0, "This device is working properly.", "No action needed.");
        }

        return Codes.TryGetValue(code, out var d)
            ? d
            : new Description(code, $"Unknown problem code {code}.", "Check Device Manager for details and update the driver.");
    }
}
