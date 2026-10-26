namespace PcChecker.Core.Health;

/// <summary>Pure lookup tables for the WMI codes the health analyzers read.</summary>
public static class HealthCodes
{
    /// <summary>Win32_Battery.Chemistry.</summary>
    public static string BatteryChemistry(int? code) => code switch
    {
        3 => "Lead acid",
        4 => "Nickel cadmium",
        5 => "Nickel metal hydride",
        6 => "Lithium-ion",
        7 => "Zinc air",
        8 => "Lithium polymer",
        1 => "Other",
        _ => "Unknown",
    };

    /// <summary>Win32_Battery.BatteryStatus.</summary>
    public static string BatteryStatus(int? code) => code switch
    {
        1 => "Discharging",
        2 => "Plugged in",
        3 => "Fully charged",
        4 => "Low",
        5 => "Critical",
        6 => "Charging",
        7 => "Charging (high)",
        8 => "Charging (low)",
        9 => "Charging (critical)",
        11 => "Partially charged",
        _ => "Unknown",
    };

    /// <summary>MSFT_PhysicalDisk.MediaType.</summary>
    public static string MediaType(int? code) => code switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "Unspecified",
    };

    /// <summary>MSFT_PhysicalDisk.BusType.</summary>
    public static string BusType(int? code) => code switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        _ => "Unknown",
    };

    /// <summary>MSFT_PhysicalDisk.HealthStatus.</summary>
    public static string PhysicalDiskHealth(int? code) => code switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown",
    };

    /// <summary>Guesses SSD/HDD from the Win32_DiskDrive model and interface when MSFT_PhysicalDisk is unavailable.</summary>
    public static string GuessMediaType(string? model, string? interfaceType, int? spindleSpeed)
    {
        if (spindleSpeed is 0)
        {
            return "SSD";
        }

        var m = model ?? string.Empty;
        if (m.Contains("NVMe", StringComparison.OrdinalIgnoreCase) || m.Contains("SSD", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Solid State", StringComparison.OrdinalIgnoreCase) || m.Contains("M.2", StringComparison.OrdinalIgnoreCase))
        {
            return "SSD";
        }

        if (string.Equals(interfaceType, "USB", StringComparison.OrdinalIgnoreCase))
        {
            return "External";
        }

        return "HDD";
    }
}
