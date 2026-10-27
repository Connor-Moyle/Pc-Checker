using System.Globalization;

namespace PcChecker.Core.Storage;

/// <summary>Display text for drive facts: capacity in decimal and binary units, link speeds, partition styles.</summary>
public static class DriveText
{
    private static readonly string[] DecimalUnits = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly string[] BinaryUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <summary>
    /// "4.00 TB (3.64 TiB)". Drive makers count 1 TB as 1,000,000,000,000 bytes; Windows Explorer counts in 1024s
    /// (and labels it "TB"), which is why a 4 TB drive shows as 3.63 "TB" there. Both are shown, correctly labelled.
    /// </summary>
    public static string Capacity(long bytes)
    {
        if (bytes <= 0)
        {
            return "Unknown size";
        }

        return $"{Decimal(bytes)} ({Binary(bytes)})";
    }

    /// <summary>Decimal units (1 GB = 1,000,000,000 bytes), as printed on the box.</summary>
    public static string Decimal(long bytes) => Scale(bytes, 1000d, DecimalUnits);

    /// <summary>Binary units (1 GiB = 1,073,741,824 bytes), as Windows Explorer counts.</summary>
    public static string Binary(long bytes) => Scale(bytes, 1024d, BinaryUnits);

    /// <summary>The negotiated SATA speed from ATA IDENTIFY word 77 bits 1–3 (1 = 1.5, 2 = 3, 3 = 6 Gb/s).</summary>
    public static string? SataSpeed(int? generation) => generation switch
    {
        1 => "1.5 Gb/s",
        2 => "3 Gb/s",
        3 => "6 Gb/s",
        _ => null,
    };

    /// <summary>
    /// "SATA 6 Gb/s", or "SATA 3 Gb/s (drive supports 6 Gb/s)" when the link runs slower than the drive can, which
    /// usually means an old port, a SATA 2 cable or a bad connection.
    /// </summary>
    public static string? SataLink(int? currentGeneration, int? maxGeneration)
    {
        var current = SataSpeed(currentGeneration);
        if (current is null)
        {
            return SataSpeed(maxGeneration) is { } max ? $"SATA (drive supports {max})" : null;
        }

        return maxGeneration is { } m && m > currentGeneration && SataSpeed(m) is { } best
            ? $"SATA {current} (drive supports {best})"
            : $"SATA {current}";
    }

    /// <summary>PCIe generation from DEVPKEY_PciDevice_CurrentLinkSpeed (1 = 2.5 GT/s … 5 = 32 GT/s).</summary>
    public static string? PcieGeneration(int? speedCode) => speedCode switch
    {
        1 => "1.0",
        2 => "2.0",
        3 => "3.0",
        4 => "4.0",
        5 => "5.0",
        6 => "6.0",
        _ => null,
    };

    /// <summary>"PCIe 4.0 x4", or "PCIe 3.0 x4 (drive supports PCIe 4.0 x4)" when the slot limits the drive.</summary>
    public static string? PcieLink(int? currentSpeed, int? currentWidth, int? maxSpeed, int? maxWidth)
    {
        var gen = PcieGeneration(currentSpeed);
        if (gen is null)
        {
            return null;
        }

        string now = currentWidth is > 0 ? $"PCIe {gen} x{currentWidth}" : $"PCIe {gen}";
        bool slower = (maxSpeed is { } ms && ms > currentSpeed) || (maxWidth is { } mw && currentWidth is { } cw && mw > cw);
        if (!slower)
        {
            return now;
        }

        var maxGen = PcieGeneration(maxSpeed ?? currentSpeed) ?? gen;
        string best = (maxWidth ?? currentWidth) is > 0 ? $"PCIe {maxGen} x{maxWidth ?? currentWidth}" : $"PCIe {maxGen}";
        return $"{now} (drive supports {best})";
    }

    /// <summary>MSFT_Disk.PartitionStyle (0 RAW, 1 MBR, 2 GPT) → text, with "Dynamic" added for LDM disks.</summary>
    public static string PartitionStyle(int? code, bool dynamic) => (code, dynamic) switch
    {
        (1, true) => "MBR (dynamic disk)",
        (2, true) => "GPT (dynamic disk)",
        (1, false) => "MBR",
        (2, false) => "GPT",
        (0, _) => "Not initialized",
        _ => dynamic ? "Dynamic disk" : "Unknown",
    };

    /// <summary>"12,345 hours (about 1.4 years)".</summary>
    public static string Hours(long hours)
    {
        string h = hours.ToString("N0", CultureInfo.InvariantCulture);
        if (hours < 24 * 60)
        {
            return $"{h} hours";
        }

        double years = hours / (24d * 365.25);
        return years >= 1
            ? $"{h} hours (about {years.ToString("0.#", CultureInfo.InvariantCulture)} years)"
            : $"{h} hours (about {(hours / 24d / 30.44).ToString("0", CultureInfo.InvariantCulture)} months)";
    }

    private static string Scale(long bytes, double step, string[] units)
    {
        double value = bytes;
        int unit = 0;
        while (value >= step && unit < units.Length - 1)
        {
            value /= step;
            unit++;
        }

        string format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
